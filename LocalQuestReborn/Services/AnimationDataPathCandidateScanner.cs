using LocalQuestReborn.Models;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalQuestReborn.Services;

public sealed class AnimationDataPathCandidateScanner
{
    private static readonly string[] ReflectionTokens =
    [
        "DataPath",
        "DataHead",
        "ModelObject",
        "ModelType",
        "Skeleton",
        "Timeline",
        "Sequencer",
        "BaseOverride",
        "Animation",
        "Customize",
        "Race",
        "Gender",
        "Sex",
        "Tribe",
        "DrawData",
    ];

    public AnimationDataPathCandidateReport Scan(
        RuntimeActorInstance actor,
        nint actorPtr,
        nint drawObjectPtr,
        nint timelineContainerPtr,
        ActorAnimationRigPreset selectedRig)
    {
        var candidates = new List<AnimationDataPathCandidate>();
        var rawRead = ReadDrawObjectDataPath(drawObjectPtr);

        AddAnamnesisMappedDrawObjectCandidates(candidates, drawObjectPtr, rawRead);
        AddTimelineCandidates(candidates, timelineContainerPtr);
        AddReflectionCandidates(candidates, actor.CharacterObject);

        var summary = BuildSummary(candidates);
        var report = BuildReport(actor, actorPtr, drawObjectPtr, timelineContainerPtr, selectedRig, candidates, summary);
        return new AnimationDataPathCandidateReport(
            candidates,
            summary,
            report,
            rawRead.CurrentDataPath,
            rawRead.CurrentDataHead,
            rawRead.Error);
    }

    private static void AddAnamnesisMappedDrawObjectCandidates(List<AnimationDataPathCandidate> candidates, nint drawObjectPtr, DataPathRawRead rawRead)
    {
        var baseText = FormatPointer(drawObjectPtr);
        candidates.Add(new AnimationDataPathCandidate(
            Name: "ActorModelMemory.Skeleton",
            Source: "Anamnesis ActorModelMemory",
            Address: drawObjectPtr == 0 ? "0x0" : FormatPointer(drawObjectPtr + 0x0A0),
            Offset: "+0x0A0",
            Value: drawObjectPtr == 0 ? "drawObject unavailable" : "not-read-raw",
            ValueTypeGuess: "SkeletonMemory*",
            LinkedSource: "external/Anamnesis/Anamnesis/Memory/ActorModelMemory.cs",
            IsAppearanceField: false,
            IsAnimationResolverCandidate: true,
            SafetyLevel: "ReadOnly"));

        candidates.Add(new AnimationDataPathCandidate(
            Name: "ActorModelMemory.DataPath",
            Source: "Anamnesis DataPathSelector binding",
            Address: drawObjectPtr == 0 ? "0x0" : FormatPointer(drawObjectPtr + 0xAA0),
            Offset: "+0xAA0",
            Value: rawRead.CurrentDataPath.HasValue
                ? FormatDataPath(rawRead.CurrentDataPath.Value)
                : rawRead.Error,
            ValueTypeGuess: "short",
            LinkedSource: "external/Anamnesis/Anamnesis/Memory/ActorModelMemory.cs + external/Anamnesis/Anamnesis/Actor/Pages/ActionPage.xaml",
            IsAppearanceField: false,
            IsAnimationResolverCandidate: true,
            SafetyLevel: "CandidateAnimationOnly"));

        candidates.Add(new AnimationDataPathCandidate(
            Name: "ActorModelMemory.DataHead",
            Source: "Anamnesis DataPathSelector binding",
            Address: drawObjectPtr == 0 ? "0x0" : FormatPointer(drawObjectPtr + 0xAA4),
            Offset: "+0xAA4",
            Value: rawRead.CurrentDataHead.HasValue
                ? FormatDataHead(rawRead.CurrentDataHead.Value)
                : rawRead.Error,
            ValueTypeGuess: "byte",
            LinkedSource: "external/Anamnesis/Anamnesis/Memory/ActorModelMemory.cs + external/Anamnesis/Anamnesis/Actor/Views/DataPathSelector.xaml.cs",
            IsAppearanceField: false,
            IsAnimationResolverCandidate: true,
            SafetyLevel: "CandidateAnimationOnly"));

        candidates.Add(new AnimationDataPathCandidate(
            Name: "DrawObjectBase",
            Source: "Current Actor DrawObject",
            Address: baseText,
            Offset: "+0x0",
            Value: baseText,
            ValueTypeGuess: "CharacterBase/DrawObject*",
            LinkedSource: "FFXIVClientStructs Character.GameObject.DrawObject",
            IsAppearanceField: false,
            IsAnimationResolverCandidate: false,
            SafetyLevel: "ReadOnly"));
    }

