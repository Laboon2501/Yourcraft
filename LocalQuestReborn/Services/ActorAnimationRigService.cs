using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigService
{
    private readonly ActorAnimationService animationService;
    private readonly IPluginLog log;

    public ActorAnimationRigService(ActorAnimationService animationService, IPluginLog log)
    {
        this.animationService = animationService;
        this.log = log;
    }

    public bool IsSupported => false;

    public string UnsupportedReason => "No verified animation-only data path override is available in this build. The apply button remains active for diagnostics, but it will not write appearance Race/Gender/Customize or call Penumbra redraw.";

    public bool ApplyAnimationRigOverride(RuntimeActorInstance actor, out string reason)
    {
        if (actor.AnimationRigMode == ActorAnimationRigMode.Current || actor.AnimationRigPreset == ActorAnimationRigPreset.Current)
            return this.RestoreAnimationRig(actor, out reason);

        var beforeHash = BuildAppearanceHash(actor.CharacterObject);
        var animationId = actor.CurrentAnimationId != 0 ? actor.CurrentAnimationId : actor.DefaultAnimationId;
        var attemptedMethod = "ProbeOnly: searched for a verified animation-only rig/data-path override; no safe field is registered for this build. Replaying current ActionTimeline without Penumbra/redraw/customize writes.";

        var replaySuccess = this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        var appearanceChanged = !string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
        if (appearanceChanged)
        {
            actor.HasAnimationRigNativeOverride = false;
            actor.AnimationRigStatus = $"Reverted: rig probe changed appearance hash unexpectedly. before={beforeHash}; after={afterHash}; method={attemptedMethod}; replay={replayReason}";
            reason = actor.AnimationRigStatus;
            this.log.Error("[AnimationRig] appearance changed during rig probe. actor={Actor} rig={Rig} before={Before} after={After} replaySuccess={ReplaySuccess} replay={Replay}",
                actor.RuntimeId,
                actor.AnimationRigPreset,
                beforeHash,
                afterHash,
                replaySuccess,
                replayReason);
            return false;
        }

        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigStatus = $"Unsupported/NoEffect: selected={actor.AnimationRigPreset}; animationId={animationId}; method={attemptedMethod}; appearanceHashBefore={beforeHash}; appearanceHashAfter={afterHash}; replaySuccess={replaySuccess}; replay={replayReason}";
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] apply probe completed. actor={Actor} rig={Rig} animationId={AnimationId} result=UnsupportedNoEffect appearanceHash={Hash} replaySuccess={ReplaySuccess} replay={Replay}",
            actor.RuntimeId,
            actor.AnimationRigPreset,
            animationId,
            beforeHash,
            replaySuccess,
            replayReason);
        return false;
    }

    public bool RestoreAnimationRig(RuntimeActorInstance actor, out string reason)
    {
        var beforeHash = BuildAppearanceHash(actor.CharacterObject);
        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigMode = ActorAnimationRigMode.Current;
        actor.AnimationRigPreset = ActorAnimationRigPreset.Current;
        this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        actor.AnimationRigStatus = $"Current: native animation rig override cleared. appearanceHashBefore={beforeHash}; appearanceHashAfter={afterHash}; replay={replayReason}";
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] restore current actor={Actor} before={Before} after={After} replay={Replay}", actor.RuntimeId, beforeHash, afterHash, replayReason);
        return string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
    }

    public bool ReapplyCurrentAnimationWithRig(RuntimeActorInstance actor, out string reason)
    {
        var animationId = actor.CurrentAnimationId != 0 ? actor.CurrentAnimationId : actor.DefaultAnimationId;
        if (animationId == 0)
        {
            reason = "No current/default animation to replay.";
            return false;
        }

        return this.animationService.PlayTransientTimeline(actor, animationId, out reason);
    }

    private static string BuildAppearanceHash(object? source)
    {
        if (source == null)
            return "unavailable:null";

        try
        {
            var parts = new List<string>();
            var type = source.GetType();
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field) || !LooksLikeAppearanceMember(member.Name))
                    continue;

                object? value = null;
                try
                {
                    value = member switch
                    {
                        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(source),
                        FieldInfo field => field.GetValue(source),
                        _ => null,
                    };
                }
                catch
                {
                    continue;
                }

                parts.Add($"{member.Name}={FormatValue(value)}");
            }

            parts.Sort(StringComparer.Ordinal);
            return parts.Count == 0
                ? $"type={type.FullName}; appearanceMembers=unavailable"
                : StableHash(string.Join(";", parts));
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.Message}";
        }
    }

    private static bool LooksLikeAppearanceMember(string name)
        => name.Contains("Customize", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Race", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Gender", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Sex", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Tribe", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Equip", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("MainHand", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("OffHand", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("DrawData", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Armor", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Stain", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Dye", StringComparison.OrdinalIgnoreCase);

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "null";
        if (value is string text)
            return text;
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? "null");
                if (items.Count >= 32)
                    break;
            }

            return "[" + string.Join(",", items) + "]";
        }

        return value.ToString() ?? value.GetType().Name;
    }

    private static string StableHash(string text)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= prime;
            }

            return hash.ToString("X16");
        }
    }
}
