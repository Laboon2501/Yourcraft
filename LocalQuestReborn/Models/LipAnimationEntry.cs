namespace LocalQuestReborn.Models;

public sealed record LipAnimationEntry(
    string DisplayName,
    string InternalKey,
    uint ResolvedTimelineId,
    bool IsResolved,
    bool IsLegacy,
    string Status);
