using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

public sealed unsafe class BgPartRecreateExperimentService : IDisposable
{
    private const int ColliderOffset = 0x38;

    private readonly Dictionary<string, RecreatePathPin> pinnedPathBuffers = [];

    public string LastResult { get; private set; } = "BgPart recreate 实验尚未执行。";

    public bool SaveSnapshot(LocalLayoutObjectInstance instance, string targetPath)
    {
        var blockReason = this.GetSharedBlockReason(instance, targetPath);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return this.Fail(instance, $"slot 地址解析失败：{instance.OccupiedSlotAddress}");

        try
        {
            var bgPart = (BgPartsLayoutInstance*)pointer;
            var graphicsInfo = ReadGraphicsInfo(bgPart);
            var pin = this.PinTargetPath(instance, targetPath.Trim());

            instance.RecreateSnapshotGraphicsObject = graphicsInfo.GraphicsObjectAddress;
            instance.RecreateSnapshotIndexInPool = pointer->IndexInPool;
            instance.RecreateSnapshotTransform = ReadLayoutTransform(pointer);
            instance.RecreateSnapshotTransformMode = instance.TransformMode.ToString();
            instance.RecreateSnapshotColliderAddress = ReadColliderAddress(bgPart);
            instance.RecreateSnapshotOriginalPath = FirstNonEmpty(graphicsInfo.Path, instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.RecreateSnapshotTargetPath = targetPath.Trim();
            instance.RecreateSnapshotModelResourceHandle = graphicsInfo.ModelResourceHandleAddress;
            instance.RecreateAfterGraphicsObject = string.Empty;
            instance.RecreateAfterModelResourceHandle = string.Empty;
            instance.RecreateAfterVisible = string.Empty;
            instance.RecreateAfterTransform = string.Empty;
            instance.RecreateAfterColliderAddress = string.Empty;
            instance.RecreateLayoutRestoreResult = string.Empty;
            instance.RecreateVisualReapplyResult = string.Empty;
            instance.RecreateCollisionModeResult = string.Empty;
            instance.RecreateLastError = string.Empty;
            instance.RecreateLastResult =
                $"已保存 recreate 快照：GraphicsObject={graphicsInfo.GraphicsObjectAddress}; IndexInPool={instance.RecreateSnapshotIndexInPool}; " +
                $"mode={instance.TransformMode}; collider={instance.RecreateSnapshotColliderAddress}; original={instance.RecreateSnapshotOriginalPath}; " +
                $"target={instance.RecreateSnapshotTargetPath}; targetBuffer={instance.RecreatePinnedPathAddress}; " +
                $"pathPointer={instance.RecreatePathPointerAddress}; pinStable={pin.IsAllocated}";
            this.LastResult = instance.RecreateLastResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"保存 recreate 快照失败：{ex.Message}");
        }
    }

