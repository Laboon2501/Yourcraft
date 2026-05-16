using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;
using System.Numerics;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace LocalQuestReborn.Services;

public sealed unsafe class LayoutObjectTransformService
{
    public string LastResult { get; private set; } = "尚未执行本地场景物体 transform。";

    public bool ApplyPosition(LocalLayoutObjectInstance instance)
        => this.ApplyTransform(instance);

    public bool ApplyRotation(LocalLayoutObjectInstance instance)
        => this.ApplyTransform(instance);

    public bool ApplyScale(LocalLayoutObjectInstance instance)
        => this.ApplyTransform(instance);

    public bool ApplyTransform(LocalLayoutObjectInstance instance)
    {
        if (!this.CanWriteTransform(instance, out var reason))
            return this.Fail(instance, reason);

        return instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? this.ApplyVisualOnly(instance)
            : this.ApplyFullLayout(instance);
    }

    public bool RestoreTransform(LocalLayoutObjectInstance instance)
    {
        if (!this.CanWriteTransform(instance, out var reason))
            return this.Fail(instance, reason);

        return instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? this.RestoreVisualOnly(instance)
            : this.RestoreFullLayout(instance);
    }

    public bool CanWriteTransform(LocalLayoutObjectInstance? instance, out string reason)
    {
        reason = string.Empty;
        if (instance == null)
        {
            reason = "未选中有效实例。";
            return false;
        }

        if (instance.IsInvalid)
            reason = "实例已失效。";
        else if (instance.IsRestored)
            reason = "实例已恢复。";
        else if (instance.PendingVisualTransform)
            reason = $"recreate 后 VisualOnly transform 仍在等待安全写入：{instance.PendingVisualTransformResult}";
        else if (instance.IsRenderInvalid)
            reason = string.IsNullOrWhiteSpace(instance.TransformWriteDisabledReason)
                ? "当前实例 render 已失效，不能再写 Graphics.Scene.Object transform。"
                : instance.TransformWriteDisabledReason;
        else if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            reason = this.GetVisualOnlyBlockReason(instance);
        else if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer) || pointer == null)
            reason = $"slot 地址解析失败：{instance.OccupiedSlotAddress}";

        if (string.IsNullOrWhiteSpace(reason))
        {
            instance.TransformWriteDisabledReason = string.Empty;
            return true;
        }

        instance.TransformWriteDisabledReason = reason;
        return false;
    }

    private string GetVisualOnlyBlockReason(LocalLayoutObjectInstance instance)
    {
        this.EnsureGraphicsObjectAddress(instance);
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.IsRenderInvalid = true;
            return $"graphicsObjectAddress 无效：{instance.GraphicsObjectAddress}";
        }

        try
        {
            if (!ValidateSceneBgObjectWritable(graphicsAddress, out var validationReason))
            {
                instance.IsRenderInvalid = true;
                return validationReason;
            }

            var readback = ReadSceneObjectTransform(graphicsAddress);
            if (readback == null)
            {
                instance.IsRenderInvalid = true;
                return "实例 render 已失效或 native pointer stale：readback transform 失败。";
            }

            if (!IsTransformNormal(readback.Value.Position, readback.Value.Rotation, readback.Value.Scale))
            {
                instance.IsRenderInvalid = true;
                return $"实例 transform 读回数值异常，禁止写入：{FormatSceneSnapshot(readback.Value)}";
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            instance.IsRenderInvalid = true;
            return $"实例 render 检查失败：{ex.Message}";
        }
    }

    private bool ApplyVisualOnly(LocalLayoutObjectInstance instance)
    {
        this.EnsureGraphicsObjectAddress(instance);
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.Fail(instance, $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}");

        try
        {
            var rotation = WorldTransformUtil.WorldEulerRadiansToQuaternion(instance.CurrentRotationEuler);
            var target = new SceneTransformSnapshot(instance.CurrentPosition, rotation, instance.CurrentScale, false);
            if (!IsTransformNormal(target.Position, target.Rotation, target.Scale))
                return this.Fail(instance, "VisualOnly requested transform is not finite or has invalid scale.");
            if (!ValidateSceneBgObjectWritable(graphicsAddress, out var validationReason))
                return this.Fail(instance, validationReason);

            WriteSceneObjectTransform(graphicsAddress, target);
            var readback = ReadSceneObjectTransform(graphicsAddress);
            if (readback == null)
                return this.Fail(instance, "VisualOnly 写入后 readback 失败。");

            instance.CurrentPosition = readback.Value.Position;
            instance.CurrentRotation = readback.Value.Rotation;
            instance.CurrentRotationEuler = WorldTransformUtil.QuaternionToWorldEulerRadians(readback.Value.Rotation);
            instance.CurrentScale = readback.Value.Scale;
            instance.CurrentVisualTranslation = readback.Value.Position;
            instance.LastReadback = FormatSceneSnapshot(readback.Value);
            instance.LastError = string.Empty;
            instance.IsOccupied = true;
            instance.IsRestored = false;
            instance.HasCollisionMoved = false;
            instance.VisualOnlyVerified = true;
            instance.Notes = "VisualOnly：写 Graphics.Scene.Object Position/Rotation/Scale，不写 LayoutInstance，collision 不变。";
            this.LastResult = $"VisualOnly transform 已应用：{instance.Id}";
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"VisualOnly transform 写入失败：{ex.Message}");
        }
    }

    private bool ApplyFullLayout(LocalLayoutObjectInstance instance)
    {
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return this.Fail(instance, $"slot 地址解析失败：{instance.OccupiedSlotAddress}");

        try
        {
            var rotation = WorldTransformUtil.WorldEulerRadiansToQuaternion(instance.CurrentRotationEuler);
            var transform = new Transform
            {
                Translation = instance.CurrentPosition,
                Rotation = rotation,
                Scale = instance.CurrentScale,
            };
            if (!IsTransformNormal(transform.Translation, transform.Rotation, transform.Scale))
                return this.Fail(instance, "FullLayout requested transform is not finite or has invalid scale.");

            pointer->SetTransform(&transform);

            var readback = ReadLayoutTransform(pointer);
            if (readback == null)
                return this.Fail(instance, "FullLayout 写入后 readback 失败。");

            instance.CurrentPosition = readback.Value.Position;
            instance.CurrentRotation = readback.Value.Rotation;
            instance.CurrentRotationEuler = WorldTransformUtil.QuaternionToWorldEulerRadians(readback.Value.Rotation);
            instance.CurrentScale = readback.Value.Scale;
            instance.LastReadback = FormatLayoutSnapshot(readback.Value);
            instance.LastError = string.Empty;
            instance.IsOccupied = true;
            instance.IsRestored = false;
            instance.HasCollisionMoved = true;
            instance.Notes = "FullLayoutWithCollision：写 LayoutInstance transform，模型和 collision 一起变化。";
            this.LastResult = $"FullLayout transform 已应用：{instance.Id}";
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"FullLayout transform 写入失败：{ex.Message}");
        }
    }

    private bool RestoreVisualOnly(LocalLayoutObjectInstance instance)
    {
        this.EnsureGraphicsObjectAddress(instance);
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.Fail(instance, $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}");

        try
        {
            var original = new SceneTransformSnapshot(instance.OriginalVisualPosition, instance.OriginalVisualRotation, instance.OriginalVisualScale, false);
            WriteSceneObjectTransform(graphicsAddress, original);
            var readback = ReadSceneObjectTransform(graphicsAddress) ?? original;
            instance.CurrentPosition = readback.Position;
            instance.CurrentRotation = readback.Rotation;
            instance.CurrentRotationEuler = WorldTransformUtil.QuaternionToWorldEulerRadians(readback.Rotation);
            instance.CurrentScale = readback.Scale;
            instance.CurrentVisualTranslation = readback.Position;
            instance.LastReadback = FormatSceneSnapshot(readback);
            instance.LastError = string.Empty;
            instance.HasCollisionMoved = false;
            this.LastResult = $"VisualOnly transform 已恢复：{instance.Id}";
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"VisualOnly transform 恢复失败：{ex.Message}");
        }
    }

    private bool RestoreFullLayout(LocalLayoutObjectInstance instance)
    {
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return this.Fail(instance, $"slot 地址解析失败：{instance.OccupiedSlotAddress}");

        try
        {
            var transform = new Transform
            {
                Translation = instance.OriginalLayoutPosition,
                Rotation = instance.OriginalLayoutRotation,
                Scale = instance.OriginalLayoutScale,
            };
            pointer->SetTransform(&transform);

            var readback = ReadLayoutTransform(pointer);
            if (readback == null)
                return this.Fail(instance, "FullLayout 恢复后 readback 失败。");

            instance.CurrentPosition = readback.Value.Position;
            instance.CurrentRotation = readback.Value.Rotation;
            instance.CurrentRotationEuler = WorldTransformUtil.QuaternionToWorldEulerRadians(readback.Value.Rotation);
            instance.CurrentScale = readback.Value.Scale;
            instance.LastReadback = FormatLayoutSnapshot(readback.Value);
            instance.LastError = string.Empty;
            instance.HasCollisionMoved = false;
            this.LastResult = $"FullLayout transform 已恢复：{instance.Id}";
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"FullLayout transform 恢复失败：{ex.Message}");
        }
    }

    private bool Fail(LocalLayoutObjectInstance instance, string message)
    {
        instance.LastError = message;
        instance.TransformWriteDisabledReason = message;
        this.LastResult = message;
        return false;
    }

    private void EnsureGraphicsObjectAddress(LocalLayoutObjectInstance instance)
    {
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return;

        if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
            return;

        instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
    }

    private static SceneTransformSnapshot? ReadSceneObjectTransform(nint graphicsObjectAddress)
    {
        if (graphicsObjectAddress == 0)
            return null;

        var obj = (SceneObject*)graphicsObjectAddress;
        return new SceneTransformSnapshot(obj->Position, obj->Rotation, obj->Scale, false);
    }

    private static void WriteSceneObjectTransform(nint graphicsObjectAddress, SceneTransformSnapshot snapshot)
    {
        var obj = (SceneObject*)graphicsObjectAddress;
        var bg = (SceneBgObject*)graphicsObjectAddress;
        obj->Position = snapshot.Position;
        obj->Rotation = snapshot.Rotation;
        obj->Scale = snapshot.Scale;
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
    }

    private static bool ValidateSceneBgObjectWritable(nint graphicsObjectAddress, out string reason)
    {
        reason = string.Empty;
        if (graphicsObjectAddress == 0)
        {
            reason = "GraphicsObject address is zero";
            return false;
        }

        try
        {
            var vtable = *(nint*)graphicsObjectAddress;
            if (vtable == 0)
            {
                reason = "GraphicsObject vtable is zero";
                return false;
            }

            var bg = (SceneBgObject*)graphicsObjectAddress;
            if (bg->ModelResourceHandle == null)
            {
                reason = "ModelResourceHandle=null";
                return false;
            }

            if (bg->ModelResourceHandle->LoadState < 7)
            {
                reason = $"ModelResourceHandle LoadState={bg->ModelResourceHandle->LoadState}";
                return false;
            }

            var scene = ReadSceneObjectTransform(graphicsObjectAddress);
            if (scene == null)
            {
                reason = "Scene.Object transform readback failed";
                return false;
            }

            if (!IsTransformNormal(scene.Value.Position, scene.Value.Rotation, scene.Value.Scale))
            {
                reason = $"Scene.Object transform readback invalid: {FormatSceneSnapshot(scene.Value)}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"GraphicsObject validation failed: {ex.Message}";
            return false;
        }
    }

    private static LayoutTransformSnapshot? ReadLayoutTransform(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return null;

        var transform = pointer->GetTransformImpl();
        return transform == null ? null : new LayoutTransformSnapshot(transform->Translation, transform->Rotation, transform->Scale);
    }

    private static bool TryGetPointer(string? raw, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(raw, out var address) || address == 0)
            return false;

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static bool TryGetGraphicsObjectAddress(ILayoutInstance* pointer, out nint graphicsObjectAddress)
    {
        graphicsObjectAddress = 0;
        try
        {
            if (pointer == null || pointer->Id.Type != InstanceType.BgPart)
                return false;

            var bgPart = (BgPartsLayoutInstance*)pointer;
            if (bgPart->GraphicsObject == null)
                return false;

            graphicsObjectAddress = (nint)bgPart->GraphicsObject;
            return true;
        }
        catch
        {
            return false;
        }
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

    private static string FormatSceneSnapshot(SceneTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)}), IsTransformChanged={snapshot.IsTransformChanged}";

    private static string FormatLayoutSnapshot(LayoutTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)})";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

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
            && Math.Abs(value.X) < 1_000_000f
            && Math.Abs(value.Y) < 1_000_000f
            && Math.Abs(value.Z) < 1_000_000f;

    private static bool IsQuaternionNormal(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() is > 0.0001f and < 10f;

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
