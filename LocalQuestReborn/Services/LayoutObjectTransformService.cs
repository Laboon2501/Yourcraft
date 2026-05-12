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
        return instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? this.ApplyVisualOnly(instance)
            : this.ApplyFullLayout(instance);
    }

    public bool RestoreTransform(LocalLayoutObjectInstance instance)
    {
        return instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? this.RestoreVisualOnly(instance)
            : this.RestoreFullLayout(instance);
    }

    private bool ApplyVisualOnly(LocalLayoutObjectInstance instance)
    {
        this.EnsureGraphicsObjectAddress(instance);
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.Fail(instance, $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}");

        try
        {
            var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(instance.CurrentRotationEuler.Y, instance.CurrentRotationEuler.X, instance.CurrentRotationEuler.Z) * instance.OriginalVisualRotation);
            var target = new SceneTransformSnapshot(instance.CurrentPosition, rotation, instance.CurrentScale, false);
            WriteSceneObjectTransform(graphicsAddress, target);
            var readback = ReadSceneObjectTransform(graphicsAddress);
            if (readback == null)
                return this.Fail(instance, "VisualOnly 写入后 readback 失败。");

            instance.CurrentPosition = readback.Value.Position;
            instance.CurrentRotation = readback.Value.Rotation;
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
            var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(instance.CurrentRotationEuler.Y, instance.CurrentRotationEuler.X, instance.CurrentRotationEuler.Z) * instance.OriginalLayoutRotation);
            var transform = new Transform
            {
                Translation = instance.CurrentPosition,
                Rotation = rotation,
                Scale = instance.CurrentScale,
            };
            pointer->SetTransform(&transform);

            var readback = ReadLayoutTransform(pointer);
            if (readback == null)
                return this.Fail(instance, "FullLayout 写入后 readback 失败。");

            instance.CurrentPosition = readback.Value.Position;
            instance.CurrentRotation = readback.Value.Rotation;
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
            instance.CurrentRotationEuler = Vector3.Zero;
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
            instance.CurrentRotationEuler = Vector3.Zero;
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
        this.LastResult = message;
        return false;
    }

    private void EnsureGraphicsObjectAddress(LocalLayoutObjectInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.GraphicsObjectAddress))
            return;

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return;

        if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
            return;

        instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
        var originalVisual = ReadSceneObjectTransform(graphicsAddress);
        if (originalVisual == null)
            return;

        instance.OriginalVisualPosition = originalVisual.Value.Position;
        instance.OriginalVisualRotation = originalVisual.Value.Rotation;
        instance.OriginalVisualScale = originalVisual.Value.Scale;
        instance.OriginalVisualTransform = FormatSceneSnapshot(originalVisual.Value);
    }

    private static SceneTransformSnapshot? ReadSceneObjectTransform(nint graphicsObjectAddress)
    {
        if (graphicsObjectAddress == 0)
            return null;

        var obj = (SceneObject*)graphicsObjectAddress;
        var bg = (SceneBgObject*)graphicsObjectAddress;
        return new SceneTransformSnapshot(obj->Position, obj->Rotation, obj->Scale, bg->IsTransformChanged);
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

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
