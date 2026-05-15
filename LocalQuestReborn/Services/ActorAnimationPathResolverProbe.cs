using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationPathResolverProbe
{
    private static readonly string[] CandidateTokens =
    [
        "Skeleton",
        "PartialSkeleton",
        "Animation",
        "AnimationBinding",
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
        "Havok",
        "Hka",
        "Rig",
    ];

    private readonly IPluginLog log;

    public ActorAnimationPathResolverProbe(IPluginLog log)
    {
        this.log = log;
    }

    public ActorAnimationPathDump Capture(RuntimeActorInstance actor, string label, uint timelineId)
    {
        var actorPtr = TryReadAddress(actor.Address, out var parsedPtr) ? parsedPtr : 0;
        var dump = new ActorAnimationPathDump
        {
            Label = label,
            ActorRuntimeId = actor.RuntimeId,
            ActorDisplayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.NpcName : actor.DisplayName,
            TimelineId = timelineId,
            ActorPtr = actorPtr,
            AppearanceHash = BuildAppearanceHash(actor.CharacterObject),
            CandidateSummary = BuildCandidateSummary(actor.CharacterObject),
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
                    dump.TimelineBaseOverride = character->Timeline.BaseOverride;
                    dump.AnimationBindingHash = StableHash(
                        $"actor={FormatPointer(actorPtr)};draw={FormatPointer(dump.DrawObjectPtr)};timeline={timelineId};baseOverride={character->Timeline.BaseOverride};candidates={dump.CandidateSummary}");
                }
            }
            catch (Exception ex)
            {
                dump.AnimationBindingHash = StableHash($"actor={FormatPointer(actorPtr)};timeline={timelineId};error={ex.Message};candidates={dump.CandidateSummary}");
                dump.Error = $"native snapshot failed: {ex.Message}";
            }
        }
        else
        {
            dump.AnimationBindingHash = StableHash($"actor=0;timeline={timelineId};candidates={dump.CandidateSummary}");
            dump.Error = "actor native pointer unavailable";
        }

        dump.Report = BuildDumpReport(dump);
        this.log.Information("[AnimationPathResolverProbe] dump captured label={Label} actor={Actor} report={Report}", label, actor.RuntimeId, dump.Report);
        return dump;
    }

    public ActorAnimationPathDiff Compare(ActorAnimationPathDump before, ActorAnimationPathDump after, string label)
    {
        var changed = new List<string>();
        AddDiff(changed, "actorPtr", FormatPointer(before.ActorPtr), FormatPointer(after.ActorPtr));
        AddDiff(changed, "drawObjectPtr", FormatPointer(before.DrawObjectPtr), FormatPointer(after.DrawObjectPtr));
        AddDiff(changed, "characterBasePtr", FormatPointer(before.CharacterBasePtr), FormatPointer(after.CharacterBasePtr));
        AddDiff(changed, "timelineBaseOverride", before.TimelineBaseOverride.ToString(), after.TimelineBaseOverride.ToString());
        AddDiff(changed, "appearanceHash", before.AppearanceHash, after.AppearanceHash);
        AddDiff(changed, "animationBindingHash", before.AnimationBindingHash, after.AnimationBindingHash);
        AddDiff(changed, "candidateSummary", before.CandidateSummary, after.CandidateSummary);

        var report = string.Join(Environment.NewLine, new[]
        {
            $"[AnimationPathResolverDiff] {label}",
            $"before={before.Label}; after={after.Label}",
            $"timelineIdBefore={before.TimelineId}; timelineIdAfter={after.TimelineId}",
            $"appearanceHashChanged={!string.Equals(before.AppearanceHash, after.AppearanceHash, StringComparison.Ordinal)}",
            $"animationBindingChanged={!string.Equals(before.AnimationBindingHash, after.AnimationBindingHash, StringComparison.Ordinal)}",
            $"candidateFieldChanged={!string.Equals(before.CandidateSummary, after.CandidateSummary, StringComparison.Ordinal)}",
            "changedFields:",
            changed.Count == 0 ? "none" : string.Join(Environment.NewLine, changed),
        });

        this.log.Information("{Report}", report);
        return new ActorAnimationPathDiff(
            label,
            !string.Equals(before.AppearanceHash, after.AppearanceHash, StringComparison.Ordinal),
            !string.Equals(before.AnimationBindingHash, after.AnimationBindingHash, StringComparison.Ordinal),
            !string.Equals(before.CandidateSummary, after.CandidateSummary, StringComparison.Ordinal),
            changed,
            report);
    }

    private static string BuildDumpReport(ActorAnimationPathDump dump)
        => string.Join(Environment.NewLine, new[]
        {
            "[AnimationPathResolverDump]",
            $"label={dump.Label}",
            $"actorRuntimeId={dump.ActorRuntimeId}",
            $"actorDisplayName={dump.ActorDisplayName}",
            $"timelineId={dump.TimelineId}",
            $"actorPtr={FormatPointer(dump.ActorPtr)}",
            $"drawObjectPtr={FormatPointer(dump.DrawObjectPtr)}",
            $"characterBasePtr={FormatPointer(dump.CharacterBasePtr)}",
            $"timelineBaseOverride={dump.TimelineBaseOverride}",
            $"appearanceHash={dump.AppearanceHash}",
            $"animationBindingHash={dump.AnimationBindingHash}",
            $"candidateSummary={dump.CandidateSummary}",
            $"error={dump.Error}",
        });

    private static void AddDiff(List<string> changed, string name, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changed.Add($"{name}: before={before}; after={after}");
    }

    private static string BuildCandidateSummary(object? source)
    {
        if (source == null)
            return "unavailable:null";

        try
        {
            var values = new List<string>();
            AppendCandidateValues(source, source.GetType().Name, values, 0);
            values.Sort(StringComparer.Ordinal);
            return values.Count == 0
                ? $"type={source.GetType().FullName}; candidates=none"
                : string.Join(";", values.Take(96));
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.Message}";
        }
    }

    private static void AppendCandidateValues(object source, string prefix, List<string> values, int depth)
    {
        if (depth > 1 || values.Count >= 128)
            return;

        var type = source.GetType();
        foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field) || !LooksLikeCandidate(member.Name))
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
                AppendCandidateValues(value, path, values, depth + 1);
        }
    }

    private static bool LooksLikeCandidate(string name)
        => CandidateTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

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

    private static string FormatPointer(nint pointer) => pointer == 0 ? "0x0" : $"0x{pointer.ToInt64():X}";
}

public sealed record ActorAnimationPathDump
{
    public string Label { get; init; } = string.Empty;
    public string ActorRuntimeId { get; init; } = string.Empty;
    public string ActorDisplayName { get; init; } = string.Empty;
    public uint TimelineId { get; init; }
    public nint ActorPtr { get; set; }
    public nint DrawObjectPtr { get; set; }
    public nint CharacterBasePtr { get; set; }
    public ushort TimelineBaseOverride { get; set; }
    public string AppearanceHash { get; set; } = string.Empty;
    public string AnimationBindingHash { get; set; } = string.Empty;
    public string CandidateSummary { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Report { get; set; } = string.Empty;
}

public sealed record ActorAnimationPathDiff(
    string Label,
    bool AppearanceHashChanged,
    bool AnimationBindingChanged,
    bool CandidateFieldChanged,
    IReadOnlyList<string> ChangedFields,
    string Report);
