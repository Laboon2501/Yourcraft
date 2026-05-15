using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigService
{
    private readonly ActorAnimationService animationService;
    private readonly IPluginLog log;
    private readonly ActorAnimationRigResolverProbe resolverProbe;
    private readonly Dictionary<nint, ActorAnimationRigPreset> activeRigByActorPtr = new();
    private readonly Dictionary<string, ActorAnimationRigPreset> activeRigByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RigHookProbeState> hookProbeByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);

    public ActorAnimationRigService(ActorAnimationService animationService, IPluginLog log)
    {
        this.animationService = animationService;
        this.log = log;
        this.resolverProbe = new ActorAnimationRigResolverProbe(log);
        this.animationService.TimelinePlayRequested += this.OnTimelinePlayRequested;
    }

    public bool IsSupported => false;

    public string UnsupportedReason => "No verified animation-only data path override is available in this build. The apply button runs the rig-context probe and never writes appearance Race/Gender/Customize or calls Penumbra redraw.";

    public bool ApplyAnimationRigOverride(RuntimeActorInstance actor, out string reason)
    {
        if (actor.AnimationRigMode == ActorAnimationRigMode.Current || actor.AnimationRigPreset == ActorAnimationRigPreset.Current)
            return this.RestoreAnimationRig(actor, out reason);

        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var beforeHash = BuildAppearanceHash(actor.CharacterObject);
        var animationId = actor.CurrentAnimationId != 0 ? actor.CurrentAnimationId : actor.DefaultAnimationId;
        var probe = this.BeginProbe(actor, actorPtr, animationId);
        var beforeSnapshot = this.resolverProbe.CaptureSnapshot(actor, animationId, actor.AnimationRigPreset, "BeforeReplay");

        var replaySuccess = this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        var afterSnapshot = this.resolverProbe.CaptureSnapshot(actor, animationId, actor.AnimationRigPreset, "AfterReplay");
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        var appearanceChanged = !string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
        probe.AppearanceHashBefore = beforeHash;
        probe.AppearanceHashAfter = afterHash;
        probe.ReplaySuccess = replaySuccess;
        probe.ReplayReason = replayReason;
        var resolverReport = this.resolverProbe.Probe(
            actor,
            beforeSnapshot,
            afterSnapshot,
            probe.HookInstalled,
            probe.HookHitCount,
            probe.OwnerActorPointer != 0 && probe.OwnerActorPointer == probe.ActorPointer,
            replaySuccess,
            appearanceChanged);
        probe.ResolverResult = resolverReport.Result;
        probe.ResolverReason = resolverReport.Reason;
        probe.ResolverNextProbeTarget = resolverReport.NextProbeTarget;
        probe.ChangedAnimationDataPath = resolverReport.AnimationDataPathChanged;
        probe.AnimationBindingChanged = resolverReport.AnimationBindingChanged;
        actor.AnimationRigDebugReport = resolverReport.DetailedReport;

        if (appearanceChanged)
        {
            this.ClearActiveRigContext(actor, actorPtr);
            actor.HasAnimationRigNativeOverride = false;
            actor.AnimationRigStatus = $"Reverted: ActionTimeline replay changed appearance hash unexpectedly. {probe.ToStatus()}";
            reason = actor.AnimationRigStatus;
            this.log.Error("[AnimationRig] appearance changed during rig context probe. actor={Actor} rig={Rig} before={Before} after={After} summary={Summary} details={Details}",
                actor.RuntimeId,
                actor.AnimationRigPreset,
                beforeHash,
                afterHash,
                probe.ToStatus(),
                actor.AnimationRigDebugReport);
            return false;
        }

        actor.HasAnimationRigNativeOverride = false;
        var resultKind = "NoEffect";
        var missing = probe.HookHitCount > 0
            ? "已命中当前 Actor 的动画播放链，并成功重播动画，但尚未找到安全的动画骨架/数据路径字段，所以骨架没有生效"
            : "current replay did not pass through the managed ActionTimeline hook";
        actor.AnimationRigStatus = $"{resultKind}: selected={actor.AnimationRigPreset}; {missing}. {probe.ToStatus()}";
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] rig resolver probe completed. actor={Actor} rig={Rig} result={Result} summary={Summary} details={Details}",
            actor.RuntimeId,
            actor.AnimationRigPreset,
            resultKind,
            probe.ToStatus(),
            actor.AnimationRigDebugReport);
        return false;
    }

    public bool RestoreAnimationRig(RuntimeActorInstance actor, out string reason)
    {
        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var beforeHash = BuildAppearanceHash(actor.CharacterObject);
        this.ClearActiveRigContext(actor, actorPtr);
        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigMode = ActorAnimationRigMode.Current;
        actor.AnimationRigPreset = ActorAnimationRigPreset.Current;
        this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        actor.AnimationRigStatus = $"Current: rig context cleared. appearanceHashBefore={beforeHash}; appearanceHashAfter={afterHash}; replay={replayReason}";
        actor.AnimationRigDebugReport = actor.AnimationRigStatus;
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] restore current actor={Actor} ptr={Pointer} before={Before} after={After} replay={Replay}",
            actor.RuntimeId,
            FormatPointer(actorPtr),
            beforeHash,
            afterHash,
            replayReason);
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

    public void DumpLastDebugReport(RuntimeActorInstance actor)
    {
        var report = string.IsNullOrWhiteSpace(actor.AnimationRigDebugReport)
            ? actor.AnimationRigStatus
            : actor.AnimationRigDebugReport;
        this.log.Information("[AnimationRig] manual debug report dump actor={Actor} report={Report}", actor.RuntimeId, report);
    }

    private RigHookProbeState BeginProbe(RuntimeActorInstance actor, nint actorPtr, uint animationId)
    {
        var probe = new RigHookProbeState
        {
            SelectedPreset = actor.AnimationRigPreset,
            ActorPointer = actorPtr,
            ActorRuntimeId = actor.RuntimeId,
            TimelineId = animationId,
            Method = "ActionTimelineManagedHookProbe",
            HookInstalled = true,
            HookHitCount = 0,
            ChangedAnimationDataPath = false,
        };

        this.activeRigByActorInstanceId[actor.RuntimeId] = actor.AnimationRigPreset;
        if (actorPtr != 0)
            this.activeRigByActorPtr[actorPtr] = actor.AnimationRigPreset;
        this.hookProbeByActorInstanceId[actor.RuntimeId] = probe;
        return probe;
    }

    private void ClearActiveRigContext(RuntimeActorInstance actor, nint actorPtr)
    {
        this.activeRigByActorInstanceId.Remove(actor.RuntimeId);
        this.hookProbeByActorInstanceId.Remove(actor.RuntimeId);
        if (actorPtr != 0)
            this.activeRigByActorPtr.Remove(actorPtr);
    }

    private void OnTimelinePlayRequested(RuntimeActorInstance actor, uint timelineId, string route)
    {
        if (!this.activeRigByActorInstanceId.TryGetValue(actor.RuntimeId, out var preset))
            return;

        if (!this.hookProbeByActorInstanceId.TryGetValue(actor.RuntimeId, out var probe))
        {
            probe = new RigHookProbeState
            {
                ActorRuntimeId = actor.RuntimeId,
                SelectedPreset = preset,
                Method = "ActionTimelineManagedHookProbe",
                HookInstalled = true,
            };
            this.hookProbeByActorInstanceId[actor.RuntimeId] = probe;
        }

        probe.HookHitCount++;
        probe.OwnerActorPointer = TryReadAddress(actor.Address, out var actorPtr) ? actorPtr : 0;
        probe.Slot = route;
        probe.TimelineId = timelineId;
        probe.ResolvedRigBefore = "Actor current animation data path (native field unknown)";
        probe.ResolvedRigAfter = preset.ToString();
        this.log.Information("[AnimationRig] managed ActionTimeline hook hit actor={Actor} ptr={Pointer} route={Route} timeline={Timeline} rig={Rig}",
            actor.RuntimeId,
            FormatPointer(probe.OwnerActorPointer),
            route,
            timelineId,
            preset);
    }

    private static bool TryReadAddress(string? rawAddress, out nint address)
    {
        address = 0;
        var raw = rawAddress?.Trim() ?? string.Empty;
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

    private static string FormatPointer(nint pointer) => pointer == 0 ? "0x0" : $"0x{pointer.ToInt64():X}";

    private sealed class RigHookProbeState
    {
        public string Method { get; init; } = "ActionTimelineManagedHookProbe";

        public string ActorRuntimeId { get; init; } = string.Empty;

        public nint ActorPointer { get; init; }

        public nint OwnerActorPointer { get; set; }

        public ActorAnimationRigPreset SelectedPreset { get; init; } = ActorAnimationRigPreset.Current;

        public uint TimelineId { get; set; }

        public string Slot { get; set; } = "Base";

        public bool HookInstalled { get; init; }

        public int HookHitCount { get; set; }

        public string ResolvedRigBefore { get; set; } = "unknown";

        public string ResolvedRigAfter { get; set; } = "unknown";

        public bool ChangedAnimationDataPath { get; set; }

        public bool ReplaySuccess { get; set; }

        public string ReplayReason { get; set; } = string.Empty;

        public string AppearanceHashBefore { get; set; } = string.Empty;

        public string AppearanceHashAfter { get; set; } = string.Empty;

        public bool AnimationBindingChanged { get; set; }

        public string ResolverResult { get; set; } = "Unknown";

        public string ResolverReason { get; set; } = string.Empty;

        public string ResolverNextProbeTarget { get; set; } = string.Empty;

        public string ToStatus()
            => $"method={this.Method}; hookInstalled={this.HookInstalled}; hookHitCount={this.HookHitCount}; hookOwnerMatched={this.OwnerActorPointer != 0 && this.OwnerActorPointer == this.ActorPointer}; timelineId={this.TimelineId}; selectedRig={this.SelectedPreset}; animationDataPathChanged={this.ChangedAnimationDataPath}; animationBindingChanged={this.AnimationBindingChanged}; appearanceChanged={this.AppearanceHashBefore != this.AppearanceHashAfter}; replaySuccess={this.ReplaySuccess}; result={this.ResolverResult}; nextProbeTarget={this.ResolverNextProbeTarget}; reason={this.ResolverReason}";
    }

}