    private static DataPathRawRead ReadDrawObjectDataPath(nint drawObjectPtr)
    {
        if (drawObjectPtr == 0)
            return new DataPathRawRead(null, null, "drawObject unavailable");

        var dataPathAddress = drawObjectPtr + 0xAA0;
        var dataHeadAddress = drawObjectPtr + 0xAA4;
        try
        {
            var currentDataPath = Marshal.ReadInt16(dataPathAddress);
            var currentDataHead = Marshal.ReadByte(dataHeadAddress);
            return new DataPathRawRead(currentDataPath, currentDataHead, string.Empty);
        }
        catch (Exception ex)
        {
            return new DataPathRawRead(
                null,
                null,
                $"read failed at DataPath={FormatPointer(dataPathAddress)}, DataHead={FormatPointer(dataHeadAddress)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AddTimelineCandidates(List<AnimationDataPathCandidate> candidates, nint timelineContainerPtr)
    {
        candidates.Add(new AnimationDataPathCandidate(
            Name: "Character.Timeline",
            Source: "FFXIVClientStructs Character",
            Address: FormatPointer(timelineContainerPtr),
            Offset: "native field",
            Value: timelineContainerPtr == 0 ? "unavailable" : "read by AnimationDataPathProbe",
            ValueTypeGuess: "TimelineContainer*",
            LinkedSource: "FFXIVClientStructs Character.Timeline",
            IsAppearanceField: false,
            IsAnimationResolverCandidate: false,
            SafetyLevel: "ReadOnly"));
    }

    private static void AddReflectionCandidates(List<AnimationDataPathCandidate> candidates, object? source)
    {
        if (source == null)
            return;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AppendReflectionCandidates(candidates, source, source.GetType().Name, depth: 0, visited);
    }

    private static void AppendReflectionCandidates(
        List<AnimationDataPathCandidate> candidates,
        object source,
        string path,
        int depth,
        HashSet<object> visited)
    {
        if (depth > 3 || candidates.Count >= 160)
            return;

        if (!source.GetType().IsValueType && !visited.Add(source))
            return;

        var type = source.GetType();
        foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field))
                continue;

            if (!LooksInteresting(member.Name) && depth > 0)
                continue;

            object? value;
            Type? valueType;
            try
            {
                switch (member)
                {
                    case PropertyInfo property when property.GetIndexParameters().Length == 0:
                        value = property.GetValue(source);
                        valueType = property.PropertyType;
                        break;
                    case FieldInfo field:
                        value = field.GetValue(source);
                        valueType = field.FieldType;
                        break;
                    default:
                        continue;
                }
            }
            catch
            {
                continue;
            }

            var memberPath = $"{path}.{member.Name}";
            if (LooksInteresting(member.Name))
            {
                var safety = ClassifySafety(memberPath);
                candidates.Add(new AnimationDataPathCandidate(
                    Name: memberPath,
                    Source: "Runtime reflection",
                    Address: "managed/reflection",
                    Offset: "unknown",
                    Value: FormatValue(value),
                    ValueTypeGuess: valueType?.Name ?? "unknown",
                    LinkedSource: LinkSource(memberPath),
                    IsAppearanceField: safety == "UnsafeAppearance",
                    IsAnimationResolverCandidate: safety is "CandidateAnimationOnly" or "ReadOnly",
                    SafetyLevel: safety));
            }

            if (value == null || value is string || valueType == null || valueType.IsPrimitive || valueType.IsEnum)
                continue;

            if (LooksContainer(member.Name) || depth == 0)
                AppendReflectionCandidates(candidates, value, memberPath, depth + 1, visited);
        }
    }

