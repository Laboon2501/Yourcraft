using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class TargetProbeService
{
    private readonly ITargetManager targetManager;

    public TargetProbeService(ITargetManager targetManager)
    {
        this.targetManager = targetManager;
    }

    public TargetProbeSnapshot? ReferenceNpcSnapshot { get; private set; }

    public TargetProbeSnapshot? BrioActorSnapshot { get; private set; }

    public string LastComparison { get; private set; } = "尚未对比。";

    public TargetProbeSnapshot CaptureCurrentTarget(string label = "参考 NPC 快照")
        => CaptureObject(this.targetManager.Target, label);

    public TargetProbeSnapshot CaptureActor(RuntimeActorInstance actor, string label = "生成 Actor 快照")
    {
        var snapshot = CaptureObject(actor.CharacterObject, label);
        snapshot.Fields["RuntimeId"] = actor.RuntimeId;
        snapshot.Fields["NpcId"] = actor.NpcId;
        snapshot.Fields["Actor.ObjectIndex"] = actor.ObjectIndex;
        snapshot.Fields["Actor.Address"] = actor.Address;
        return snapshot;
    }

    public void SaveReferenceFromCurrentTarget()
        => this.ReferenceNpcSnapshot = this.CaptureCurrentTarget();

    public bool SaveActorSnapshot(RuntimeActorInstance actor)
    {
        if (actor.CharacterObject == null)
        {
            this.BrioActorSnapshot = new TargetProbeSnapshot { Label = "生成 Actor 快照（失败）" };
            this.BrioActorSnapshot.Fields["错误"] = "characterObject 为空。";
            return false;
        }

        this.BrioActorSnapshot = this.CaptureActor(actor);
        return true;
    }

    public string CompareSnapshots()
    {
        if (this.ReferenceNpcSnapshot == null || this.BrioActorSnapshot == null)
        {
            this.LastComparison = "需要先保存参考 NPC 快照和生成 Actor 快照。";
            return this.LastComparison;
        }

        var keys = this.ReferenceNpcSnapshot.Fields.Keys
            .Union(this.BrioActorSnapshot.Fields.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>
        {
            "字段差异：",
        };

        foreach (var key in keys)
        {
            var left = this.ReferenceNpcSnapshot.Fields.GetValueOrDefault(key, "未读取");
            var right = this.BrioActorSnapshot.Fields.GetValueOrDefault(key, "未读取");
            if (!string.Equals(left, right, StringComparison.Ordinal))
                lines.Add($"- {key}: 参考={left} / Actor={right}");
        }

        lines.Add("");
        lines.Add("可能控制可选中：ObjectKind、SubKind、IsTargetable、EventHandler、DataId、GameObjectId、HitboxRadius。");
        lines.Add("可能控制 NamePlate：Name、NameId、NamePlateIconId、ObjectKind、DataId，以及 Dalamud NamePlateGui override。");
        lines.Add("本轮不写未知字段；只保留 TargetManager setter 和快照对比。");
        this.LastComparison = string.Join(Environment.NewLine, lines);
        return this.LastComparison;
    }

    public static TargetProbeSnapshot CaptureObject(object? obj, string label)
    {
        var snapshot = new TargetProbeSnapshot { Label = label, CapturedAt = DateTime.Now };
        if (obj == null)
        {
            snapshot.Fields["错误"] = "对象为空。";
            return snapshot;
        }

        snapshot.Fields["Type"] = obj.GetType().FullName ?? obj.GetType().Name;
        foreach (var name in new[]
                 {
                     "Name", "ObjectIndex", "ObjectTableIndex", "Index", "Address", "ObjectKind", "SubKind", "DataId",
                     "EntityId", "GameObjectId", "ObjectId", "OwnerId", "IsTargetable", "Targetable", "Position",
                     "HitboxRadius", "EventHandler", "NameId", "NamePlateIconId", "BaseId", "TargetObjectId",
                     "RenderFlags",
                 })
        {
            snapshot.Fields[name] = ReadMember(obj, name);
        }

        snapshot.Fields["Name.CanWrite"] = CanWrite(obj, "Name");
        snapshot.Fields["IsTargetable.CanWrite"] = CanWrite(obj, "IsTargetable");
        snapshot.Fields["Targetable.CanWrite"] = CanWrite(obj, "Targetable");
        return snapshot;
    }

    private static string CanWrite(object obj, string name)
    {
        try
        {
            return obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.CanWrite == true
                ? "True"
                : "False";
        }
        catch
        {
            return "未知";
        }
    }

    private static string ReadMember(object obj, string name)
    {
        try
        {
            var type = obj.GetType();
            var value = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj)
                ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
            return FormatValue(value);
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "null";

        if (value is Vector3 vector)
            return $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";

        return value.ToString() ?? "null";
    }
}
