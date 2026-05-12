using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class NativeNpcProbeService
{
    private readonly ITargetManager targetManager;

    public NativeNpcProbeService(ITargetManager targetManager)
    {
        this.targetManager = targetManager;
    }

    public NativeNpcProbeSnapshot? ReferenceEventNpcSnapshot { get; private set; }

    public NativeNpcProbeSnapshot? BrioActorSnapshot { get; private set; }

    public string LastComparison { get; private set; } = "尚未对比。";

    public NativeNpcProbeSnapshot SaveCurrentTargetAsReference()
    {
        this.ReferenceEventNpcSnapshot = CaptureObject(this.targetManager.Target, "真实 EventNpc 快照");
        return this.ReferenceEventNpcSnapshot;
    }

    public NativeNpcProbeSnapshot SaveActorSnapshot(RuntimeActorInstance actor)
    {
        this.BrioActorSnapshot = CaptureObject(actor.CharacterObject, "Brio Actor 快照");
        this.BrioActorSnapshot.Fields["RuntimeId"] = new(actor.RuntimeId, "LocalQuestReborn RuntimeActorInstance");
        this.BrioActorSnapshot.Fields["NpcId"] = new(actor.NpcId, "LocalQuestReborn RuntimeActorInstance");
        this.BrioActorSnapshot.Fields["Actor.ObjectIndex"] = new(actor.ObjectIndex, "LocalQuestReborn RuntimeActorInstance");
        this.BrioActorSnapshot.Fields["Actor.Address"] = new(actor.Address, "LocalQuestReborn RuntimeActorInstance");
        return this.BrioActorSnapshot;
    }

    public string Compare()
    {
        if (this.ReferenceEventNpcSnapshot == null || this.BrioActorSnapshot == null)
        {
            this.LastComparison = "需要先保存真实 EventNpc 快照和 Brio Actor 快照。";
            return this.LastComparison;
        }

        var keys = this.ReferenceEventNpcSnapshot.Fields.Keys
            .Union(this.BrioActorSnapshot.Fields.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string> { "Native EventNpc 字段差异：" };
        foreach (var key in keys)
        {
            var left = this.ReferenceEventNpcSnapshot.Fields.GetValueOrDefault(key, new("未读取", "未确认"));
            var right = this.BrioActorSnapshot.Fields.GetValueOrDefault(key, new("未读取", "未确认"));
            if (!string.Equals(left.Value, right.Value, StringComparison.Ordinal))
                lines.Add($"- {key}: EventNpc={left.Value} [{left.Source}] / Brio={right.Value} [{right.Source}]");
        }

        lines.Add("");
        lines.Add("候选 target/nameplate 字段：ObjectKind、SubKind、IsTargetable、DataId、EventHandler、NameId、NamePlateIconId、HitboxRadius。");
        lines.Add("本轮实验按钮只允许单字段尝试；未确认 native offset 的字段不直接写。");
        this.LastComparison = string.Join(Environment.NewLine, lines);
        return this.LastComparison;
    }

    public static NativeNpcProbeSnapshot CaptureObject(object? obj, string label)
    {
        var snapshot = new NativeNpcProbeSnapshot { Label = label };
        if (obj == null)
        {
            snapshot.Fields["错误"] = new("对象为空", "未确认");
            return snapshot;
        }

        snapshot.Fields["Type"] = new(obj.GetType().FullName ?? obj.GetType().Name, "反射读取");
        foreach (var name in new[]
                 {
                     "Name", "ObjectIndex", "ObjectTableIndex", "Index", "Address", "ObjectKind", "SubKind", "IsTargetable",
                     "Targetable", "DataId", "EntityId", "GameObjectId", "ObjectId", "NameId", "NamePlateIconId",
                     "EventHandler", "EventId", "NpcId", "BaseId", "HitboxRadius", "TargetObjectId", "OwnerId",
                     "Position", "DrawObject", "RenderFlags",
                 })
        {
            snapshot.Fields[name] = ReadField(obj, name);
        }

        return snapshot;
    }

    private static NativeNpcProbeField ReadField(object obj, string name)
    {
        try
        {
            var type = obj.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return new(FormatValue(property.GetValue(obj)), property.GetMethod?.IsPublic == true ? "Dalamud public property" : "反射读取");

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return new(FormatValue(field.GetValue(obj)), field.IsPublic ? "public field" : "反射读取");
        }
        catch (Exception ex)
        {
            return new($"读取失败：{ex.Message}", "反射读取");
        }

        return new("未找到", "未确认");
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
