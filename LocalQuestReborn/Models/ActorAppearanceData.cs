using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class ActorAppearanceData
{
    [JsonPropertyName("sourceKind")]
    public ActorAppearanceSourceKind SourceKind { get; set; } = ActorAppearanceSourceKind.None;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("isHumanoid")]
    public bool IsHumanoid { get; set; } = true;

    [JsonPropertyName("spawnKind")]
    public ActorSpawnKind SpawnKind { get; set; } = ActorSpawnKind.Character;

    [JsonPropertyName("sourceActorKind")]
    public string SourceActorKind { get; set; } = string.Empty;

    [JsonPropertyName("objectKind")]
    public string ObjectKind { get; set; } = string.Empty;

    [JsonPropertyName("modelCharaId")]
    public uint ModelCharaId { get; set; }

    [JsonPropertyName("modelCharaOverrideId")]
    public uint ModelCharaOverrideId { get; set; }

    [JsonPropertyName("modelSkeletonId")]
    public uint ModelSkeletonId { get; set; }

    [JsonPropertyName("customize")]
    public ActorCustomizeData Customize { get; set; } = new();

    [JsonPropertyName("equipment")]
    public ActorEquipmentData Equipment { get; set; } = new();

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public sealed class ActorCustomizeData
{
    public byte Race { get; set; }
    public byte Sex { get; set; }
    public byte BodyType { get; set; } = 1;
    public byte Height { get; set; }
    public byte Tribe { get; set; }
    public byte Face { get; set; }
    public byte HairStyle { get; set; }
    public byte Highlights { get; set; }
    public byte SkinColor { get; set; }
    public byte EyeColorRight { get; set; }
    public byte HairColor { get; set; }
    public byte HighlightsColor { get; set; }
    public byte FacialFeatures { get; set; }
    public byte FacialFeaturesColor { get; set; }
    public byte Eyebrows { get; set; }
    public byte EyeColorLeft { get; set; }
    public byte EyeShape { get; set; }
    public byte Nose { get; set; }
    public byte Jaw { get; set; }
    public byte Lipstick { get; set; }
    public byte LipColorFurPattern { get; set; }
    public byte MuscleMass { get; set; }
    public byte TailShape { get; set; }
    public byte BustSize { get; set; }
    public byte FacePaint { get; set; }
    public byte FacePaintColor { get; set; }
    public string RawCustomizeBase64 { get; set; } = string.Empty;
}

public sealed class ActorEquipmentData
{
    public ActorWeaponModelData? MainHand { get; set; }
    public ActorWeaponModelData? OffHand { get; set; }
    public ActorEquipmentModelData? Head { get; set; }
    public ActorEquipmentModelData? Body { get; set; }
    public ActorEquipmentModelData? Hands { get; set; }
    public ActorEquipmentModelData? Legs { get; set; }
    public ActorEquipmentModelData? Feet { get; set; }
    public ActorEquipmentModelData? Ears { get; set; }
    public ActorEquipmentModelData? Neck { get; set; }
    public ActorEquipmentModelData? Wrists { get; set; }
    public ActorEquipmentModelData? LeftRing { get; set; }
    public ActorEquipmentModelData? RightRing { get; set; }
    public bool HideWeapons { get; set; }
    public bool HideHeadgear { get; set; }
}

public sealed class ActorWeaponModelData
{
    public ushort ModelSetId { get; set; }
    public ushort Base { get; set; }
    public ushort Variant { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}

public sealed class ActorEquipmentModelData
{
    public ushort ModelId { get; set; }
    public byte Variant { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}

public enum ActorAppearanceSourceKind
{
    None,
    CurrentPlayer,
    GlamourerDesign,
    GlamourerNpc,
    ManualSnapshot,
    GameNpc,
    Local,
}

public enum ActorSpawnKind
{
    Unknown,
    Character,
    Demihuman,
    Mount,
    Minion,
    Unsupported,
}
