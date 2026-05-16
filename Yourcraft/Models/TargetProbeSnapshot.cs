namespace Yourcraft.Models;

public sealed class TargetProbeSnapshot
{
    public string Label { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; } = DateTime.Now;

    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