    private static bool LooksInteresting(string name)
        => ReflectionTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool LooksContainer(string name)
        => name.Contains("Actor", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Unsafe", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Draw", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Animation", StringComparison.OrdinalIgnoreCase);

    private static string ClassifySafety(string path)
    {
        if (path.Contains("DataPath", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("DataHead", StringComparison.OrdinalIgnoreCase))
        {
            return "CandidateAnimationOnly";
        }

        if (path.Contains("Customize", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Race", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Gender", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Sex", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Tribe", StringComparison.OrdinalIgnoreCase))
        {
            return "UnsafeAppearance";
        }

        if (path.Contains("Skeleton", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Animation", StringComparison.OrdinalIgnoreCase))
        {
            return "ReadOnly";
        }

        return "Unknown";
    }

    private static string LinkSource(string path)
    {
        if (path.Contains("DataPath", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("DataHead", StringComparison.OrdinalIgnoreCase))
        {
            return "Anamnesis Actor.Unsafe.ModelObject.DataPath/DataHead";
        }

        if (path.Contains("Customize", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Race", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Gender", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Tribe", StringComparison.OrdinalIgnoreCase))
        {
            return "Appearance/customize field; do not write for AnimationRig";
        }

        if (path.Contains("Skeleton", StringComparison.OrdinalIgnoreCase))
            return "Skeleton pointer/resource read-only probe";

        if (path.Contains("Timeline", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Animation", StringComparison.OrdinalIgnoreCase))
            return "ActionTimeline playback/binding read-only probe";

        return "Unmapped";
    }

    private static string BuildSummary(IReadOnlyList<AnimationDataPathCandidate> candidates)
    {
        var groups = candidates
            .GroupBy(candidate => candidate.SafetyLevel)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");
        var important = candidates
            .Where(candidate => candidate.Name.Contains("DataPath", StringComparison.OrdinalIgnoreCase) ||
                                candidate.Name.Contains("DataHead", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(candidate => $"{candidate.Name}@{candidate.Offset}:{candidate.Value}");
        return $"count={candidates.Count}; {string.Join(", ", groups)}; dataPath={string.Join(" | ", important)}";
    }

    private static string BuildReport(
        RuntimeActorInstance actor,
        nint actorPtr,
        nint drawObjectPtr,
        nint timelineContainerPtr,
        ActorAnimationRigPreset selectedRig,
        IReadOnlyList<AnimationDataPathCandidate> candidates,
        string summary)
        => string.Join(Environment.NewLine, new[]
        {
            "[AnimationDataPathCandidateScanner]",
            $"actor={actor.RuntimeId}",
            $"display={actor.DisplayName}",
            $"selectedRig={selectedRig}",
            $"actorPtr={FormatPointer(actorPtr)}",
            $"drawObjectPtr={FormatPointer(drawObjectPtr)}",
            $"timelineContainerPtr={FormatPointer(timelineContainerPtr)}",
            $"summary={summary}",
            "candidates:",
            string.Join(Environment.NewLine, candidates.Select(candidate =>
                $"- name={candidate.Name}; safety={candidate.SafetyLevel}; source={candidate.Source}; address={candidate.Address}; offset={candidate.Offset}; value={candidate.Value}; type={candidate.ValueTypeGuess}; linked={candidate.LinkedSource}; appearance={candidate.IsAppearanceField}; resolver={candidate.IsAnimationResolverCandidate}")),
        });

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
                if (items.Count >= 8)
                    break;
            }

            return "[" + string.Join(",", items) + "]";
        }

        return value.ToString() ?? value.GetType().Name;
    }

    private static string FormatDataPath(short value) => $"{value} (0x{unchecked((ushort)value):X4})";

    private static string FormatDataHead(byte value) => $"{value} (0x{value:X2})";

    private static string FormatPointer(nint pointer) => pointer == 0 ? "0x0" : $"0x{pointer.ToInt64():X}";

    private sealed record DataPathRawRead(short? CurrentDataPath, byte? CurrentDataHead, string Error);
}

public sealed record AnimationDataPathCandidateReport(
    IReadOnlyList<AnimationDataPathCandidate> Candidates,
    string Summary,
    string Report,
    short? CurrentDataPath,
    byte? CurrentDataHead,
    string ReadError);

public sealed record AnimationDataPathCandidate(
    string Name,
    string Source,
    string Address,
    string Offset,
    string Value,
    string ValueTypeGuess,
    string LinkedSource,
    bool IsAppearanceField,
    bool IsAnimationResolverCandidate,
    string SafetyLevel);
