using System.Text.Json.Serialization;

namespace Yourcraft.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestObjectiveType
{
    Manual,
    ReachPosition,
    TalkToNpc,
}

public sealed class QuestObjective
{
    public string Id { get; set; } = string.Empty;

    public QuestObjectiveType Type { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? TargetNpcId { get; set; }

    public ushort TerritoryType { get; set; }

    public Vector3Data? Position { get; set; }

    public float Radius { get; set; } = 3f;
}
