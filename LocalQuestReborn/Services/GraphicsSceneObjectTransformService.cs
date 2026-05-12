using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Numerics;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace LocalQuestReborn.Services;

public sealed unsafe class GraphicsSceneObjectTransformService
{
    private readonly BgPartVisualTransformProbeService visualProbe;
    private readonly Dictionary<nint, SceneTransformSnapshot> originals = [];
    private nint currentGraphicsObjectAddress;

    public GraphicsSceneObjectTransformService(BgPartVisualTransformProbeService visualProbe)
    {
        this.visualProbe = visualProbe;
    }

    public string LastResult { get; private set; } = "尚未读取 Scene.Object transform。";

    public bool HasOriginalForCurrent
        => this.currentGraphicsObjectAddress != 0 && this.originals.ContainsKey(this.currentGraphicsObjectAddress);

    public void ReadCurrent()
    {
        if (!this.TryGetCurrentObject(out var address, out var obj, out var bg, out var reason))
        {
            this.LastResult = reason;
            return;
        }

        this.currentGraphicsObjectAddress = address;
        var snapshot = ReadSnapshot(obj, bg);
        this.originals.TryAdd(address, snapshot);
        this.LastResult = string.Join(Environment.NewLine, new[]
        {
            "已读取 Graphics.Scene.Object transform。",
            $"graphicsObjectAddress=0x{address:X}",
            FormatSnapshot("current", snapshot),
            "说明：BgPart.GraphicsObject reinterpret 为 Graphics.Scene.Object / BgObject；未写 layout/collision/resourcePath。",
        });
    }

    public void Yaw(float degrees)
    {
        this.Apply("Scene.Object yaw " + (degrees >= 0 ? "+" : string.Empty) + $"{degrees:F1}°", snapshot =>
        {
            var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(degrees));
            snapshot.Rotation = Quaternion.Normalize(delta * snapshot.Rotation);
            return snapshot;
        });
    }

    public void Scale(float multiplier)
    {
        this.Apply($"Scene.Object scale x{multiplier:F3}", snapshot =>
        {
            snapshot.Scale *= multiplier;
            return snapshot;
        });
    }

    public void MovePosition(Vector3 position)
    {
        this.Apply("Scene.Object position 移到玩家位置", snapshot =>
        {
            snapshot.Position = position;
            return snapshot;
        });
    }

    public void Restore()
    {
        if (!this.TryGetCurrentObject(out var address, out var obj, out var bg, out var reason))
        {
            this.LastResult = reason;
            return;
        }

        if (!this.originals.TryGetValue(address, out var original))
        {
            this.LastResult = $"没有保存的 original Scene.Object transform：0x{address:X}。请先读取一次。";
            return;
        }

        try
        {
            var before = ReadSnapshot(obj, bg);
            WriteSnapshot(obj, bg, original);
            var after = ReadSnapshot(obj, bg);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复 Scene.Object transform。",
                $"graphicsObjectAddress=0x{address:X}",
                FormatSnapshot("before", before),
                FormatSnapshot("targetOriginal", original),
                FormatSnapshot("readback", after),
                $"readbackMatches={SnapshotsClose(original, after)}",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复 Scene.Object transform 失败：{ex.Message}";
        }
    }

    private void Apply(string action, Func<SceneTransformSnapshot, SceneTransformSnapshot> mutate)
    {
        if (!this.TryGetCurrentObject(out var address, out var obj, out var bg, out var reason))
        {
            this.LastResult = reason;
            return;
        }

        try
        {
            this.currentGraphicsObjectAddress = address;
            var before = ReadSnapshot(obj, bg);
            this.originals.TryAdd(address, before);
            var target = mutate(before);
            WriteSnapshot(obj, bg, target);
            var after = ReadSnapshot(obj, bg);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                $"{action} 已执行。",
                $"graphicsObjectAddress=0x{address:X}",
                FormatSnapshot("before", before),
                FormatSnapshot("target", target),
                FormatSnapshot("readback", after),
                $"readbackMatches={SnapshotsClose(target, after)}",
                "调用链：写 Scene.Object Position/Rotation/Scale -> IsTransformChanged=true -> NotifyTransformChanged -> UpdateTransforms(true) -> UpdateRender。",
                "安全边界：不写 layout transform，不写 collision/physics，不写 resourcePath，不写 raw matrix。",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"{action} 失败：{ex.Message}";
        }
    }

    private bool TryGetCurrentObject(out nint address, out SceneObject* obj, out BgObject* bg, out string reason)
    {
        address = 0;
        obj = null;
        bg = null;
        if (!this.visualProbe.TryGetCurrentGraphicsObject(out address, out reason))
            return false;

        if (address == 0)
        {
            reason = "GraphicsObject 地址无效。";
            return false;
        }

        obj = (SceneObject*)address;
        bg = (BgObject*)address;
        reason = "OK";
        return true;
    }

    private static SceneTransformSnapshot ReadSnapshot(SceneObject* obj, BgObject* bg)
        => new(obj->Position, obj->Rotation, obj->Scale, bg->IsTransformChanged);

    private static void WriteSnapshot(SceneObject* obj, BgObject* bg, SceneTransformSnapshot snapshot)
    {
        obj->Position = snapshot.Position;
        obj->Rotation = snapshot.Rotation;
        obj->Scale = snapshot.Scale;
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
    }

    private static bool SnapshotsClose(SceneTransformSnapshot expected, SceneTransformSnapshot actual)
    {
        return Vector3.Distance(expected.Position, actual.Position) <= 0.01f &&
               Vector3.Distance(expected.Scale, actual.Scale) <= 0.01f &&
               QuaternionClose(expected.Rotation, actual.Rotation);
    }

    private static bool QuaternionClose(Quaternion a, Quaternion b)
        => MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y) + MathF.Abs(a.Z - b.Z) + MathF.Abs(a.W - b.W) <= 0.01f;

    private static float DegreesToRadians(float degrees)
        => degrees * (MathF.PI / 180f);

    private static string FormatSnapshot(string label, SceneTransformSnapshot snapshot)
        => $"{label}: position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)}), IsTransformChanged={snapshot.IsTransformChanged}";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";

    private record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);
}
