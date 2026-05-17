using System.Text.Json.Serialization;

namespace Yourcraft.Models;

public sealed class CustomNpc
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "未命名 NPC";

    public string NameTemplate { get; set; } = "{name}";

    // Legacy spawn fields from older versions. Kept for JSON compatibility, but
    // NPC templates are no longer bound to a specific map or position.
    public ushort TerritoryType { get; set; }

    public Vector3Data Position { get; set; } = new();

    public ushort LegacyDefaultTerritoryType { get; set; }

    public Vector3Data DefaultSpawnOffset { get; set; } = new();

    public Vector3Data DefaultRotationEuler { get; set; } = new();

    public Vector3Data DefaultScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public uint DefaultAnimationId { get; set; }

    public bool AutoPlayDefaultAnimation { get; set; }

    public bool LookAtPlayerEnabled { get; set; }

    public float LookAtRadius { get; set; } = 8f;

    public NpcLookAtMode LookAtMode { get; set; } = NpcLookAtMode.None;

    public bool RespawnAfterGpose { get; set; } = true;

    public PenumbraCollectionMode PenumbraMode { get; set; } = PenumbraCollectionMode.DoNotTouch;

    public Guid? PenumbraCollectionId { get; set; }

    public string PenumbraCollectionNameCache { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("appearance")]
    public CustomNpcAppearance Appearance { get; set; } = new();
}

public enum NpcLookAtMode
{
    None,
    BodyYaw,
    NativeLookAt,
    HeadOnly,
}
