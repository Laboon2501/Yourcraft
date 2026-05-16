using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class ActorLipSyncPresetService
{
    public const string StopKey = "speak/stop";
    private const string LegacyPrefix = "legacy:";

    private static readonly string[] PresetKeys =
    [
        StopKey,
        "speak/whisper_short",
        "speak/whisper_middle",
        "speak/whisper_long",
        "speak/normal_short",
        "speak/normal_middle",
        "speak/normal_long",
        "speak/shout_short",
        "speak/shout_middle",
        "speak/shout_long",
        "speak/whisper_short_nolip",
        "speak/whisper_middle_nolip",
        "speak/whisper_long_nolip",
        "speak/normal_short_nolip",
        "speak/normal_middle_nolip",
        "speak/normal_long_nolip",
        "speak/shout_short_nolip",
        "speak/shout_middle_nolip",
        "speak/shout_long_nolip",
    ];

    private readonly ActorAnimationCatalogService catalog;

    public ActorLipSyncPresetService(ActorAnimationCatalogService catalog)
    {
        this.catalog = catalog;
    }

    public IReadOnlyList<LipAnimationEntry> Entries
        => PresetKeys.Select(this.ResolvePresetKey).ToList();

    public IReadOnlyList<LipAnimationEntry> EntriesWithLegacy(string? key, uint legacyTimelineId)
    {
        var entries = this.Entries.ToList();
        var current = this.Resolve(key, legacyTimelineId);
        if (current.IsLegacy && entries.All(entry => !string.Equals(entry.InternalKey, current.InternalKey, StringComparison.OrdinalIgnoreCase)))
            entries.Add(current);
        return entries;
    }

    public LipAnimationEntry Resolve(string? key, uint legacyTimelineId)
    {
        var normalized = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (string.Equals(normalized, StopKey, StringComparison.OrdinalIgnoreCase) && legacyTimelineId != 0)
            {
                var matchingPreset = this.Entries.FirstOrDefault(entry => entry.ResolvedTimelineId == legacyTimelineId && entry.IsResolved);
                return matchingPreset ?? ResolveLegacy(legacyTimelineId);
            }

            if (normalized.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(normalized[LegacyPrefix.Length..], out var legacyFromKey))
            {
                return ResolveLegacy(legacyFromKey);
            }

            var known = PresetKeys.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(known))
                return this.ResolvePresetKey(known);
        }

        if (legacyTimelineId != 0)
        {
            var matchingPreset = this.Entries.FirstOrDefault(entry => entry.ResolvedTimelineId == legacyTimelineId && entry.IsResolved);
            return matchingPreset ?? ResolveLegacy(legacyTimelineId);
        }

        return this.ResolvePresetKey(StopKey);
    }

    public bool TryResolveTimelineId(string? key, uint legacyTimelineId, out uint timelineId, out LipAnimationEntry entry, out string reason)
    {
        entry = this.Resolve(key, legacyTimelineId);
        timelineId = entry.ResolvedTimelineId;
        if (entry.InternalKey == StopKey)
        {
            reason = "Lip preset resolved to speak/stop.";
            return true;
        }

        if (entry.IsResolved || entry.IsLegacy)
        {
            reason = $"Lip preset resolved: {entry.DisplayName} -> {timelineId}.";
            return true;
        }

        reason = $"Lip preset could not be resolved from ActionTimeline sheet: {entry.InternalKey}.";
        return false;
    }

    private LipAnimationEntry ResolvePresetKey(string key)
    {
        if (string.Equals(key, StopKey, StringComparison.OrdinalIgnoreCase))
            return new LipAnimationEntry(StopKey, StopKey, 0, true, false, "clear lip override");

        var match = this.catalog.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return new LipAnimationEntry(key, key, match.ActionTimelineId, true, false, $"ActionTimelineId={match.ActionTimelineId}");

        return new LipAnimationEntry($"{key} (unresolved)", key, 0, false, false, "missing ActionTimeline key");
    }

    private static LipAnimationEntry ResolveLegacy(uint timelineId)
        => new($"Legacy/Unknown {timelineId}", $"{LegacyPrefix}{timelineId}", timelineId, true, true, "legacy numeric ActionTimelineId");
}
