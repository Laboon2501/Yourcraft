namespace Yourcraft.Models;

public sealed class SceneEditorLocalBgPartRecord
{
    public string InstanceId { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public uint TerritoryId { get; set; }

    public string SourceMdlPath { get; set; } = string.Empty;

    public string CurrentMdlPath { get; set; } = string.Empty;

    public string CustomMdlPath { get; set; } = string.Empty;

    public LocalLayoutTransformMode CollisionMode { get; set; } = LocalLayoutTransformMode.VisualOnly;

    public string SourceBgPartStableKey { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SourceSharedGroupPath { get; set; } = string.Empty;

    public int SourceChildIndex { get; set; } = -1;

    public bool Enabled { get; set; } = true;

    public bool Hidden { get; set; }

    public string RestoreStatus { get; set; } = string.Empty;

    public Vector3Data WorldPosition { get; set; } = new();

    public Vector3Data WorldRotationEuler { get; set; } = new();

    public Vector3Data WorldScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public DateTime LastSavedAt { get; set; } = DateTime.UtcNow;
}
