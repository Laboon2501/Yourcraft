using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class SceneEditorLocalActorRecord
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");

    public string RuntimeId { get; set; } = string.Empty;

    public string NpcId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public uint TerritoryId { get; set; }

    public int SortOrder { get; set; }

    public bool Enabled { get; set; } = true;

    public Vector3 WorldPosition { get; set; }

    public Vector3 WorldRotationEuler { get; set; }

    public Vector3 WorldScale { get; set; } = Vector3.One;

    public DateTime LastSavedAt { get; set; } = DateTime.UtcNow;

    public string RestoreStatus { get; set; } = string.Empty;
}
