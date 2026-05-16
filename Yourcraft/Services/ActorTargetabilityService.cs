using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class ActorTargetabilityService
{
    private readonly ITargetManager targetManager;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public ActorTargetabilityService(ITargetManager targetManager, BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.targetManager = targetManager;
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public bool TryMakeTargetable(RuntimeActorInstance actor)
    {
        actor.HoverOrTargetDebugInfo = "已禁用写入：请使用“取证：对比真实 NPC 和此 Actor”查看字段差异。本轮不写 ObjectKind/SubKind/DataId/EventHandler/Targetable flag。";
        this.TryReadTargetability(actor);
        return false;
    }

    public void TryReadTargetability(RuntimeActorInstance actor)
    {
        var source = actor.CharacterObject;
        if (source == null)
        {
            actor.HoverOrTargetDebugInfo = "characterObject 为空。";
            return;
        }

        actor.ObjectKindReadback = ReadMember(source, "ObjectKind");
        actor.SubKindReadback = ReadMember(source, "SubKind");
        actor.DataIdReadback = ReadMember(source, "DataId");
        actor.EntityIdReadback = ReadMember(source, "EntityId", "GameObjectId");
        actor.IsTargetableReadback = ReadMember(source, "IsTargetable", "Targetable");
        if (actor.IsTargetableReadback == "未知")
            actor.IsTargetableReadback = "未发现公开 IsTargetable/Targetable 属性";
    }

    public bool TryMatchCurrentTarget(RuntimeActorInstance actor)
    {
        var targetIndex = ReadMember(this.targetManager.Target, "ObjectIndex", "ObjectTableIndex", "Index");
        actor.CurrentTargetMatched = ObjectIndexMatches(targetIndex, actor.ObjectIndex);
        actor.HoverOrTargetDebugInfo = actor.CurrentTargetMatched
            ? $"当前 Target 匹配此 Actor：ObjectIndex={targetIndex}"
            : $"当前 Target 不匹配。Target ObjectIndex={targetIndex}，Actor ObjectIndex={actor.ObjectIndex}";
        return actor.CurrentTargetMatched;
    }

    public bool TrySetCurrentTarget(RuntimeActorInstance actor)
    {
        if (actor.CharacterObject == null)
        {
            actor.HoverOrTargetDebugInfo = "失败：characterObject 为空。";
            return false;
        }

        try
        {
            var property = this.targetManager.GetType().GetProperty("Target", BindingFlags.Instance | BindingFlags.Public);
            if (property is { CanWrite: true })
            {
                property.SetValue(this.targetManager, actor.CharacterObject);
                this.TryMatchCurrentTarget(actor);
                return true;
            }

            actor.HoverOrTargetDebugInfo = "当前 Dalamud ITargetManager 未暴露可写 Target setter。";
            return false;
        }
        catch (Exception ex)
        {
            actor.HoverOrTargetDebugInfo = $"设为当前目标失败：{ex.Message}";
            this.log.Warning(ex, "Failed to set current target. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    private static bool TrySetBoolMember(object source, bool value, out string result, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property is { CanWrite: true } && property.PropertyType == typeof(bool))
                {
                    property.SetValue(source, value);
                    result = $"成功：已通过 {source.GetType().Name}.{name} setter 设置。";
                    return true;
                }
            }
            catch (Exception ex)
            {
                result = $"{name} setter 失败：{ex.Message}";
                return false;
            }
        }

        result = "未找到可写 IsTargetable/Targetable bool 属性。";
        return false;
    }

    private static string ReadMember(object? source, params string[] names)
    {
        if (source == null)
            return "未知";

        foreach (var name in names)
        {
            try
            {
                var type = source.GetType();
                var value = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)
                    ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
                var text = value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch
            {
            }
        }

        return "未知";
    }

    private static bool ObjectIndexMatches(string targetObjectIndex, string actorObjectIndex)
    {
        if (string.Equals(targetObjectIndex, actorObjectIndex, StringComparison.OrdinalIgnoreCase))
            return true;

        return int.TryParse(targetObjectIndex, out var targetIndex)
               && int.TryParse(actorObjectIndex, out var actorIndex)
               && targetIndex == actorIndex;
    }
}
