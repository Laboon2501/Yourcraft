using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class AnimationDataPathProbeService
{
    private static readonly string[] CandidateTokens =
    [
        "Skeleton",
        "PartialSkeleton",
        "Animation",
        "AnimationBinding",
        "Anim",
        "DataPath",
        "ModelType",
        "ModelChara",
        "Model",
        "Race",
        "Gender",
        "Sex",
        "Tribe",
        "Customize",
        "Timeline",
        "Sequencer",
        "BaseOverride",
        "Pap",
        "Tmb",
        "Sklb",
        "Havok",
        "Hka",
        "Rig",
    ];

    private static readonly string[] CustomizeTokens =
    [
        "Customize",
        "Race",
        "Gender",
        "Sex",
        "Tribe",
        "BodyType",
        "ModelChara",
        "Model",
        "DrawData",
    ];

    private static readonly string[] EquipmentTokens =
    [
        "Equip",
        "Equipment",
        "Weapon",
        "MainHand",
        "OffHand",
        "Armor",
        "Stain",
        "Dye",
        "Head",
        "Body",
        "Hands",
        "Legs",
        "Feet",
        "Ring",
        "Earring",
        "Neck",
        "Wrist",
    ];

    private readonly IPluginLog log;
    private readonly AnimationDataPathCandidateScanner candidateScanner = new();

    public AnimationDataPathProbeService(IPluginLog log)
    {
        this.log = log;
    }

    public AnimationDataPathDump DumpCurrentActorAnimationState(RuntimeActorInstance actor, uint timelineId, ActorAnimationRigPreset selectedRig, string label)
    {
        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var customizeSummary = BuildMemberSummary(actor.CharacterObject, CustomizeTokens, maxValues: 96);
        var equipmentSummary = BuildMemberSummary(actor.CharacterObject, EquipmentTokens, maxValues: 96);
        var candidateSummary = BuildMemberSummary(actor.CharacterObject, CandidateTokens, maxValues: 160);
        var transformSummary = BuildTransformSummary(actor);
        var dump = new AnimationDataPathDump
        {
            Label = label,
            ActorRuntimeId = actor.RuntimeId,
            ActorDisplayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.NpcName : actor.DisplayName,
            GameObjectIndex = TryParseObjectIndex(actor.ObjectIndex),
            TimelineId = timelineId,
            CurrentActionTimelineId = timelineId,
            SelectedRig = selectedRig,
            ActorPtr = actorPtr,
            AppearanceHash = StableHash($"{customizeSummary}|{equipmentSummary}"),
            CustomizeHash = StableHash(customizeSummary),
            EquipmentHash = StableHash(equipmentSummary),
            ModelCustomizeSummary = Shorten(customizeSummary, 900),
            TransformHash = StableHash(transformSummary),
            TransformSummary = transformSummary,
            CandidateSummary = Shorten(candidateSummary, 1600),
            CandidateCount = candidateSummary == "unavailable:null" ? 0 : candidateSummary.Split(';', StringSplitOptions.RemoveEmptyEntries).Length,
        };

        if (actorPtr != 0)
        {
            try
            {
                unsafe
                {
                    var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actorPtr;
                    dump.DrawObjectPtr = character->GameObject.DrawObject == null
                        ? 0
                        : (nint)character->GameObject.DrawObject;
                    dump.CharacterBasePtr = dump.DrawObjectPtr;
                    dump.TimelineContainerPtr = (nint)(&character->Timeline);
                    dump.TimelineSequencerPtr = (nint)(&character->Timeline.TimelineSequencer);
                    dump.TimelineBaseOverride = character->Timeline.BaseOverride;
                    dump.AnimationBindingHash = StableHash(
                        $"actor={FormatPointer(actorPtr)};draw={FormatPointer(dump.DrawObjectPtr)};timeline={timelineId};baseOverride={dump.TimelineBaseOverride};candidates={dump.CandidateSummary}");
                }
            }
            catch (Exception ex)
            {
                dump.AnimationBindingHash = StableHash($"actor={FormatPointer(actorPtr)};timeline={timelineId};nativeError={ex.Message};candidates={dump.CandidateSummary}");
                dump.Error = $"native snapshot failed: {ex.Message}";
            }
        }
        else
        {
            dump.AnimationBindingHash = StableHash($"actor=0;timeline={timelineId};candidates={dump.CandidateSummary}");
            dump.Error = "actor native pointer unavailable";
        }

        var dataPathCandidates = this.candidateScanner.Scan(actor, actorPtr, dump.DrawObjectPtr, dump.TimelineContainerPtr, selectedRig);
        dump.DataPathCandidateCount = dataPathCandidates.Candidates.Count;
        dump.DataPathCandidateSummary = dataPathCandidates.Summary;
        dump.DataPathCandidateReport = dataPathCandidates.Report;
        dump.CurrentDataPath = dataPathCandidates.CurrentDataPath;
        dump.CurrentDataHead = dataPathCandidates.CurrentDataHead;
        dump.DataPathReadError = dataPathCandidates.ReadError;
        dump.DataPathReadSummary = FormatDataPathReadSummary(dump.CurrentDataPath, dump.CurrentDataHead, dump.DataPathReadError);
        dump.CandidateCount += dataPathCandidates.Candidates.Count;
        dump.CandidateSummary = Shorten($"{dump.CandidateSummary}; dataPathCandidates={dataPathCandidates.Summary}", 1600);

        dump.Report = BuildDumpReport(dump);
        actor.AnimationPathResolverStatus = $"DataPath read: {dump.DataPathReadSummary}; timeline={timelineId}; selectedRig={selectedRig}; candidateCount={dump.DataPathCandidateCount}";
        this.log.Information("[AnimationDataPathProbe] dump captured label={Label} actor={Actor} report={Report}", label, actor.RuntimeId, dump.Report);
        return dump;
    }

    public AnimationDataPathDump DumpBeforeExternalChange(RuntimeActorInstance actor, uint timelineId, ActorAnimationRigPreset selectedRig)
        => this.DumpCurrentActorAnimationState(actor, timelineId, selectedRig, "BeforeExternalRigChange");

    public AnimationDataPathDump DumpAfterExternalChange(RuntimeActorInstance actor, uint timelineId, ActorAnimationRigPreset selectedRig)
        => this.DumpCurrentActorAnimationState(actor, timelineId, selectedRig, "AfterExternalRigChange");

    public AnimationDataPathDiff CompareRigDumps(AnimationDataPathDump before, AnimationDataPathDump after, string label)
    {
        var changed = new List<string>();
        AddDiff(changed, "actorPtr", FormatPointer(before.ActorPtr), FormatPointer(after.ActorPtr));
        AddDiff(changed, "gameObjectIndex", before.GameObjectIndex.ToString(), after.GameObjectIndex.ToString());
        AddDiff(changed, "drawObjectPtr", FormatPointer(before.DrawObjectPtr), FormatPointer(after.DrawObjectPtr));
        AddDiff(changed, "characterBasePtr", FormatPointer(before.CharacterBasePtr), FormatPointer(after.CharacterBasePtr));
        AddDiff(changed, "timelineContainerPtr", FormatPointer(before.TimelineContainerPtr), FormatPointer(after.TimelineContainerPtr));
        AddDiff(changed, "timelineSequencerPtr", FormatPointer(before.TimelineSequencerPtr), FormatPointer(after.TimelineSequencerPtr));
        AddDiff(changed, "timelineBaseOverride", before.TimelineBaseOverride.ToString(), after.TimelineBaseOverride.ToString());
        AddDiff(changed, "appearanceHash", before.AppearanceHash, after.AppearanceHash);
        AddDiff(changed, "customizeHash", before.CustomizeHash, after.CustomizeHash);
        AddDiff(changed, "equipmentHash", before.EquipmentHash, after.EquipmentHash);
        AddDiff(changed, "transformHash", before.TransformHash, after.TransformHash);
        AddDiff(changed, "animationBindingHash", before.AnimationBindingHash, after.AnimationBindingHash);
        AddDiff(changed, "skeletonPtr", FormatPointer(before.SkeletonPtr), FormatPointer(after.SkeletonPtr));
        AddDiff(changed, "skeletonResourceHash", before.SkeletonResourceHash, after.SkeletonResourceHash);
        AddDiff(changed, "animationResourceHash", before.AnimationResourceHash, after.AnimationResourceHash);
        AddDiff(changed, "candidateSummary", before.CandidateSummary, after.CandidateSummary);
        AddDiff(changed, "dataPathCandidateSummary", before.DataPathCandidateSummary, after.DataPathCandidateSummary);
        AddDiff(changed, "currentDataPath", before.CurrentDataPath?.ToString() ?? "null", after.CurrentDataPath?.ToString() ?? "null");
        AddDiff(changed, "currentDataHead", before.CurrentDataHead?.ToString() ?? "null", after.CurrentDataHead?.ToString() ?? "null");
        AddDiff(changed, "dataPathReadError", before.DataPathReadError, after.DataPathReadError);

        var diff = new AnimationDataPathDiff
        {
            Label = label,
            AppearanceChanged = !StringEquals(before.AppearanceHash, after.AppearanceHash),
            CustomizeChanged = !StringEquals(before.CustomizeHash, after.CustomizeHash),
            EquipmentChanged = !StringEquals(before.EquipmentHash, after.EquipmentHash),
            TransformChanged = !StringEquals(before.TransformHash, after.TransformHash),
            AnimationBindingChanged = !StringEquals(before.AnimationBindingHash, after.AnimationBindingHash),
            SkeletonPointerChanged = before.SkeletonPtr != after.SkeletonPtr,
            SkeletonResourceChanged = !StringEquals(before.SkeletonResourceHash, after.SkeletonResourceHash),
            AnimationResourceChanged = !StringEquals(before.AnimationResourceHash, after.AnimationResourceHash),
            CandidateFieldsChanged = !StringEquals(before.CandidateSummary, after.CandidateSummary),
            ChangedFields = changed,
        };

        diff.Result = diff.AppearanceChanged || diff.CustomizeChanged || diff.EquipmentChanged
            ? "CandidateUnsafeAppearanceChanged"
            : diff.AnimationBindingChanged || diff.SkeletonPointerChanged || diff.SkeletonResourceChanged || diff.AnimationResourceChanged || diff.CandidateFieldsChanged
                ? "CandidateFound"
                : "ProbeOnly";
        diff.Report = BuildDiffReport(diff, before, after);
        this.log.Information("{Report}", diff.Report);
        return diff;
    }

    public AnimationDataPathDiff CompareTwoActorsSameTimeline(RuntimeActorInstance actorA, RuntimeActorInstance actorB, uint timelineId, out AnimationDataPathDump dumpA, out AnimationDataPathDump dumpB)
    {
        dumpA = this.DumpCurrentActorAnimationState(actorA, timelineId, actorA.AnimationRigPreset, $"DualActorA-{ShortId(actorA.RuntimeId)}");
        dumpB = this.DumpCurrentActorAnimationState(actorB, timelineId, actorA.AnimationRigPreset, $"DualActorB-{ShortId(actorB.RuntimeId)}");
        return this.CompareRigDumps(dumpA, dumpB, "DualActorSameTimelineReadOnly");
    }

    private static string BuildDumpReport(AnimationDataPathDump dump)
        => string.Join(Environment.NewLine, new[]
        {
            "[AnimationDataPathProbeDump]",
            $"label={dump.Label}",
            $"actorRuntimeId={dump.ActorRuntimeId}",
            $"actorDisplayName={dump.ActorDisplayName}",
            $"gameObjectIndex={dump.GameObjectIndex}",
            $"selectedRig={dump.SelectedRig}",
            $"timelineId={dump.TimelineId}",
            $"currentActionTimelineId={dump.CurrentActionTimelineId}",
            $"actorPtr={FormatPointer(dump.ActorPtr)}",
            $"drawObjectPtr={FormatPointer(dump.DrawObjectPtr)}",
            $"characterBasePtr={FormatPointer(dump.CharacterBasePtr)}",
            $"timelineContainerPtr={FormatPointer(dump.TimelineContainerPtr)}",
            $"timelineSequencerPtr={FormatPointer(dump.TimelineSequencerPtr)}",
            $"timelineBaseOverride={dump.TimelineBaseOverride}",
            $"skeletonPtr={FormatPointer(dump.SkeletonPtr)}",
            $"partialSkeletonCount={dump.PartialSkeletonCount}",
            $"animationControlCount={dump.AnimationControlCount}",
            $"appearanceHash={dump.AppearanceHash}",
            $"customizeHash={dump.CustomizeHash}",
            $"equipmentHash={dump.EquipmentHash}",
            $"transformHash={dump.TransformHash}",
            $"animationBindingHash={dump.AnimationBindingHash}",
            $"skeletonResourceHash={dump.SkeletonResourceHash}",
            $"animationResourceHash={dump.AnimationResourceHash}",
            $"currentDataPath={FormatNullableDataPath(dump.CurrentDataPath)}",
            $"currentDataHead={FormatNullableDataHead(dump.CurrentDataHead)}",
            $"dataPathReadError={dump.DataPathReadError}",
            $"dataPathReadSummary={dump.DataPathReadSummary}",
            $"modelCustomizeSummary={dump.ModelCustomizeSummary}",
            $"transformSummary={dump.TransformSummary}",
            $"candidateCount={dump.CandidateCount}",
            $"candidateSummary={dump.CandidateSummary}",
            $"dataPathCandidateCount={dump.DataPathCandidateCount}",
            $"dataPathCandidateSummary={dump.DataPathCandidateSummary}",
            dump.DataPathCandidateReport,
            $"error={dump.Error}",
        });

    private static string BuildDiffReport(AnimationDataPathDiff diff, AnimationDataPathDump before, AnimationDataPathDump after)
        => string.Join(Environment.NewLine, new[]
        {
            $"[AnimationDataPathProbeDiff] {diff.Label}",
            $"result={diff.Result}",
            $"before={before.Label}; after={after.Label}",
            $"timelineIdBefore={before.TimelineId}; timelineIdAfter={after.TimelineId}",
            $"appearanceChanged={diff.AppearanceChanged}",
            $"customizeChanged={diff.CustomizeChanged}",
            $"equipmentChanged={diff.EquipmentChanged}",
            $"transformChanged={diff.TransformChanged}",
            $"animationBindingChanged={diff.AnimationBindingChanged}",
            $"skeletonPtrChanged={diff.SkeletonPointerChanged}",
            $"skeletonResourceChanged={diff.SkeletonResourceChanged}",
            $"animationResourceChanged={diff.AnimationResourceChanged}",
            $"candidateFieldsChanged={diff.CandidateFieldsChanged}",
            "changedFields:",
            diff.ChangedFields.Count == 0 ? "none" : string.Join(Environment.NewLine, diff.ChangedFields),
        });

    private static string BuildTransformSummary(RuntimeActorInstance actor)
    {
        var scale = actor.LastKnownScale == Vector3.Zero ? actor.TransformEditScale : actor.LastKnownScale;
        if (scale == Vector3.Zero)
            scale = Vector3.One;
        return $"lastPos={actor.LastKnownPosition};lastEuler={actor.LastKnownRotationEuler};lastQuat={actor.LastKnownRotation};lastScale={scale};editPos={actor.TransformEditPosition};editEuler={actor.TransformEditRotationEuler};editScale={actor.TransformEditScale}";
    }

    private static string BuildMemberSummary(object? source, IReadOnlyList<string> tokens, int maxValues)
    {
        if (source == null)
            return "unavailable:null";

        try
        {
            var values = new List<string>();
            AppendMemberValues(source, source.GetType().Name, tokens, values, depth: 0, maxValues);
            values.Sort(StringComparer.Ordinal);
            return values.Count == 0
                ? $"type={source.GetType().FullName}; members=none"
                : string.Join(";", values.Take(maxValues));
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.Message}";
        }
    }

    private static void AppendMemberValues(object source, string prefix, IReadOnlyList<string> tokens, List<string> values, int depth, int maxValues)
    {
        if (depth > 1 || values.Count >= maxValues)
            return;

        var type = source.GetType();
        foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (values.Count >= maxValues)
                return;
            if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field) || !LooksLikeMember(member.Name, tokens))
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

            var path = $"{prefix}.{member.Name}";
            values.Add($"{path}={FormatValue(value)}");
            if (value != null && value is not string && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
                AppendMemberValues(value, path, tokens, values, depth + 1, maxValues);
        }
    }

    private static bool LooksLikeMember(string name, IReadOnlyList<string> tokens)
        => tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void AddDiff(List<string> changed, string name, string before, string after)
    {
        if (!StringEquals(before, after))
            changed.Add($"{name}: before={before}; after={after}");
    }

    private static int TryParseObjectIndex(string? raw)
        => int.TryParse(raw, out var index) ? index : -1;

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "null";
        if (value is string text)
            return text;
        if (value is nint pointer)
            return FormatPointer(pointer);
        if (value is nuint upointer)
            return $"0x{upointer:X}";
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? "null");
                if (items.Count >= 16)
                    break;
            }

            return "[" + string.Join(",", items) + "]";
        }

        return value.ToString() ?? value.GetType().Name;
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

    private static string Shorten(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...<truncated>";

    private static string ShortId(string runtimeId)
        => runtimeId[..Math.Min(8, runtimeId.Length)];

    private static string FormatDataPathReadSummary(short? currentDataPath, byte? currentDataHead, string error)
    {
        if (!currentDataPath.HasValue || !currentDataHead.HasValue)
            return string.IsNullOrWhiteSpace(error) ? "unavailable" : error;

        return $"currentDataPath={FormatNullableDataPath(currentDataPath)}; currentDataHead={FormatNullableDataHead(currentDataHead)}";
    }

    private static string FormatNullableDataPath(short? value)
        => value.HasValue ? $"{value.Value} (0x{unchecked((ushort)value.Value):X4})" : "unavailable";

    private static string FormatNullableDataHead(byte? value)
        => value.HasValue ? $"{value.Value} (0x{value.Value:X2})" : "unavailable";

    private static string FormatPointer(nint pointer) => pointer == 0 ? "0x0" : $"0x{pointer.ToInt64():X}";
}

