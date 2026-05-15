using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigService
{
    private readonly ActorAnimationService animationService;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, ActorAnimationRigPreset> activeRigByActorPtr = new();
    private readonly Dictionary<string, ActorAnimationRigPreset> activeRigByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RigHookProbeState> hookProbeByActorInstanceId = new(StringComparer.OrdinalIgnoreCase);

    public ActorAnimationRigService(ActorAnimationService animationService, IPluginLog log)
    {
        this.animationService = animationService;
        this.log = log;
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

        var replaySuccess = this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        var afterHash = BuildAppearanceHash(actor.CharacterObject);
        var appearanceChanged = !string.Equals(beforeHash, afterHash, StringComparison.Ordinal);
        probe.AppearanceHashBefore = beforeHash;
        probe.AppearanceHashAfter = afterHash;
        probe.ReplaySuccess = replaySuccess;
        probe.ReplayReason = replayReason;
        var brioProbe = ProbeBrioRigDetails(actor);
        probe.BrioTimelineCandidates = brioProbe.CandidateSignatures;
        probe.BrioActionTimelineCapabilityFound = brioProbe.ActionTimelineCapabilityFound;
        probe.BrioAffectsSkeletonsMemberFound = brioProbe.AffectsSkeletonsMemberFound;
        probe.BrioActionTimelineCapabilityInstanceFound = brioProbe.ActionTimelineCapabilityInstanceFound;
        probe.BrioCapabilityProbeResult = brioProbe.CapabilityProbeResult;

        if (appearanceChanged)
        {
            this.ClearActiveRigContext(actor, actorPtr);
            actor.HasAnimationRigNativeOverride = false;
            actor.AnimationRigStatus = $"Reverted: ActionTimeline rig probe changed appearance hash unexpectedly. {probe.ToStatus()}; changedAppearance=true";
            reason = actor.AnimationRigStatus;
            this.log.Error("[AnimationRig] appearance changed during rig context probe. actor={Actor} rig={Rig} before={Before} after={After} report={Report}",
                actor.RuntimeId,
                actor.AnimationRigPreset,
                beforeHash,
                afterHash,
                probe.ToStatus());
            return false;
        }

        actor.HasAnimationRigNativeOverride = false;
        var resultKind = probe.HookHitCount > 0 ? "Unsupported" : "NoEffect";
        var missing = probe.HookHitCount > 0
            ? "managed ActionTimeline hook was hit, but no verified animation-resolve/data-path field is registered for this build"
            : "current replay did not pass through the managed ActionTimeline hook";
        actor.AnimationRigStatus = $"{resultKind}: selected={actor.AnimationRigPreset}; {missing}. {probe.ToStatus()}";
        reason = actor.AnimationRigStatus;
        this.log.Information("[AnimationRig] rig context probe completed. actor={Actor} rig={Rig} result={Result} report={Report}",
            actor.RuntimeId,
            actor.AnimationRigPreset,
            resultKind,
            probe.ToStatus());
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

    private static BrioRigProbeDetails ProbeBrioRigDetails(RuntimeActorInstance actor)
    {
        try
        {
            var brioAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name?.Contains("Brio", StringComparison.OrdinalIgnoreCase) == true);
            if (brioAssembly == null)
                return new BrioRigProbeDetails(false, false, false, "Brio assembly not loaded.", string.Empty);

            var types = SafeGetTypes(brioAssembly).ToList();
            var actionTimelineCapabilityType = types.FirstOrDefault(type =>
                string.Equals(type.FullName, "Brio.Capabilities.Actor.ActionTimelineCapability", StringComparison.OrdinalIgnoreCase) ||
                type.FullName?.Contains("ActionTimelineCapability", StringComparison.OrdinalIgnoreCase) == true);
            var actionTimelineFound = actionTimelineCapabilityType != null;
            var affectsFound = types.Any(type => type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(member => member.Name.Contains("AffectsSkeletons", StringComparison.OrdinalIgnoreCase)));

            var signatures = types
                .Where(type =>
                    type.FullName?.Contains("ActionTimelineCapability", StringComparison.OrdinalIgnoreCase) == true ||
                    type.FullName?.Contains("BrioAccessUtils", StringComparison.OrdinalIgnoreCase) == true ||
                    type.FullName?.Contains("SpawnFlags", StringComparison.OrdinalIgnoreCase) == true ||
                    type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(member => member.Name.Contains("AffectsSkeletons", StringComparison.OrdinalIgnoreCase) || LooksLikeTimelineMember(type, member)))
                .SelectMany(type => DescribeInterestingMembers(type).Prepend($"type {type.FullName}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();

            var capabilityProbe = TryGetActionTimelineCapabilityInstance(actor, brioAssembly, actionTimelineCapabilityType, out var capabilityInstance);
            return new BrioRigProbeDetails(
                actionTimelineFound,
                affectsFound,
                capabilityInstance != null,
                capabilityProbe,
                string.Join(" | ", signatures));
        }
        catch (Exception ex)
        {
            return new BrioRigProbeDetails(false, false, false, $"probe failed: {ex.Message}", string.Empty);
        }
    }

    private static IEnumerable<string> DescribeInterestingMembers(Type type)
        => type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member.Name.Contains("AffectsSkeletons", StringComparison.OrdinalIgnoreCase) || LooksLikeTimelineMember(type, member))
            .Take(12)
            .Select(member => $"{type.FullName}.{DescribeMember(member)}");

    private static string DescribeMember(MemberInfo member)
    {
        var visibility = member switch
        {
            MethodBase method when method.IsPublic => "public",
            MethodBase method when method.IsPrivate => "private",
            MethodBase method when method.IsFamily => "protected",
            FieldInfo field when field.IsPublic => "public",
            FieldInfo field when field.IsPrivate => "private",
            FieldInfo field when field.IsFamily => "protected",
            _ => "member",
        };
        var scope = member switch
        {
            MethodBase method when method.IsStatic => " static",
            FieldInfo field when field.IsStatic => " static",
            PropertyInfo property when (property.GetMethod ?? property.SetMethod)?.IsStatic == true => " static",
            _ => string.Empty,
        };

        return member switch
        {
            MethodInfo method => $"{visibility}{scope} {FormatType(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"))})",
            ConstructorInfo ctor => $"{visibility}{scope} {ctor.Name}({string.Join(", ", ctor.GetParameters().Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"))})",
            PropertyInfo property => $"{visibility}{scope} {FormatType(property.PropertyType)} {property.Name} {{ get={property.CanRead}; set={property.CanWrite}; }}",
            FieldInfo field => $"{visibility}{scope} {FormatType(field.FieldType)} {field.Name}",
            _ => $"{visibility}{scope} {member.MemberType} {member.Name}",
        };
    }

    private static string FormatType(Type type)
        => type.FullName ?? type.Name;

    private static string TryGetActionTimelineCapabilityInstance(RuntimeActorInstance actor, Assembly brioAssembly, Type? actionTimelineCapabilityType, out object? capability)
    {
        capability = null;
        if (actor.CharacterObject == null)
            return "actor.CharacterObject=null";
        if (actionTimelineCapabilityType == null)
            return "ActionTimelineCapability type not found";

        try
        {
            var accessUtils = brioAssembly.GetType("Brio.BrioAccessUtils");
            var entityManager = accessUtils?.GetProperty("EntityManager", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            if (entityManager == null)
                return "BrioAccessUtils.EntityManager unavailable";

            var setSelectedEntity = entityManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "SetSelectedEntity")
                        return false;

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(actor.CharacterObject);
                });
            if (setSelectedEntity == null)
                return "EntityManager.SetSelectedEntity(actor) overload not found";

            setSelectedEntity.Invoke(entityManager, [actor.CharacterObject]);
            var tryGetCapability = entityManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "TryGetCapabilityFromSelectedEntity" && method.IsGenericMethodDefinition);
            if (tryGetCapability == null)
                return "TryGetCapabilityFromSelectedEntity<T> not found";

            var args = new object?[] { null, false, true };
            var result = tryGetCapability.MakeGenericMethod(actionTimelineCapabilityType).Invoke(entityManager, args);
            capability = args[0];
            return $"TryGetCapability result={result ?? "null"}; instance={(capability != null ? capability.GetType().FullName : "null")}";
        }
        catch (Exception ex)
        {
            return $"capability probe failed: {ex.Message}";
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
        catch
        {
            return [];
        }
    }

    private static bool LooksLikeTimelineMember(Type type, MemberInfo member)
    {
        static bool HasToken(string value)
            => value.Contains("ActionTimeline", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Sequencer", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Slot", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("DataPath", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Skeleton", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Rig", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ModelType", StringComparison.OrdinalIgnoreCase);

        return HasToken(type.FullName ?? type.Name) || HasToken(member.Name);
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

        public string BrioTimelineCandidates { get; set; } = string.Empty;

        public bool BrioActionTimelineCapabilityFound { get; set; }

        public bool BrioAffectsSkeletonsMemberFound { get; set; }

        public bool BrioActionTimelineCapabilityInstanceFound { get; set; }

        public string BrioCapabilityProbeResult { get; set; } = string.Empty;

        public string ToStatus()
            => $"method={this.Method}; hookInstalled={this.HookInstalled}; hookHitCount={this.HookHitCount}; hookOwnerMatched={this.OwnerActorPointer != 0 && this.OwnerActorPointer == this.ActorPointer}; actorPtr={FormatPointer(this.ActorPointer)}; ownerActorPtr={FormatPointer(this.OwnerActorPointer)}; slot={this.Slot}; timelineId={this.TimelineId}; selectedRig={this.SelectedPreset}; brioActionTimelineCapabilityFound={this.BrioActionTimelineCapabilityFound}; brioAffectsSkeletonsMemberFound={this.BrioAffectsSkeletonsMemberFound}; brioActionTimelineCapabilityInstanceFound={this.BrioActionTimelineCapabilityInstanceFound}; brioCapabilityProbe={this.BrioCapabilityProbeResult}; resolvedRigBefore={this.ResolvedRigBefore}; resolvedRigAfter={this.ResolvedRigAfter}; changedAnimationDataPath={this.ChangedAnimationDataPath}; changedAppearance={this.AppearanceHashBefore != this.AppearanceHashAfter}; replaySuccess={this.ReplaySuccess}; replay={this.ReplayReason}; appearanceHashBefore={this.AppearanceHashBefore}; appearanceHashAfter={this.AppearanceHashAfter}; brioCandidates={this.BrioTimelineCandidates}";
    }

    private readonly record struct BrioRigProbeDetails(
        bool ActionTimelineCapabilityFound,
        bool AffectsSkeletonsMemberFound,
        bool ActionTimelineCapabilityInstanceFound,
        string CapabilityProbeResult,
        string CandidateSignatures);
}
