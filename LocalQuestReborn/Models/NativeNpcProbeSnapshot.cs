namespace LocalQuestReborn.Models;

public sealed class NativeNpcProbeSnapshot
{
    public string Label { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; } = DateTime.Now;

    public Dictionary<string, NativeNpcProbeField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record NativeNpcProbeField(string Value, string Source);