public sealed record AnimationDataPathDump
{
    public string Label { get; init; } = string.Empty;
    public string ActorRuntimeId { get; init; } = string.Empty;
    public string ActorDisplayName { get; init; } = string.Empty;
    public int GameObjectIndex { get; init; } = -1;
    public uint TimelineId { get; init; }
    public uint CurrentActionTimelineId { get; init; }
    public ActorAnimationRigPreset SelectedRig { get; init; } = ActorAnimationRigPreset.Current;
    public nint ActorPtr { get; set; }
    public nint DrawObjectPtr { get; set; }
    public nint CharacterBasePtr { get; set; }
    public nint TimelineContainerPtr { get; set; }
    public nint TimelineSequencerPtr { get; set; }
    public nint SkeletonPtr { get; set; }
    public int PartialSkeletonCount { get; set; } = -1;
    public int AnimationControlCount { get; set; } = -1;
    public ushort TimelineBaseOverride { get; set; }
    public string AppearanceHash { get; set; } = string.Empty;
    public string CustomizeHash { get; set; } = string.Empty;
    public string EquipmentHash { get; set; } = string.Empty;
    public string TransformHash { get; set; } = string.Empty;
    public string TransformSummary { get; set; } = string.Empty;
    public string ModelCustomizeSummary { get; set; } = string.Empty;
    public string AnimationBindingHash { get; set; } = string.Empty;
    public string SkeletonResourceHash { get; set; } = "unavailable";
    public string AnimationResourceHash { get; set; } = "unavailable";
    public string CandidateSummary { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public string DataPathCandidateSummary { get; set; } = string.Empty;
    public string DataPathCandidateReport { get; set; } = string.Empty;
    public int DataPathCandidateCount { get; set; }
    public short? CurrentDataPath { get; set; }
    public byte? CurrentDataHead { get; set; }
    public string DataPathReadError { get; set; } = string.Empty;
    public string DataPathReadSummary { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Report { get; set; } = string.Empty;
}

public sealed record AnimationDataPathDiff
{
    public string Label { get; init; } = string.Empty;
    public bool AppearanceChanged { get; init; }
    public bool CustomizeChanged { get; init; }
    public bool EquipmentChanged { get; init; }
    public bool TransformChanged { get; init; }
    public bool AnimationBindingChanged { get; init; }
    public bool SkeletonPointerChanged { get; init; }
    public bool SkeletonResourceChanged { get; init; }
    public bool AnimationResourceChanged { get; init; }
    public bool CandidateFieldsChanged { get; init; }
    public string Result { get; set; } = "ProbeOnly";
    public IReadOnlyList<string> ChangedFields { get; init; } = [];
    public string Report { get; set; } = string.Empty;
}
