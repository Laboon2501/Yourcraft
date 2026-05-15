using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigService
{
    private readonly ActorAnimationService animationService;
    private readonly IPluginLog log;
    private readonly AnimationDataPathProbeService dataPathProbe;
    private readonly ActorDataPathOverrideService dataPathOverride;
    private readonly Dictionary<nint, ActorAnimationRigPreset> activeRigByActorPtr = new();
    private readonly Dictionary<string, ActorAnimationRigPreset> activeRigByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RigHookProbeState> hookProbeByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationDataPathDump> pathBeforeDumpByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationDataPathDump> pathAfterDumpByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);

    public ActorAnimationRigService(ActorAnimationService animationService, IPluginLog log)
    {
        this.animationService = animationService;
        this.log = log;
        this.dataPathProbe = new AnimationDataPathProbeService(log);
        this.dataPathOverride = new ActorDataPathOverrideService(log);
        this.animationService.TimelinePlayRequested += this.OnTimelinePlayRequested;
    }

    public bool IsSupported => false;

    public string UnsupportedReason => "No verified animation-only data path override is available in this build. Apply is read-only: it registers selectedRig as debug context and runs the Anamnesis-derived DataPath candidate scanner.";

    public bool ApplyAnimationRigOverride(RuntimeActorInstance actor, out string reason)
    {
        if (actor.AnimationRigMode == ActorAnimationRigMode.Current || actor.AnimationRigPreset == ActorAnimationRigPreset.Current)
            return this.RestoreAnimationRig(actor, out reason);

        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var animationId = GetCurrentAnimationId(actor);
        var probe = this.BeginProbe(actor, actorPtr, animationId);
        var dump = this.dataPathProbe.DumpCurrentActorAnimationState(actor, animationId, actor.AnimationRigPreset, "RigApplyReadOnly");
        this.UpdateExperimentalDataPathState(actor, dump);

        actor.HasAnimationRigNativeOverride = false;
        probe.ResolverResult = "ProbeOnly";
        probe.ResolverReason = "Read-only定位阶段：已注册 selectedRig debug context，并 dump 当前 Actor 的动画路径状态；未重播 Timeline，未写 native，未调用 Penumbra redraw。";
        probe.ResolverNextProbeTarget = "Use Anamnesis Diff or dual-actor same-timeline comparison to locate animation resource resolver fields.";
        actor.AnimationPathResolverStatus = $"ProbeOnly: timeline={animationId}; selectedRig={actor.AnimationRigPreset}; bindingHash={dump.AnimationBindingHash}; candidateCount={dump.CandidateCount}; appearanceHash={dump.AppearanceHash}";
        actor.AnimationRigStatus = $"ProbeOnly: selected={actor.AnimationRigPreset}; 当前阶段只读定位，不应用骨架。ActionTimeline replay 没有执行；animation resource/data-path resolver 仍未知。 {probe.ToStatus()}";
        probe.ResolverReason = "Read-only source-trace/candidate-scan stage. selectedRig is recorded as debug context; no timeline replay, native write, Race/Gender/Customize write, actor redraw, or Penumbra redraw is performed.";
        probe.ResolverNextProbeTarget = "Validate Anamnesis ActorModelMemory.DataPath(+0xAA0)/DataHead(+0xAA4) candidates with guarded experimental mode only after read-only evidence is sufficient.";
        actor.AnimationPathResolverStatus = $"ProbeOnly: timeline={animationId}; selectedRig={actor.AnimationRigPreset}; bindingHash={dump.AnimationBindingHash}; dataPathCandidates={dump.DataPathCandidateCount}; appearanceHash={dump.AppearanceHash}";
        actor.AnimationRigStatus = $"ProbeOnly: selected={actor.AnimationRigPreset}; Anamnesis source trace points to ActorModelMemory.DataPath/DataHead, but this apply path is read-only and does not change rig yet. {probe.ToStatus()}";
        actor.AnimationRigDebugReport = string.Join(Environment.NewLine, new[]
        {
            actor.AnimationRigStatus,
            dump.Report,
        });
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] read-only rig context registered. actor={Actor} rig={Rig} summary={Summary} details={Details}",
            actor.RuntimeId,
            actor.AnimationRigPreset,
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
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        actor.AnimationRigStatus = $"Current: rig debug context cleared. No timeline replay or native write was performed. appearanceHashBefore={beforeHash}; appearanceHashAfter={afterHash}";
        actor.AnimationPathResolverStatus = "Rig context cleared; AnimationDataPathProbe remains read-only.";
        actor.AnimationRigDebugReport = actor.AnimationRigStatus;
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] restore current actor={Actor} ptr={Pointer} before={Before} after={After}; no replay/native write",
            actor.RuntimeId,
            FormatPointer(actorPtr),
            beforeHash,
            afterHash);
        return string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
    }

    public bool ReapplyCurrentAnimationWithRig(RuntimeActorInstance actor, out string reason)
    {
        var animationId = GetCurrentAnimationId(actor);
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

    public void DumpExperimentalDataPathState(RuntimeActorInstance actor)
    {
        var animationId = GetCurrentAnimationId(actor);
        var dump = this.dataPathProbe.DumpCurrentActorAnimationState(actor, animationId, this.ResolveExperimentalTargetRig(actor), "ExperimentalDataPathDump");
        this.UpdateExperimentalDataPathState(actor, dump);
        actor.ExperimentalDataPathLastResult = $"DumpCurrentDataPath: {dump.DataPathReadSummary}; target={FormatNullableDataPath(actor.TargetDataPath)}/{FormatNullableDataHead(actor.TargetDataHead)}; mapping={actor.ExperimentalDataPathMappingStatus}";
        actor.ExperimentalDataPathTestReport = string.Join(Environment.NewLine, new[]
        {
            actor.ExperimentalDataPathLastResult,
            dump.Report,
        });
        this.log.Information("[DataPathOverrideTest] dump actor={Actor} report={Report}", actor.RuntimeId, actor.ExperimentalDataPathTestReport);
    }

    public bool ApplyExperimentalDataPathOnce(RuntimeActorInstance actor, out string reason)
        => this.ApplyExperimentalDataPath(actor, replayTimeline: false, out reason);

    public bool ApplyExperimentalDataPathAndReplay(RuntimeActorInstance actor, out string reason)
        => this.ApplyExperimentalDataPath(actor, replayTimeline: true, out reason);

    public bool RestoreOriginalDataPath(RuntimeActorInstance actor, out string reason)
    {
        var restored = this.dataPathOverride.TryRestore(actor, out reason);
        var dump = this.dataPathProbe.DumpCurrentActorAnimationState(actor, GetCurrentAnimationId(actor), this.ResolveExperimentalTargetRig(actor), "RestoreOriginalDataPath");
        this.UpdateExperimentalDataPathState(actor, dump);
        actor.ExperimentalDataPathRestored = restored;
        actor.ExperimentalDataPathLastResult = restored ? $"RestoreOriginalDataPath: {reason}" : $"RestoreOriginalDataPath failed/skipped: {reason}";
        actor.ExperimentalDataPathTestReport = string.Join(Environment.NewLine, new[]
        {
            "[DataPathOverrideTest]",
            $"actor={actor.RuntimeId}",
            $"result={actor.ExperimentalDataPathLastResult}",
            dump.Report,
        });
        this.log.Information("[DataPathOverrideTest] restore actor={Actor} result={Result} report={Report}", actor.RuntimeId, reason, actor.ExperimentalDataPathTestReport);
        return restored;
    }

    public void DumpAnimationPathBeforeExternalChange(RuntimeActorInstance actor)
    {
        var animationId = GetCurrentAnimationId(actor);
        var dump = this.dataPathProbe.DumpBeforeExternalChange(actor, animationId, actor.AnimationRigPreset);
        this.UpdateExperimentalDataPathState(actor, dump);
        this.pathBeforeDumpByActorInstanceId[actor.RuntimeId] = dump;
        actor.AnimationPathResolverStatus = $"Before dump captured: timeline={animationId}; bindingHash={dump.AnimationBindingHash}; appearanceHash={dump.AppearanceHash}; candidateCount={dump.CandidateCount}";
        actor.AnimationRigDebugReport = dump.Report;
        this.log.Information("[AnimationRig] Animation path before-external-change dump actor={Actor} report={Report}", actor.RuntimeId, dump.Report);
    }

    public void DumpAnimationPathAfterExternalChange(RuntimeActorInstance actor)
    {
        var animationId = GetCurrentAnimationId(actor);
        var dump = this.dataPathProbe.DumpAfterExternalChange(actor, animationId, actor.AnimationRigPreset);
        this.UpdateExperimentalDataPathState(actor, dump);
        this.pathAfterDumpByActorInstanceId[actor.RuntimeId] = dump;
        actor.AnimationPathResolverStatus = $"After dump captured: timeline={animationId}; bindingHash={dump.AnimationBindingHash}; appearanceHash={dump.AppearanceHash}; candidateCount={dump.CandidateCount}";
        actor.AnimationRigDebugReport = dump.Report;
        this.log.Information("[AnimationRig] Animation path after-external-change dump actor={Actor} report={Report}", actor.RuntimeId, dump.Report);
    }

    public bool CompareExternalAnimationPathDumps(RuntimeActorInstance actor, out string reason)
    {
        if (!this.pathBeforeDumpByActorInstanceId.TryGetValue(actor.RuntimeId, out var before))
        {
            reason = "No Before dump. Click Dump Rig State Before External Change first.";
            actor.AnimationPathResolverStatus = reason;
            return false;
        }

        if (!this.pathAfterDumpByActorInstanceId.TryGetValue(actor.RuntimeId, out var after))
        {
            reason = "No After dump. Click Dump Rig State After External Change first.";
            actor.AnimationPathResolverStatus = reason;
            return false;
        }

        var diff = this.dataPathProbe.CompareRigDumps(before, after, "ExternalAnamnesisChange");
        actor.AnimationPathResolverStatus = $"External diff: result={diff.Result}; bindingChanged={diff.AnimationBindingChanged}; skeletonChanged={diff.SkeletonPointerChanged || diff.SkeletonResourceChanged}; animationResourceChanged={diff.AnimationResourceChanged}; candidateChanged={diff.CandidateFieldsChanged}; appearanceChanged={diff.AppearanceChanged}; transformChanged={diff.TransformChanged}";
        actor.AnimationRigDebugReport = diff.Report;
        reason = actor.AnimationPathResolverStatus;
        return true;
    }

    public bool CompareAnimationPathWithActor(RuntimeActorInstance actor, RuntimeActorInstance otherActor, out string reason)
    {
        var animationId = GetCurrentAnimationId(actor);
        if (animationId == 0)
        {
            reason = "No current/default timeline for actor A.";
            actor.AnimationPathResolverStatus = reason;
            return false;
        }

        var diff = this.dataPathProbe.CompareTwoActorsSameTimeline(actor, otherActor, animationId, out var actorDump, out var otherDump);
        actor.AnimationPathResolverStatus = $"Dual actor compare read-only: timeline={animationId}; bindingChanged={diff.AnimationBindingChanged}; skeletonChanged={diff.SkeletonPointerChanged || diff.SkeletonResourceChanged}; animationResourceChanged={diff.AnimationResourceChanged}; candidateChanged={diff.CandidateFieldsChanged}; appearanceChanged={diff.AppearanceChanged}; transformChanged={diff.TransformChanged}";
        actor.AnimationRigDebugReport = string.Join(Environment.NewLine, new[]
        {
            actor.AnimationPathResolverStatus,
            "No timeline replay was performed by this comparison. Play the same timeline on both actors manually first if you need live binding changes.",
            actorDump.Report,
            otherDump.Report,
            diff.Report,
        });
        reason = actor.AnimationPathResolverStatus;
        return true;
    }

    private bool ApplyExperimentalDataPath(RuntimeActorInstance actor, bool replayTimeline, out string reason)
    {
        actor.ExperimentalDataPathRestored = false;
        actor.ExperimentalDataPathAppearanceChanged = false;
        actor.ExperimentalDataPathTransformChanged = false;
        actor.ExperimentalDataPathBindingChanged = false;

        if (!actor.EnableExperimentalDataPathOverride)
        {
            reason = "Experimental DataPath override is disabled. Enable the checkbox before applying.";
            actor.ExperimentalDataPathLastResult = reason;
            return false;
        }

        var timelineId = GetCurrentAnimationId(actor);
        if (replayTimeline && timelineId == 0)
        {
            reason = "No current/default timeline to replay.";
            actor.ExperimentalDataPathLastResult = reason;
            return false;
        }

        var targetRig = this.ResolveExperimentalTargetRig(actor);
        var before = this.dataPathProbe.DumpCurrentActorAnimationState(actor, timelineId, targetRig, replayTimeline ? "DataPathReplayBefore" : "DataPathApplyBefore");
        this.UpdateExperimentalDataPathState(actor, before);
        if (!this.dataPathOverride.TryResolveTarget(actor, targetRig, out var targetDataPath, out var targetDataHead, out var mappingReason))
        {
            reason = mappingReason;
            actor.ExperimentalDataPathMappingStatus = mappingReason;
            actor.ExperimentalDataPathLastResult = reason;
            return false;
        }

        actor.TargetDataPath = targetDataPath;
        actor.TargetDataHead = targetDataHead;
        actor.ExperimentalDataPathMappingStatus = mappingReason;

        if (!this.dataPathOverride.TryCreateSnapshot(actor, before, out _, out var snapshotReason))
        {
            reason = snapshotReason;
            actor.ExperimentalDataPathLastResult = reason;
            return false;
        }

        if (!this.dataPathOverride.TryWrite(actor, targetDataPath, targetDataHead, out var writeReason))
        {
            reason = writeReason;
            actor.ExperimentalDataPathLastResult = reason;
            return false;
        }

        string replayReason = "not requested";
        var replaySuccess = true;
        if (replayTimeline)
            replaySuccess = this.animationService.PlayTransientTimeline(actor, timelineId, out replayReason);

        var after = this.dataPathProbe.DumpCurrentActorAnimationState(actor, timelineId, targetRig, replayTimeline ? "DataPathReplayAfter" : "DataPathApplyAfter");
        this.UpdateExperimentalDataPathState(actor, after);

        var appearanceChanged = !StringEquals(before.AppearanceHash, after.AppearanceHash);
        var transformChanged = !StringEquals(before.TransformHash, after.TransformHash);
        var bindingChanged = !StringEquals(before.AnimationBindingHash, after.AnimationBindingHash);
        var dataPathHitTarget = after.CurrentDataPath == targetDataPath && after.CurrentDataHead == targetDataHead;

        actor.ExperimentalDataPathAppearanceChanged = appearanceChanged;
        actor.ExperimentalDataPathTransformChanged = transformChanged;
        actor.ExperimentalDataPathBindingChanged = bindingChanged;

        var result = DetermineDataPathResult(
            dataPathHitTarget,
            appearanceChanged,
            transformChanged,
            bindingChanged,
            replayTimeline,
            replaySuccess);

        var shouldRestore = appearanceChanged ||
            transformChanged ||
            (actor.ExperimentalDataPathRestoreAfterTest && !actor.ExperimentalDataPathKeepOverrideUntilRestore);
        var restoreReason = "not requested";
        if (shouldRestore)
            actor.ExperimentalDataPathRestored = this.dataPathOverride.TryRestore(actor, out restoreReason);

        reason = result;
        actor.ExperimentalDataPathLastResult = result;
        actor.ExperimentalDataPathTestReport = BuildDataPathTestReport(
            actor,
            targetRig,
            targetDataPath,
            targetDataHead,
            before,
            after,
            writeReason,
            replayTimeline,
            replaySuccess,
            replayReason,
            restoreReason,
            actor.ExperimentalDataPathRestored,
            result);
        actor.AnimationRigDebugReport = actor.ExperimentalDataPathTestReport;
        this.log.Information("{Report}", actor.ExperimentalDataPathTestReport);
        return result is "WriteSuccess" or "SuccessCandidate" or "NoEffect";
    }

    private void UpdateExperimentalDataPathState(RuntimeActorInstance actor, AnimationDataPathDump dump)
    {
        actor.CurrentDataPath = dump.CurrentDataPath;
        actor.CurrentDataHead = dump.CurrentDataHead;
        var targetRig = this.ResolveExperimentalTargetRig(actor);
        if (this.dataPathOverride.TryResolveTarget(actor, targetRig, out var targetDataPath, out var targetDataHead, out var mappingReason))
        {
            actor.TargetDataPath = targetDataPath;
            actor.TargetDataHead = targetDataHead;
            actor.ExperimentalDataPathMappingStatus = mappingReason;
        }
        else
        {
            actor.TargetDataPath = null;
            actor.TargetDataHead = null;
            actor.ExperimentalDataPathMappingStatus = mappingReason;
        }
    }

    private ActorAnimationRigPreset ResolveExperimentalTargetRig(RuntimeActorInstance actor)
    {
        if (actor.ExperimentalDataPathTargetRig != ActorAnimationRigPreset.Current)
            return actor.ExperimentalDataPathTargetRig;

        return actor.AnimationRigPreset != ActorAnimationRigPreset.Current
            ? actor.AnimationRigPreset
            : ActorAnimationRigPreset.Current;
    }

    private static string DetermineDataPathResult(
        bool dataPathHitTarget,
        bool appearanceChanged,
        bool transformChanged,
        bool bindingChanged,
        bool replayTimeline,
        bool replaySuccess)
    {
        if (appearanceChanged)
            return "UnsafeAppearanceChanged";
        if (transformChanged)
            return "UnsafeTransformChanged";
        if (!dataPathHitTarget)
            return "WriteReadbackMismatch";
        if (replayTimeline && !replaySuccess)
            return "ReplayFailed";
        if (replayTimeline && bindingChanged)
            return "SuccessCandidate";
        if (replayTimeline)
            return "NoEffect";

        return "WriteSuccess";
    }

    private static string BuildDataPathTestReport(
        RuntimeActorInstance actor,
        ActorAnimationRigPreset targetRig,
        short targetDataPath,
        byte targetDataHead,
        AnimationDataPathDump before,
        AnimationDataPathDump after,
        string writeReason,
        bool replayTimeline,
        bool replaySuccess,
        string replayReason,
        string restoreReason,
        bool restored,
        string result)
        => string.Join(Environment.NewLine, new[]
        {
            "[DataPathOverrideTest]",
            $"actor={actor.RuntimeId}",
            $"drawObject={FormatPointer(before.DrawObjectPtr)}",
            $"selectedRig={targetRig}",
            $"timelineId={before.TimelineId}",
            $"originalDataPath={FormatNullableDataPath(before.CurrentDataPath)}",
            $"originalDataHead={FormatNullableDataHead(before.CurrentDataHead)}",
            $"targetDataPath={FormatNullableDataPath(targetDataPath)}",
            $"targetDataHead={FormatNullableDataHead(targetDataHead)}",
            $"afterDataPath={FormatNullableDataPath(after.CurrentDataPath)}",
            $"afterDataHead={FormatNullableDataHead(after.CurrentDataHead)}",
            $"appearanceHashBefore={before.AppearanceHash}",
            $"appearanceHashAfter={after.AppearanceHash}",
            $"transformHashBefore={before.TransformHash}",
            $"transformHashAfter={after.TransformHash}",
            $"bindingHashBefore={before.AnimationBindingHash}",
            $"bindingHashAfter={after.AnimationBindingHash}",
            $"appearanceChanged={!StringEquals(before.AppearanceHash, after.AppearanceHash)}",
            $"transformChanged={!StringEquals(before.TransformHash, after.TransformHash)}",
            $"bindingChanged={!StringEquals(before.AnimationBindingHash, after.AnimationBindingHash)}",
            $"writeReason={writeReason}",
            $"replayTimeline={replayTimeline}",
            $"replaySuccess={replaySuccess}",
            $"replayReason={replayReason}",
            $"result={result}",
            $"restored={restored}",
            $"restoreReason={restoreReason}",
        });

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static string FormatNullableDataPath(short? value)
        => value.HasValue ? $"{value.Value} (0x{unchecked((ushort)value.Value):X4})" : "unavailable";

    private static string FormatNullableDataHead(byte? value)
        => value.HasValue ? $"{value.Value} (0x{value.Value:X2})" : "unavailable";

    private RigHookProbeState BeginProbe(RuntimeActorInstance actor, nint actorPtr, uint animationId)
    {
        var probe = new RigHookProbeState
        {
            SelectedPreset = actor.AnimationRigPreset,
            ActorPointer = actorPtr,
            ActorRuntimeId = actor.RuntimeId,
            TimelineId = animationId,
            Method = "AnimationDataPathReadOnlyProbe",
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

        probe.Method = "ActionTimelineManagedHookProbe";
        probe.HookHitCount++;
        probe.OwnerActorPointer = TryReadAddress(actor.Address, out var actorPtr) ? actorPtr : 0;
        probe.Slot = route;
        probe.TimelineId = timelineId;
        probe.ResolvedRigBefore = "Actor current animation data path (native field unknown)";
        probe.ResolvedRigAfter = preset.ToString();
        probe.ResolverResult = "ProbeOnly";
        probe.ResolverReason = "ActionTimeline hook hit, but no animation data-path resolver override is registered.";
        probe.ResolverNextProbeTarget = "Use DataPath candidate scanner report; do not treat ActionTimeline replay as rig success.";
        actor.AnimationRigStatus = $"ProbeOnly: ActionTimeline hook hit for timeline={timelineId}, but selected rig was not applied to resource resolver. {probe.ToStatus()}";
        this.log.Information("[AnimationRig] managed ActionTimeline hook hit actor={Actor} ptr={Pointer} route={Route} timeline={Timeline} rig={Rig}",
            actor.RuntimeId,
            FormatPointer(probe.OwnerActorPointer),
            route,
            timelineId,
            preset);
    }

    private static uint GetCurrentAnimationId(RuntimeActorInstance actor)
        => actor.CurrentAnimationId != 0 ? actor.CurrentAnimationId : actor.DefaultAnimationId;

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

                object? value;
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
        public string Method { get; set; } = "AnimationDataPathReadOnlyProbe";

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

        public string ReplayReason { get; set; } = "No replay in read-only apply.";

        public string AppearanceHashBefore { get; set; } = string.Empty;

        public string AppearanceHashAfter { get; set; } = string.Empty;

        public bool AnimationBindingChanged { get; set; }

        public string ResolverResult { get; set; } = "ProbeOnly";

        public string ResolverReason { get; set; } = string.Empty;

        public string ResolverNextProbeTarget { get; set; } = string.Empty;

        public string ToStatus()
            => $"method={this.Method}; hookInstalled={this.HookInstalled}; hookHitCount={this.HookHitCount}; hookOwnerMatched={this.OwnerActorPointer != 0 && this.OwnerActorPointer == this.ActorPointer}; timelineId={this.TimelineId}; selectedRig={this.SelectedPreset}; animationDataPathChanged={this.ChangedAnimationDataPath}; animationBindingChanged={this.AnimationBindingChanged}; appearanceChanged={this.AppearanceHashBefore != this.AppearanceHashAfter}; replaySuccess={this.ReplaySuccess}; result={this.ResolverResult}; nextProbeTarget={this.ResolverNextProbeTarget}; reason={this.ResolverReason}";
    }
}
