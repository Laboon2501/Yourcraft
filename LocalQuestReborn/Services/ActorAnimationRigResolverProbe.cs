using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigResolverProbe
{
    private static readonly string[] ResolverTypeTokens =
    [
        "CharacterBase",
        "DrawObject",
        "Skeleton",
        "PartialSkeleton",
        "Havok",
        "AnimatedSkeleton",
        "Animation",
        "AnimationBinding",
        "DataPath",
        "ModelType",
        "ModelChara",
        "TimelineContainer",
        "TimelineSequencer",
        "OwnerObject",
        "Race",
        "Gender",
        "Rig",
        "Resolver",
    ];

    private static readonly string[] NoisyNameTokens =
    [
        "<",
        ">",
        "d__",
        "__",
        "MemberwiseClone",
        "Equals",
        "GetHashCode",
        "ToString",
        "Finalize",
        "Clone",
        "StopSpeedAndResetTimeline",
        "OriginalBaseAnimation",
        "ActionTimelineCapability",
    ];

    private readonly IPluginLog log;

    public ActorAnimationRigResolverProbe(IPluginLog log)
    {
        this.log = log;
    }

    public ResolverProbeSnapshot CaptureSnapshot(RuntimeActorInstance actor, uint timelineId, ActorAnimationRigPreset selectedRig, string stage)
    {
        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var snapshot = new ResolverProbeSnapshot
        {
            Stage = stage,
            TimelineId = timelineId,
            SelectedRig = selectedRig,
            OwnerActorPtr = actorPtr,
            DrawObjectPtr = 0,
            CharacterBasePtr = 0,
            SkeletonPtr = 0,
            PartialSkeletonCount = "unavailable: no verified reader",
            AnimationControlCount = "unavailable: no verified reader",
            CurrentAnimationNameOrPath = "unavailable: no verified native animation binding reader",
        };

        if (actorPtr == 0)
        {
            snapshot.AnimationBindingHash = StableHash($"{stage};actorPtr=0;timeline={timelineId};rig={selectedRig}");
            return snapshot;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actorPtr;
                snapshot.DrawObjectPtr = character->GameObject.DrawObject == null
                    ? 0
                    : (nint)character->GameObject.DrawObject;
                snapshot.CharacterBasePtr = snapshot.DrawObjectPtr;
                snapshot.CurrentAnimationNameOrPath = $"Timeline.BaseOverride={character->Timeline.BaseOverride}";
                snapshot.AnimationBindingHash = StableHash(
                    $"draw={FormatPointer(snapshot.DrawObjectPtr)};baseOverride={character->Timeline.BaseOverride};timeline={timelineId};rig={selectedRig}");
            }
        }
        catch (Exception ex)
        {
            snapshot.CurrentAnimationNameOrPath = $"unavailable: native snapshot failed: {ex.Message}";
            snapshot.AnimationBindingHash = StableHash($"{stage};actor={FormatPointer(actorPtr)};snapshotError={ex.Message};timeline={timelineId};rig={selectedRig}");
        }

        return snapshot;
    }

    public ResolverProbeResult Probe(
        RuntimeActorInstance actor,
        ResolverProbeSnapshot before,
        ResolverProbeSnapshot after,
        bool hookInstalled,
        int hookHitCount,
        bool hookOwnerMatched,
        bool replaySuccess,
        bool appearanceChanged)
    {
        var metadata = this.BuildResolverMetadataReport();
        var animationBindingChanged = !string.Equals(before.AnimationBindingHash, after.AnimationBindingHash, StringComparison.Ordinal);
        var animationDataPathChanged = false;
        var result = "NoEffect";
        var nextProbeTarget = metadata.SafeWritableFieldFound
            ? $"{metadata.CandidateFieldName} (experimental write disabled until verified)"
            : "CharacterBase/DrawObject skeleton resolver or Anamnesis-style animation data path";
        var reason = hookHitCount == 0
            ? "ActionTimeline replay did not pass through the rig context hook."
            : "ActionTimeline 播放链已命中，但未找到动画骨架 resolver 字段。";

        var summary = string.Join("; ", new[]
        {
            $"result={result}",
            $"hookInstalled={hookInstalled}",
            $"hookHitCount={hookHitCount}",
            $"ownerMatched={hookOwnerMatched}",
            $"timelineId={after.TimelineId}",
            $"selectedRig={after.SelectedRig}",
            $"animationDataPathChanged={animationDataPathChanged}",
            $"appearanceChanged={appearanceChanged}",
            $"nextProbeTarget={nextProbeTarget}",
        });

        var details = string.Join(Environment.NewLine, new[]
        {
            "[AnimationRigResolverProbe]",
            $"actorRuntimeId={actor.RuntimeId}",
            $"timelineId={after.TimelineId}",
            $"selectedRig={after.SelectedRig}",
            $"ownerActorPtr={FormatPointer(after.OwnerActorPtr)}",
            $"drawObjectPtr={FormatPointer(after.DrawObjectPtr)}",
            $"characterBasePtr={FormatPointer(after.CharacterBasePtr)}",
            $"skeletonPtr={FormatPointer(after.SkeletonPtr)}",
            $"partialSkeletonCount={after.PartialSkeletonCount}",
            $"animationControlCount={after.AnimationControlCount}",
            $"currentAnimation={after.CurrentAnimationNameOrPath}",
            $"beforeAnimationBindingHash={before.AnimationBindingHash}",
            $"afterAnimationBindingHash={after.AnimationBindingHash}",
            $"animationBindingChanged={animationBindingChanged}",
            $"animationDataPathChanged={animationDataPathChanged}",
            $"appearanceHashChanged={appearanceChanged}",
            $"safeWritableFieldFound={metadata.SafeWritableFieldFound}",
            $"candidateFieldName={metadata.CandidateFieldName}",
            $"candidateFieldOffset={metadata.CandidateFieldOffset}",
            $"result={result}",
            $"reason={reason}",
            $"metadataSummary={metadata.Summary}",
            "metadataDetails:",
            metadata.Details,
        });

        this.log.Information("{Details}", details);

        return new ResolverProbeResult(
            result,
            reason,
            summary,
            details,
            animationBindingChanged,
            animationDataPathChanged,
            metadata.SafeWritableFieldFound,
            metadata.CandidateFieldName,
            metadata.CandidateFieldOffset,
            nextProbeTarget);
    }

    private ResolverMetadataReport BuildResolverMetadataReport()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly =>
                {
                    var name = assembly.GetName().Name ?? string.Empty;
                    return name.Contains("FFXIVClientStructs", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Brio", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Anamnesis", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var signatures = new List<string>();
            CandidateMetadata? firstCandidate = null;

            foreach (var assembly in assemblies)
            {
                foreach (var type in SafeGetTypes(assembly).Where(LooksLikeResolverType))
                {
                    foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                 .Where(member => !IsNoisy(member.Name) && LooksLikeResolverMember(type, member))
                                 .Take(20))
                    {
                        var signature = $"{type.FullName}.{DescribeMember(member)}";
                        signatures.Add(signature);
                        firstCandidate ??= ToCandidate(type, member);
                    }
                }
            }

            signatures = signatures
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToList();

            var candidate = firstCandidate;
            return new ResolverMetadataReport(
                SafeWritableFieldFound: false,
                CandidateFieldName: candidate?.Name ?? "none",
                CandidateFieldOffset: candidate?.Offset ?? "unknown",
                Summary: signatures.Count == 0
                    ? "No resolver-like metadata found in loaded FFXIVClientStructs/Brio/Anamnesis assemblies."
                    : $"resolver-like metadata candidates={signatures.Count}; firstCandidate={candidate?.Name ?? "none"}; safeWritableFieldFound=false",
                Details: signatures.Count == 0 ? "none" : string.Join(Environment.NewLine, signatures));
        }
        catch (Exception ex)
        {
            return new ResolverMetadataReport(false, "none", "unknown", $"metadata probe failed: {ex.Message}", ex.ToString());
        }
    }

    private static CandidateMetadata ToCandidate(Type type, MemberInfo member)
    {
        var name = $"{type.FullName}.{member.Name}";
        var offset = "unknown";
        if (member is FieldInfo field)
        {
            var fieldOffset = field.GetCustomAttribute<FieldOffsetAttribute>();
            if (fieldOffset != null)
                offset = $"0x{fieldOffset.Value:X}";
        }

        return new CandidateMetadata(name, offset);
    }

    private static bool LooksLikeResolverType(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        if (IsNoisy(fullName))
            return false;
        return ResolverTypeTokens.Any(token => fullName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeResolverMember(Type type, MemberInfo member)
    {
        var fullName = type.FullName ?? type.Name;
        return ResolverTypeTokens.Any(token =>
            fullName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNoisy(string name)
        => NoisyNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

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

    private sealed record CandidateMetadata(string Name, string Offset);

    private sealed record ResolverMetadataReport(
        bool SafeWritableFieldFound,
        string CandidateFieldName,
        string CandidateFieldOffset,
        string Summary,
        string Details);
}

public sealed record ResolverProbeSnapshot
{
    public string Stage { get; init; } = string.Empty;

    public uint TimelineId { get; init; }

    public ActorAnimationRigPreset SelectedRig { get; init; } = ActorAnimationRigPreset.Current;

    public nint OwnerActorPtr { get; init; }

    public nint DrawObjectPtr { get; set; }

    public nint CharacterBasePtr { get; set; }

    public nint SkeletonPtr { get; set; }

    public string PartialSkeletonCount { get; init; } = "unavailable";

    public string AnimationControlCount { get; init; } = "unavailable";

    public string CurrentAnimationNameOrPath { get; set; } = "unavailable";

    public string AnimationBindingHash { get; set; } = string.Empty;
}

public sealed record ResolverProbeResult(
    string Result,
    string Reason,
    string UiSummary,
    string DetailedReport,
    bool AnimationBindingChanged,
    bool AnimationDataPathChanged,
    bool SafeWritableFieldFound,
    string CandidateFieldName,
    string CandidateFieldOffset,
    string NextProbeTarget);
