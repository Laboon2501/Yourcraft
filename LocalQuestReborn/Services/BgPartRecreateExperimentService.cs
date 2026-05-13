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
    private const int CachedMatricesOffset = 0xA0;
    private const int StainOrBgChangeDataOffset = 0xA8;
    private const int CachedTransformOffset = 0xB0;
    private const int AnimationDataOffset = 0xB8;
    private const int PendingVisualTransformFrames = 3;
    private const float MaxReasonableCoordinate = 1_000_000f;

    private readonly Dictionary<string, RecreatePathPin> pinnedPathBuffers = [];

    public string LastResult { get; private set; } = "BgPart recreate 尚未执行。";

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
            var normalizedTargetPath = targetPath.Trim();
            var targetCategory = TryGetCategoryForPath(normalizedTargetPath, out var inferredTargetCategory)
                ? FormatCategory(inferredTargetCategory)
                : "Unknown";
            var pin = this.PinTargetPath(instance, normalizedTargetPath);

            instance.RecreateSnapshotGraphicsObject = graphicsInfo.GraphicsObjectAddress;
            instance.RecreateSnapshotIndexInPool = pointer->IndexInPool;
            instance.RecreateSnapshotTransform = ReadLayoutTransform(pointer);
            instance.RecreateSnapshotTransformMode = instance.TransformMode.ToString();
            instance.RecreateSnapshotColliderAddress = ReadColliderAddress(bgPart);
            instance.RecreateSnapshotOriginalPath = FirstNonEmpty(graphicsInfo.Path, instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.RecreateSnapshotTargetPath = normalizedTargetPath;
            instance.RecreateSnapshotModelResourceHandle = graphicsInfo.ModelResourceHandleAddress;
            instance.RecreateAfterGraphicsObject = string.Empty;
            instance.RecreateAfterModelResourceHandle = string.Empty;
            instance.RecreateAfterVisible = string.Empty;
            instance.RecreateAfterTransform = string.Empty;
            instance.RecreateAfterColliderAddress = string.Empty;
            instance.ModelResourceCategoryReadback = graphicsInfo.Category;
            instance.ModelResourceCategoryGuess = targetCategory;
            instance.ModelResourceCategoryConfidence = "before/target category 仅记录；允许 Bg 与 BgCommon 跨 category recreate。";
            instance.RecreateLayoutRestoreResult = string.Empty;
            instance.RecreateVisualReapplyResult = string.Empty;
            instance.RecreateCollisionModeResult = string.Empty;
            instance.RecreateLastError = string.Empty;
            instance.PendingRecreate = true;
            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformFrameWait = 0;
            instance.PendingVisualTransformResult = string.Empty;
            this.StoreGraphicsReadback(instance, graphicsInfo);

            instance.RecreateLastResult =
                $"已保存 recreate 快照：GraphicsObject={graphicsInfo.GraphicsObjectAddress}; IndexInPool={instance.RecreateSnapshotIndexInPool}; " +
                $"mode={instance.TransformMode}; collider={instance.RecreateSnapshotColliderAddress}; original={instance.RecreateSnapshotOriginalPath}; " +
                $"target={instance.RecreateSnapshotTargetPath}; beforeCategory={graphicsInfo.Category}; targetCategory={targetCategory}; " +
                $"targetBuffer={instance.RecreatePinnedPathAddress}; pathPointer={instance.RecreatePathPointerAddress}; pinStable={pin.IsAllocated}";
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
            var normalizedTargetPath = targetPath.Trim();
            if (!this.pinnedPathBuffers.ContainsKey(instance.Id) ||
                !string.Equals(instance.RecreateSnapshotTargetPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!this.SaveSnapshot(instance, normalizedTargetPath))
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
            var targetCategory = TryGetCategoryForPath(normalizedTargetPath, out var inferredTargetCategory)
                ? FormatCategory(inferredTargetCategory)
                : "Unknown";
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
                instance.PendingRecreate = false;
                instance.PendingVisualTransform = false;
                instance.HasCollisionMoved = true;
                instance.ModelApplyStatus = "StaticOk";
                instance.RecreateLayoutRestoreResult = "FullLayoutWithCollision：保留 CreatePrimary 使用的当前 Layout transform。";
                instance.RecreateVisualReapplyResult = "FullLayoutWithCollision：不单独写 Graphics.Scene.Object transform。";
                instance.RecreateCollisionModeResult =
                    $"FullLayoutWithCollision：只重建 primary graphics，target collision 由正式流程随后处理。Collider before={beforeCollider}; after={afterCollider}";
            }

            after = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
            this.StoreGraphicsReadback(instance, after);
            instance.GraphicsObjectAddress = after.GraphicsObjectAddress;
            instance.ModelResourceHandleAddress = after.ModelResourceHandleAddress;
            instance.AfterModelPath = after.Path;
            instance.CurrentResourcePath = FirstNonEmpty(after.Path, instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.RecreateAfterGraphicsObject = after.GraphicsObjectAddress;
            instance.RecreateAfterModelResourceHandle = after.ModelResourceHandleAddress;
            instance.RecreateAfterVisible = after.Visible;
            instance.RecreateAfterTransform = after.Transform;
            instance.RecreateAfterColliderAddress = ReadColliderAddress((BgPartsLayoutInstance*)pointer);
            instance.ModelResourceCategoryReadback = $"before={before.Category}; target={targetCategory}; after={after.Category}";
            instance.ModelResourceCategoryGuess = targetCategory;
            instance.ModelResourceCategoryConfidence = "CreatePrimary path recreate；允许当前实例 category 与 target category 不同。";
            instance.RecreateLastError = string.Empty;

        if (!after.IsUsable || !after.TransformValuesNormal)
        {
            if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            {
                instance.PendingRecreate = false;
                instance.PendingVisualTransform = true;
                instance.PendingVisualTransformFrameWait = PendingVisualTransformFrames;
                instance.PendingRecreateStabilizeAttempts = 0;
                instance.ModelApplyStatus = "PendingRecreateStabilize";
                instance.ApplyMdlStatus = "PendingRecreateStabilize";
                instance.InstanceState = "PendingRecreateStabilize";
                instance.PendingVisualTransformResult = $"recreate 后首帧暂不稳定，等待后续帧重试：{after.SafetyDump}";
                instance.RecreateLastError = string.Empty;
                this.LastResult = instance.PendingVisualTransformResult;
                return true;
            }

            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.ModelApplyStatus = FirstNonEmpty(instance.ModelApplyStatus, "UnsafeAfterRecreate");
            instance.ApplyMdlStatus = instance.ModelApplyStatus;
            instance.TransformWriteDisabledReason = "DestroyPrimary -> CreatePrimary 后 GraphicsObject/ModelResourceHandle/visible/transform 读回不安全；请切图或重载地图恢复。";
        }

            instance.RecreateLastResult =
                $"已执行 DestroyPrimary -> CreatePrimary：mode={instance.TransformMode}; beforeGraphics={before.GraphicsObjectAddress}; " +
                $"afterGraphics={after.GraphicsObjectAddress}; beforePath={before.Path}; target={normalizedTargetPath}; afterPath={after.Path}; " +
                $"beforeCategory={before.Category}; targetCategory={targetCategory}; afterCategory={after.Category}; visible={after.Visible}; " +
                $"loadState={after.LoadState}; transform={after.Transform}; {instance.RecreateCollisionModeResult}; " +
                $"pendingVisualTransform={instance.PendingVisualTransform}; renderInvalid={instance.IsRenderInvalid}; risk={instance.ComplexModelRisk}";
            this.LastResult = instance.RecreateLastResult;
            return after.IsUsable;
        }
        catch (Exception ex)
        {
            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.ModelApplyStatus = "Failed";
            instance.ApplyMdlStatus = "Failed";
            instance.TransformWriteDisabledReason = "recreate native 调用异常后实例视觉 render 失效；请切图/重载地图恢复。";
            return this.Fail(instance, $"DestroyPrimary -> CreatePrimary 异常：{ex}");
        }
    }

    public bool ProcessPendingVisualTransform(LocalLayoutObjectInstance instance)
    {
        if (!instance.PendingVisualTransform)
            return false;

        if (instance.IsInvalid || instance.IsRestored || instance.IsRenderInvalid)
        {
            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformResult = "实例已失效/已恢复/render invalid，取消待写 transform。";
            return false;
        }

        if (instance.PendingVisualTransformFrameWait > 0)
        {
            instance.PendingVisualTransformFrameWait--;
            instance.PendingVisualTransformResult = $"等待 recreate 后 GraphicsObject 稳定：剩余 {instance.PendingVisualTransformFrameWait} 帧。";
            return false;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return this.MarkUnsafeAfterRecreate(instance, $"slot 地址解析失败：{instance.OccupiedSlotAddress}");

        var graphicsInfo = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
        this.StoreGraphicsReadback(instance, graphicsInfo);
        instance.GraphicsObjectAddress = graphicsInfo.GraphicsObjectAddress;

        var risk = ClassifyModelRisk(FirstNonEmpty(graphicsInfo.Path, instance.TargetModelPath, instance.CustomModelPath));
        instance.ComplexModelRisk = risk.Status;
        instance.ComplexModelRiskReason = risk.Reason;
        if (risk.Level == ModelRiskLevel.UnsafeComplex)
            return this.MarkUnsafeAfterRecreate(instance, $"高风险复杂模型，禁止延迟 transform 写入：{risk.Reason}");

        if (!graphicsInfo.IsUsable || !graphicsInfo.TransformValuesNormal)
        {
            instance.PendingRecreateStabilizeAttempts++;
            if (instance.PendingRecreateStabilizeAttempts < instance.PendingRecreateStabilizeMaxAttempts)
            {
                instance.PendingVisualTransform = true;
                instance.PendingVisualTransformFrameWait = 1;
                instance.ModelApplyStatus = "PendingRecreateStabilize";
                instance.ApplyMdlStatus = "PendingRecreateStabilize";
                instance.InstanceState = "PendingRecreateStabilize";
                instance.PendingVisualTransformResult =
                    $"recreate 后 GraphicsObject 暂未稳定，继续等待：attempt={instance.PendingRecreateStabilizeAttempts}/{instance.PendingRecreateStabilizeMaxAttempts}; {graphicsInfo.SafetyDump}";
                this.LastResult = instance.PendingVisualTransformResult;
                return false;
            }

            return this.MarkUnsafeAfterRecreate(instance, $"RecreateStabilizeTimeout：{graphicsInfo.SafetyDump}");
        }

        if (!TryParseAddress(graphicsInfo.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.MarkUnsafeAfterRecreate(instance, "延迟检查失败：GraphicsObject 地址无效。");

        var targetRotation = NormalizeRotation(instance.CurrentRotation);
        var targetScale = NormalizeScale(instance.CurrentScale);
        if (!IsTransformNormal(instance.CurrentPosition, targetRotation, targetScale))
            return this.MarkUnsafeAfterRecreate(instance, $"目标 transform 数值异常：position=({FormatVector(instance.CurrentPosition)}), rotation={targetRotation}, scale=({FormatVector(targetScale)})");

        var visualApplied = WriteSceneObjectTransform(graphicsAddress, instance.CurrentPosition, targetRotation, targetScale, out var visualReadback);
        if (!visualApplied)
            return this.MarkUnsafeAfterRecreate(instance, $"延迟写入 VisualOnly transform 失败：{visualReadback}");

        var afterWrite = ReadGraphicsInfo((BgPartsLayoutInstance*)pointer);
        this.StoreGraphicsReadback(instance, afterWrite);
        if (!afterWrite.TransformValuesNormal)
            return this.MarkUnsafeAfterRecreate(instance, $"延迟写入后 transform 读回异常：{afterWrite.SafetyDump}");

        instance.PendingVisualTransform = false;
        instance.PendingVisualTransformFrameWait = 0;
        instance.PendingRecreateStabilizeAttempts = 0;
        instance.PendingVisualTransformResult = $"已延迟应用 VisualOnly transform；readback={visualReadback}";
        instance.RecreateVisualReapplyResult = instance.PendingVisualTransformResult;
        instance.LastReadback = afterWrite.Transform;
        instance.LastError = string.Empty;
        instance.HasCollisionMoved = false;
        instance.VisualOnlyVerified = true;
        instance.ModelApplyStatus = risk.Level == ModelRiskLevel.AnimatedStaticOnly ? "AnimatedStaticOnly" : "VisualOnlyOk";
        instance.ApplyMdlStatus = instance.ModelApplyStatus;
        instance.InstanceState = "Ready";
        instance.LastModelOverrideResult = risk.Level == ModelRiskLevel.AnimatedStaticOnly
            ? "自带动画/动态材质模型可能只显示静态外观；动画需要原 layout controller/shared group/event update 支持，暂未支持。"
            : "VisualOnly mdl 替换成功，transform 已延迟安全写入。";
        this.LastResult = instance.PendingVisualTransformResult;
        return true;
    }

    public string BuildAnimationCapabilityDump(LocalLayoutObjectInstance? instance, LayoutProbeInstance? reference)
    {
        var lines = new List<string>
        {
            "自带动画/动态材质模型可能只显示静态外观；动画需要原 layout controller/shared group/event update 支持，暂未支持。",
        };

        if (reference != null && TryGetPointer(reference.Address, out var referencePointer))
        {
            var referenceInfo = ReadGraphicsInfo((BgPartsLayoutInstance*)referencePointer);
            lines.Add($"参考 BgPart: type={reference.Type}; path={reference.ResourcePath}; address={reference.Address}; {referenceInfo.SafetyDump}; renderPointers={referenceInfo.RenderPointers}");
        }
        else
        {
            lines.Add("参考 BgPart: 未选择或地址不可读。");
        }

        if (instance != null && TryGetPointer(instance.OccupiedSlotAddress, out var instancePointer))
        {
            var instanceInfo = ReadGraphicsInfo((BgPartsLayoutInstance*)instancePointer);
            this.StoreGraphicsReadback(instance, instanceInfo);
            lines.Add($"本地实例: id={instance.Id}; path={instance.CurrentResourcePath}; slot={instance.OccupiedSlotAddress}; {instanceInfo.SafetyDump}; renderPointers={instanceInfo.RenderPointers}");
            lines.Add($"状态: modelApplyStatus={instance.ModelApplyStatus}; risk={instance.ComplexModelRisk}; pendingVisualTransform={instance.PendingVisualTransform}; renderInvalid={instance.IsRenderInvalid}");
        }
        else
        {
            lines.Add("本地实例: 未选择或地址不可读。");
        }

        var result = string.Join(Environment.NewLine, lines);
        if (instance != null)
            instance.AnimationCapabilityDump = result;
        return result;
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
        this.StoreGraphicsReadback(instance, after);
        var risk = ClassifyModelRisk(FirstNonEmpty(after.Path, instance.TargetModelPath, instance.CustomModelPath));
        instance.ComplexModelRisk = risk.Status;
        instance.ComplexModelRiskReason = risk.Reason;

        if (!after.IsUsable || !after.TransformValuesNormal)
        {
            instance.PendingVisualTransform = true;
            instance.PendingVisualTransformFrameWait = PendingVisualTransformFrames;
            instance.PendingRecreateStabilizeAttempts = 0;
            instance.PendingVisualTransformResult = "recreate 后 GraphicsObject 状态不安全，已停止 transform 写入。";
            instance.ModelApplyStatus = "PendingRecreateStabilize";
            instance.ApplyMdlStatus = "PendingRecreateStabilize";
            instance.InstanceState = "PendingRecreateStabilize";
            instance.IsRenderInvalid = false;
            instance.ModelExperimentFailed = false;
            instance.TransformWriteDisabledReason = string.Empty;
            instance.TransformWriteDisabledReason = "目标模型 recreate 后 GraphicsObject 状态不安全，已停止 transform 写入，请恢复或切图。";
            instance.RecreateVisualReapplyResult = $"VisualOnly：未立即写 Graphics.Scene.Object transform。dump={after.SafetyDump}";
            instance.RecreateCollisionModeResult = "VisualOnly：未调用 CreateSecondary，未写 Collider；Layout transform 已恢复到原始 slot。";
            return;
        }

        if (risk.Level == ModelRiskLevel.UnsafeComplex)
        {
            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformFrameWait = 0;
            instance.PendingVisualTransformResult = "复杂/动态模型 recreate 后已禁止自动写 transform。";
            instance.ModelApplyStatus = "UnsafeComplexModel";
            instance.ApplyMdlStatus = "UnsafeComplexModel";
            instance.ModelExperimentFailed = true;
            instance.TransformWriteDisabledReason = "目标模型属于动态屏幕/灯光/VFX/事件类高风险资源，recreate 后不写 Graphics.Scene.Object transform。";
            instance.RecreateVisualReapplyResult = $"VisualOnly：模型已尝试 recreate，但自动 transform 写入已暂停。{risk.Reason}";
            instance.RecreateCollisionModeResult = "VisualOnly：未调用 CreateSecondary，未写 Collider；Layout transform 已恢复到原始 slot。";
            return;
        }

        instance.PendingRecreate = false;
        instance.PendingVisualTransform = true;
        instance.PendingVisualTransformFrameWait = PendingVisualTransformFrames;
        instance.PendingRecreateStabilizeAttempts = 0;
        instance.PendingVisualTransformResult = $"等待 {PendingVisualTransformFrames} 帧后重新读取 GraphicsObject，再应用 VisualOnly transform。";
        instance.ModelApplyStatus = risk.Level == ModelRiskLevel.AnimatedStaticOnly ? "AnimatedStaticOnly" : "PendingVisualTransform";
        instance.ApplyMdlStatus = instance.ModelApplyStatus;
        instance.InstanceState = instance.ModelApplyStatus == "PendingVisualTransform" ? "PendingRecreateStabilize" : instance.ModelApplyStatus;
        instance.RecreateVisualReapplyResult = "VisualOnly：recreate 后不在同一帧写 transform；已安排延迟写入。";
        instance.HasCollisionMoved = false;
        instance.RecreateCollisionModeResult =
            $"VisualOnly：未调用 CreateSecondary，未写 Collider；Layout transform 已恢复到原始 slot。Collider={ReadColliderAddress((BgPartsLayoutInstance*)pointer)}";
    }

    private string GetSharedBlockReason(LocalLayoutObjectInstance? instance, string targetPath)
    {
        if (instance == null)
            return "未选中 LocalLayoutObjectInstance。";
        if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate)
            return "实例已失效、已恢复或是重复记录。";
        if (instance.IsRenderInvalid)
            return "实例 render 已失效；请切图或重载地图恢复后再实验。";
        if (string.IsNullOrWhiteSpace(instance.OccupiedSlotAddress))
            return "occupiedSlotAddress 为空。";
        if (string.IsNullOrWhiteSpace(targetPath))
            return "target path 为空。";

        targetPath = targetPath.Trim();
        if (!targetPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "target path 必须以 .mdl 结尾。";
        if (!TryGetCategoryForPath(targetPath, out _))
            return "target path 只支持 bg/...mdl 或 bgcommon/...mdl。";
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

            _ = (ResourceCategory)(ushort)graphicsObject->ModelResourceHandle->Type.Category;
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

    private bool MarkUnsafeAfterRecreate(LocalLayoutObjectInstance instance, string message)
    {
        instance.PendingVisualTransform = false;
        instance.PendingVisualTransformFrameWait = 0;
        instance.PendingRecreateStabilizeAttempts = instance.PendingRecreateStabilizeMaxAttempts;
        instance.PendingVisualTransformResult = message;
        instance.IsRenderInvalid = true;
        instance.InstanceState = "RenderInvalid";
        instance.ModelExperimentFailed = true;
        instance.ModelApplyStatus = "UnsafeAfterRecreate";
        instance.ApplyMdlStatus = "UnsafeAfterRecreate";
        instance.TransformWriteDisabledReason = "目标模型 recreate 后 GraphicsObject 状态不安全，已停止 transform 写入，请恢复或切图。";
        instance.LastModelOverrideError = message;
        instance.LastError = message;
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

    private void StoreGraphicsReadback(LocalLayoutObjectInstance instance, GraphicsInfo info)
    {
        instance.GraphicsObjectAddress = info.GraphicsObjectAddress;
        instance.ModelResourceHandleAddress = info.ModelResourceHandleAddress;
        instance.ModelResourceHandleLoadState = info.LoadState < 0 ? "不可用" : info.LoadState.ToString();
        instance.ModelVisibilityReadback = info.Visible;
        instance.ModelTransformReadback = info.Transform;
        instance.ModelResourceCategoryReadback = info.Category;
        instance.ModelResourceHandleDump = info.SafetyDump;
        instance.GraphicsSafetyDump = info.SafetyDump;
        instance.RecreateAfterCachedMatrices = info.CachedMatricesAddress;
        instance.RecreateAfterStainOrBgChangeData = info.StainOrBgChangeDataAddress;
        instance.RecreateAfterCachedTransform = info.CachedTransformAddress;
        instance.RecreateAfterAnimationData = info.AnimationDataAddress;
        instance.ModelPointerDiff = info.RenderPointers;
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

        if (!IsTransformNormal(position, rotation, scale))
        {
            readback = $"目标 transform 数值异常：position=({FormatVector(position)}), rotation={rotation}, scale=({FormatVector(scale)})";
            return false;
        }

        try
        {
            var obj = (SceneObject*)graphicsObjectAddress;
            var bg = (SceneBgObject*)graphicsObjectAddress;
            if (bg->ModelResourceHandle == null)
            {
                readback = "ModelResourceHandle=null";
                return false;
            }

            if (bg->ModelResourceHandle->LoadState != 7)
            {
                readback = $"LoadState={bg->ModelResourceHandle->LoadState}，不是稳定完成状态 7";
                return false;
            }

            if (!bg->IsVisible)
            {
                readback = "visible=false";
                return false;
            }

            obj->Position = position;
            obj->Rotation = rotation;
            obj->Scale = scale;
            bg->IsTransformChanged = true;
            bg->NotifyTransformChanged();
            bg->UpdateTransforms(true);
            bg->UpdateRender();
            readback = $"position=({FormatVector(obj->Position)}), rotation={obj->Rotation}, scale=({FormatVector(obj->Scale)})";
            return IsTransformNormal(obj->Position, obj->Rotation, obj->Scale);
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
            return GraphicsInfo.Empty("GraphicsObject=null");

        var bgObject = (SceneBgObject*)bgPart->GraphicsObject;
        var graphicsAddress = $"0x{(nint)bgObject:X}";
        var handleAddress = "0x0";
        var path = string.Empty;
        var category = "Unknown";
        var loadState = -1;
        var handleAvailable = false;
        var isVisible = false;
        var transformReadable = false;
        var transformValuesNormal = false;
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        var scale = Vector3.One;
        var renderPointers = string.Empty;
        var cachedMatricesAddress = "0x0";
        var stainOrBgChangeDataAddress = "0x0";
        var cachedTransformAddress = "0x0";
        var animationDataAddress = "0x0";

        try
        {
            if (bgObject->ModelResourceHandle != null)
            {
                handleAvailable = true;
                handleAddress = $"0x{(nint)bgObject->ModelResourceHandle:X}";
                path = bgObject->ModelResourceHandle->FileName.ToString();
                category = FormatCategory((ResourceCategory)(ushort)bgObject->ModelResourceHandle->Type.Category);
                loadState = bgObject->ModelResourceHandle->LoadState;
            }
        }
        catch
        {
        }

        try
        {
            isVisible = bgObject->IsVisible;
            position = bgObject->Position;
            rotation = bgObject->Rotation;
            scale = bgObject->Scale;
            transformReadable = true;
            transformValuesNormal = IsTransformNormal(position, rotation, scale);
        }
        catch
        {
        }

        try
        {
            var baseAddress = (nint)bgObject;
            var cachedMatrices = *(nint*)(baseAddress + CachedMatricesOffset);
            var stainOrBgChangeData = *(nint*)(baseAddress + StainOrBgChangeDataOffset);
            var cachedTransform = *(nint*)(baseAddress + CachedTransformOffset);
            var animationData = *(nint*)(baseAddress + AnimationDataOffset);
            cachedMatricesAddress = $"0x{cachedMatrices:X}";
            stainOrBgChangeDataAddress = $"0x{stainOrBgChangeData:X}";
            cachedTransformAddress = $"0x{cachedTransform:X}";
            animationDataAddress = $"0x{animationData:X}";
            renderPointers = $"cachedMatrices={cachedMatricesAddress}; stainOrBgChangeData={stainOrBgChangeDataAddress}; cachedTransform={cachedTransformAddress}; animationData={animationDataAddress}";
        }
        catch
        {
        }

        var visible = isVisible.ToString();
        var transform = transformReadable
            ? $"position=({FormatVector(position)}), rotation={rotation}, scale=({FormatVector(scale)})"
            : "transform read failed";
        var usable = handleAvailable && loadState == 7 && isVisible && transformReadable && transformValuesNormal;
        var safetyDump =
            $"graphics={graphicsAddress}; handle={handleAddress}; path={path}; category={category}; loadState={loadState}; " +
            $"visible={visible}; transformReadable={transformReadable}; transformNormal={transformValuesNormal}; {renderPointers}";

        return new GraphicsInfo(
            graphicsAddress,
            handleAddress,
            path,
            category,
            visible,
            transform,
            usable,
            loadState,
            isVisible,
            transformReadable,
            transformValuesNormal,
            position,
            rotation,
            scale,
            renderPointers,
            cachedMatricesAddress,
            stainOrBgChangeDataAddress,
            cachedTransformAddress,
            animationDataAddress,
            safetyDump);
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

    private static bool IsTransformNormal(Vector3 position, Quaternion rotation, Vector3 scale)
        => IsVectorNormal(position)
            && IsQuaternionNormal(rotation)
            && IsVectorNormal(scale)
            && Math.Abs(scale.X) > 0.0001f
            && Math.Abs(scale.Y) > 0.0001f
            && Math.Abs(scale.Z) > 0.0001f;

    private static bool IsVectorNormal(Vector3 value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && Math.Abs(value.X) < MaxReasonableCoordinate
            && Math.Abs(value.Y) < MaxReasonableCoordinate
            && Math.Abs(value.Z) < MaxReasonableCoordinate;

    private static bool IsQuaternionNormal(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() is > 0.0001f and < 10f;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool TryGetCategoryForPath(string path, out ResourceCategory category)
    {
        path = path.Trim();
        if (path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase))
        {
            category = ResourceCategory.BgCommon;
            return true;
        }

        if (path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase))
        {
            category = ResourceCategory.Bg;
            return true;
        }

        category = default;
        return false;
    }

    private static string FormatCategory(ResourceCategory category)
        => $"{category} ({(int)category})";

    private static ModelRisk ClassifyModelRisk(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        var fileName = normalized.Split('/').LastOrDefault() ?? normalized;
        if (string.IsNullOrWhiteSpace(normalized))
            return new ModelRisk(ModelRiskLevel.Static, "StaticOk", "未提供 target path，按普通静态模型处理。");

        if (normalized.Contains("/vfx/", StringComparison.Ordinal)
            || normalized.Contains("/light/", StringComparison.Ordinal)
            || normalized.Contains("/shared/", StringComparison.Ordinal)
            || normalized.Contains("/evt/", StringComparison.Ordinal))
        {
            return new ModelRisk(ModelRiskLevel.UnsafeComplex, "UnsafeComplexModel", "路径属于 vfx/light/shared/evt 类资源，可能依赖额外 layout controller。");
        }

        if (normalized.Contains("/twn/", StringComparison.Ordinal)
            && (fileName.Contains("scr", StringComparison.Ordinal)
                || fileName.Contains("screen", StringComparison.Ordinal)
                || fileName.Contains("monitor", StringComparison.Ordinal)
                || fileName.Contains("ad", StringComparison.Ordinal)))
        {
            return new ModelRisk(ModelRiskLevel.UnsafeComplex, "UnsafeComplexModel", "城镇动态屏幕/广告类资源 recreate 后 transform 写入风险高，已软禁用自动写入。");
        }

        if (normalized.Contains("/aet/", StringComparison.Ordinal))
            return new ModelRisk(ModelRiskLevel.AnimatedStaticOnly, "AnimatedStaticOnly", "Aetheryte/动画类模型可能只显示静态外观，动画暂未接入。");

        return new ModelRisk(ModelRiskLevel.Static, "StaticOk", "普通 bg/bgcommon 静态模型路径。");
    }

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

    private enum ModelRiskLevel
    {
        Static,
        AnimatedStaticOnly,
        UnsafeComplex,
    }

    private readonly record struct ModelRisk(ModelRiskLevel Level, string Status, string Reason);

    private readonly record struct GraphicsInfo(
        string GraphicsObjectAddress,
        string ModelResourceHandleAddress,
        string Path,
        string Category,
        string Visible,
        string Transform,
        bool IsUsable,
        int LoadState,
        bool IsVisible,
        bool TransformReadable,
        bool TransformValuesNormal,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        string RenderPointers,
        string CachedMatricesAddress,
        string StainOrBgChangeDataAddress,
        string CachedTransformAddress,
        string AnimationDataAddress,
        string SafetyDump)
    {
        public static GraphicsInfo Empty(string reason)
            => new(
                "0x0",
                "0x0",
                string.Empty,
                "Unknown",
                "False",
                reason,
                false,
                -1,
                false,
                false,
                false,
                Vector3.Zero,
                Quaternion.Identity,
                Vector3.One,
                string.Empty,
                "0x0",
                "0x0",
                "0x0",
                "0x0",
                reason);
    }

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