    public bool ExecuteDestroyCreate(
        LocalLayoutObjectInstance instance,
        string targetPath,
        bool unsafeEnabled,
        bool experimentEnabled,
        bool confirmed)
    {
        var blockReason = this.GetExecuteBlockReason(instance, targetPath, unsafeEnabled, experimentEnabled, confirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return this.Fail(instance, $"slot 地址解析失败：{instance.OccupiedSlotAddress}");

        try
        {
            if (!this.pinnedPathBuffers.ContainsKey(instance.Id) ||
                !string.Equals(instance.RecreateSnapshotTargetPath, targetPath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (!this.SaveSnapshot(instance, targetPath))
                    return false;
            }

            if (!this.pinnedPathBuffers.TryGetValue(instance.Id, out var pin) || !pin.IsAllocated)
                return this.Fail(instance, "target path buffer 未固定，禁止调用 CreatePrimary。");

            var transformPointer = pointer->GetTransformImpl();
            if (transformPointer == null)
                return this.Fail(instance, "GetTransformImpl 返回 null，无法传入 CreatePrimary。");

            var createTransform = instance.TransformMode == LocalLayoutTransformMode.VisualOnly
                ? BuildOriginalLayoutTransform(instance)
                : *transformPointer;

            var before = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
            var beforeCollider = ReadColliderAddress((BgPartsLayoutInstance*)pointer);

            pointer->DestroyPrimary();
            pointer->CreatePrimary(&createTransform, (void*)pin.PathPointerStorage);

            var after = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
            var afterCollider = ReadColliderAddress((BgPartsLayoutInstance*)pointer);

            if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            {
                this.ApplyVisualOnlyCollisionProtection(instance, pointer, ref after);
            }
            else
            {
                instance.HasCollisionMoved = true;
                instance.RecreateLayoutRestoreResult = "FullLayoutWithCollision：保留 CreatePrimary 使用的当前 Layout transform。";
                instance.RecreateVisualReapplyResult = "FullLayoutWithCollision：不单独写 Graphics.Scene.Object transform。";
                instance.RecreateCollisionModeResult =
                    $"FullLayoutWithCollision：当前只重建 primary graphics；target collision / CreateSecondary 尚未实现。Collider before={beforeCollider}; after={afterCollider}";
            }

            after = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
            instance.GraphicsObjectAddress = after.GraphicsObjectAddress;
            instance.ModelResourceHandleAddress = after.ModelResourceHandleAddress;
            instance.AfterModelPath = after.Path;
            instance.CurrentResourcePath = FirstNonEmpty(after.Path, instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.RecreateAfterGraphicsObject = after.GraphicsObjectAddress;
            instance.RecreateAfterModelResourceHandle = after.ModelResourceHandleAddress;
            instance.RecreateAfterVisible = after.Visible;
            instance.RecreateAfterTransform = after.Transform;
            instance.RecreateAfterColliderAddress = ReadColliderAddress((BgPartsLayoutInstance*)pointer);
            instance.RecreateLastError = string.Empty;

            if (!after.IsUsable)
            {
                instance.IsRenderInvalid = true;
                instance.ModelExperimentFailed = true;
                instance.TransformWriteDisabledReason = "DestroyPrimary -> CreatePrimary 后 GraphicsObject/ModelResourceHandle/visible 读回不可用；请切图/重载地图恢复。";
            }

            instance.RecreateLastResult =
                $"已执行 DestroyPrimary -> CreatePrimary：mode={instance.TransformMode}; beforeGraphics={before.GraphicsObjectAddress}; " +
                $"afterGraphics={after.GraphicsObjectAddress}; beforePath={before.Path}; target={targetPath.Trim()}; afterPath={after.Path}; " +
                $"visible={after.Visible}; transform={after.Transform}; {instance.RecreateCollisionModeResult}; renderInvalid={instance.IsRenderInvalid}";
            this.LastResult = instance.RecreateLastResult;
            return after.IsUsable;
        }
        catch (Exception ex)
        {
            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.TransformWriteDisabledReason = "recreate native 调用异常后实例视为 render 失效；请切图/重载地图恢复。";
            return this.Fail(instance, $"DestroyPrimary -> CreatePrimary 异常：{ex}");
        }
    }

    public string GetExecuteBlockReason(
        LocalLayoutObjectInstance? instance,
        string targetPath,
        bool unsafeEnabled,
        bool experimentEnabled,
        bool confirmed)
    {
        if (!unsafeEnabled)
            return "UnsafeMode=false。";
        if (!experimentEnabled)
            return "需要先启用 Debug-only BgPart recreate 高风险实验。";
        if (!confirmed)
            return "需要二次确认 DestroyPrimary -> CreatePrimary 高风险实验。";

        return this.GetSharedBlockReason(instance, targetPath);
    }

    public void Dispose()
    {
        foreach (var pin in this.pinnedPathBuffers.Values)
            pin.Dispose();
        this.pinnedPathBuffers.Clear();
    }

    private void ApplyVisualOnlyCollisionProtection(LocalLayoutObjectInstance instance, ILayoutInstance* pointer, ref GraphicsInfo after)
    {
        var originalLayout = BuildOriginalLayoutTransform(instance);
        var layoutRestored = WriteLayoutTransform(pointer, originalLayout, out var layoutReadback);
        instance.RecreateLayoutRestoreResult = layoutRestored
            ? $"VisualOnly：已恢复 LayoutInstance 到原始 slot transform；readback={layoutReadback}"
            : $"VisualOnly：恢复 LayoutInstance 原始 transform 失败；readback={layoutReadback}";

        after = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
        if (!TryParseAddress(after.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.RecreateVisualReapplyResult = "VisualOnly：CreatePrimary 后 GraphicsObject 无效，未写视觉 transform。";
            instance.RecreateCollisionModeResult = "VisualOnly：未调用 CreateSecondary，未写 Collider；但 GraphicsObject 无效。";
            return;
        }

        var visualApplied = WriteSceneObjectTransform(
            graphicsAddress,
            instance.CurrentPosition,
            NormalizeRotation(instance.CurrentRotation),
            NormalizeScale(instance.CurrentScale),
            out var visualReadback);

        instance.RecreateVisualReapplyResult = visualApplied
            ? $"VisualOnly：已重新应用 Graphics.Scene.Object Position/Rotation/Scale；readback={visualReadback}"
            : $"VisualOnly：重新应用 Graphics.Scene.Object transform 失败；readback={visualReadback}";
        instance.HasCollisionMoved = false;
        instance.RecreateCollisionModeResult =
            $"VisualOnly：未调用 CreateSecondary，未写 Collider，Layout transform 已恢复到原始 slot；本地脚下只移动 GraphicsObject。Collider={ReadColliderAddress((BgPartsLayoutInstance*)pointer)}";
    }

    private string GetSharedBlockReason(LocalLayoutObjectInstance? instance, string targetPath)
    {
        if (instance == null)
            return "未选中 LocalLayoutObjectInstance。";
        if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate)
            return "实例已失效、已恢复或是重复记录。";
        if (instance.IsRenderInvalid)
            return "实例 render 已失效；请切图/重载地图恢复后再实验。";
        if (string.IsNullOrWhiteSpace(instance.OccupiedSlotAddress))
            return "occupiedSlotAddress 为空。";
        if (string.IsNullOrWhiteSpace(targetPath))
            return "target path 为空。";

        targetPath = targetPath.Trim();
        if (!targetPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "target path 必须以 .mdl 结尾。";
        if (!targetPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase))
            return "本实验只允许 category=Bg 的 bg/...mdl 路径。";
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return $"slot 地址解析失败：{instance.OccupiedSlotAddress}";
        if (pointer->Id.Type != InstanceType.BgPart)
            return $"当前实例不是 BgPart：{pointer->Id.Type}";

        try
        {
            var graphicsObject = ((BgPartsLayoutInstance*)pointer)->GraphicsObject;
            if (graphicsObject == null)
                return "当前 GraphicsObject=null，无法保存完整快照。";

            if (graphicsObject->ModelResourceHandle == null)
                return "当前 ModelResourceHandle=null。";

            var category = (ResourceCategory)(ushort)graphicsObject->ModelResourceHandle->Type.Category;
            if (category != ResourceCategory.Bg)
                return $"当前 category={category} ({(int)category})，本实验仅允许 Bg。";
        }
        catch (Exception ex)
        {
            return $"读取当前 BgPart recreate 条件失败：{ex.Message}";
        }

        return string.Empty;
    }

    private bool Fail(LocalLayoutObjectInstance instance, string message)
    {
        instance.RecreateLastError = message;
        instance.RecreateLastResult = string.Empty;
        this.LastResult = message;
        return false;
    }

    private RecreatePathPin PinTargetPath(LocalLayoutObjectInstance instance, string targetPath)
    {
        if (this.pinnedPathBuffers.Remove(instance.Id, out var oldPin))
            oldPin.Dispose();

        var buffer = Encoding.UTF8.GetBytes(targetPath + '\0');
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var pathPointerStorage = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(pathPointerStorage, handle.AddrOfPinnedObject());

        instance.RecreateTargetPathBuffer = buffer;
        instance.RecreatePinnedPathAddress = $"0x{handle.AddrOfPinnedObject().ToInt64():X}";
        instance.RecreatePathPointerAddress = $"0x{pathPointerStorage.ToInt64():X}";

        var pin = new RecreatePathPin(handle, pathPointerStorage);
        this.pinnedPathBuffers[instance.Id] = pin;
        return pin;
    }

    private static Transform BuildOriginalLayoutTransform(LocalLayoutObjectInstance instance)
        => new()
        {
            Translation = instance.OriginalLayoutPosition == default ? instance.OccupiedSlotOriginalPosition : instance.OriginalLayoutPosition,
            Rotation = NormalizeRotation(instance.OriginalLayoutRotation == default ? instance.OccupiedSlotOriginalRotation : instance.OriginalLayoutRotation),
            Scale = NormalizeScale(instance.OriginalLayoutScale == default ? instance.OccupiedSlotOriginalScale : instance.OriginalLayoutScale),
        };

    private static bool WriteLayoutTransform(ILayoutInstance* pointer, Transform transform, out string readback)
    {
        readback = string.Empty;
        if (pointer == null)
        {
            readback = "pointer=null";
            return false;
        }

        pointer->SetTransform(&transform);
        readback = ReadLayoutTransform(pointer);
        return true;
    }

    private static bool WriteSceneObjectTransform(nint graphicsObjectAddress, Vector3 position, Quaternion rotation, Vector3 scale, out string readback)
    {
        readback = string.Empty;
        if (graphicsObjectAddress == 0)
        {
            readback = "graphicsObjectAddress=0";
            return false;
        }

        try
        {
            var obj = (SceneObject*)graphicsObjectAddress;
            var bg = (SceneBgObject*)graphicsObjectAddress;
            obj->Position = position;
            obj->Rotation = rotation;
            obj->Scale = scale;
            bg->IsTransformChanged = true;
            bg->NotifyTransformChanged();
            bg->UpdateTransforms(true);
            bg->UpdateRender();
            readback = $"position=({FormatVector(obj->Position)}), rotation={obj->Rotation}, scale=({FormatVector(obj->Scale)})";
            return true;
        }
        catch (Exception ex)
        {
            readback = $"写入失败：{ex.Message}";
            return false;
        }
    }

    private static string ReadLayoutTransform(ILayoutInstance* pointer)
    {
        var transform = pointer->GetTransformImpl();
        return transform == null
            ? "transform=null"
            : $"position=({FormatVector(transform->Translation)}), rotation={transform->Rotation}, scale=({FormatVector(transform->Scale)})";
    }

    private static GraphicsInfo ReadGraphicsInfo(BgPartsLayoutInstance* bgPart)
    {
        if (bgPart == null || bgPart->GraphicsObject == null)
            return new GraphicsInfo("0x0", "0x0", string.Empty, "False", "GraphicsObject=null", false);

        var bgObject = (SceneBgObject*)bgPart->GraphicsObject;
        var graphicsAddress = $"0x{(nint)bgObject:X}";
        var handleAddress = "0x0";
        var path = string.Empty;
        var usable = false;
        try
        {
            if (bgObject->ModelResourceHandle != null)
            {
                handleAddress = $"0x{(nint)bgObject->ModelResourceHandle:X}";
                path = bgObject->ModelResourceHandle->FileName.ToString();
                usable = bgObject->ModelResourceHandle->LoadState >= 7 && bgObject->IsVisible;
            }
        }
        catch
        {
        }

        var visible = SafeRead(() => bgObject->IsVisible.ToString());
        var transform = SafeRead(() => $"position=({FormatVector(bgObject->Position)}), rotation={bgObject->Rotation}, scale=({FormatVector(bgObject->Scale)})");
        return new GraphicsInfo(graphicsAddress, handleAddress, path, visible, transform, usable);
    }

    private static string ReadColliderAddress(BgPartsLayoutInstance* bgPart)
    {
        if (bgPart == null)
            return "0x0";

        try
        {
            var collider = *(nint*)((byte*)bgPart + ColliderOffset);
            return $"0x{collider:X}";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static bool TryGetPointer(string? raw, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(raw, out var address) || address == 0)
            return false;

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            address = (nint)hex;
            return true;
        }

        if (ulong.TryParse(raw, out var value))
        {
            address = (nint)value;
            return true;
        }

        return false;
    }

    private static Quaternion NormalizeRotation(Quaternion rotation)
        => rotation.LengthSquared() < 0.0001f ? Quaternion.Identity : Quaternion.Normalize(rotation);

    private static Vector3 NormalizeScale(Vector3 scale)
        => scale.LengthSquared() < 0.0001f ? Vector3.One : scale;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private readonly record struct GraphicsInfo(
        string GraphicsObjectAddress,
        string ModelResourceHandleAddress,
        string Path,
        string Visible,
        string Transform,
        bool IsUsable);

    private sealed class RecreatePathPin(GCHandle bufferHandle, nint pathPointerStorage) : IDisposable
    {
        public nint PathPointerStorage { get; } = pathPointerStorage;

        public bool IsAllocated => bufferHandle.IsAllocated && this.PathPointerStorage != 0;

        public void Dispose()
        {
            if (bufferHandle.IsAllocated)
                bufferHandle.Free();
            if (this.PathPointerStorage != 0)
                Marshal.FreeHGlobal(this.PathPointerStorage);
        }
    }
}
