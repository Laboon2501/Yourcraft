namespace LocalQuestReborn.Models;

public sealed class PersistentActorConfig
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ConfigId { get; set; } = Guid.NewGuid().ToString("N");

    public string RuntimeId { get; set; } = Guid.NewGuid().ToString("N");

    public string SourceNpcPresetId { get; set; } = string.Empty;

    public string NpcNameSnapshot { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ActorAppearanceData Appearance { get; set; } = new();

    public ushort TerritoryType { get; set; }

    public string TerritoryName { get; set; } = string.Empty;

    public Vector3Data WorldPosition { get; set; } = new();

    public Vector3Data WorldRotationEuler { get; set; } = new();

    public Vector3Data WorldScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public uint DefaultAnimationId { get; set; }

    public uint CurrentAnimationId { get; set; }

    public bool AnimationEnabled { get; set; }

    public bool AutoPlayDefaultAnimation { get; set; }

    public bool LookAtPlayerEnabled { get; set; }

    public float LookAtRadius { get; set; } = 8f;

    public bool EnableActionSequence { get; set; }

    public bool ActionSequenceLoop { get; set; }

    public float ActionSequenceLoopDelay { get; set; }

    public List<ActorActionSequenceStep> ActionSequence { get; set; } = [];

    public PenumbraCollectionMode PenumbraMode { get; set; } = PenumbraCollectionMode.DoNotTouch;

    public Guid? PenumbraCollectionId { get; set; }

    public string PenumbraCollectionNameCache { get; set; } = string.Empty;

    public bool AutoSpawn { get; set; } = true;

    public int SortOrder { get; set; } = int.MaxValue;

    public long SpawnSequence { get; set; }

    public string Notes { get; set; } = string.Empty;
}
