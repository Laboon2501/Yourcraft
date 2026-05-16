namespace Yourcraft.Models;

public sealed record ActorAnimationCatalogEntry(
    ushort ActionTimelineId,
    uint SourceRowId,
    string Name,
    string Command,
    string SourceType,
    string Purpose,
    string Key,
    string Slot,
    bool IsLoopCandidate);
