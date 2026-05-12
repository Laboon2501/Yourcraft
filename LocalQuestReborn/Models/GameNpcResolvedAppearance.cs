using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class GameNpcResolvedAppearance
{
    [JsonPropertyName("kind")]
    public GameNpcResolvedAppearanceKind Kind { get; set; } = GameNpcResolvedAppearanceKind.Unknown;

    [JsonPropertyName("modelCharaId")]
    public uint ModelCharaId { get; set; }

    [JsonPropertyName("customize")]
    public GameNpcResolvedCustomize Customize { get; set; } = new();

    [JsonPropertyName("equipment")]
    public GameNpcResolvedEquipment Equipment { get; set; } = new();

    [JsonPropertyName("debugInfo")]
    public string DebugInfo { get; set; } = string.Empty;
}

public sealed class GameNpcResolvedCustomize
{
    public uint Race { get; set; }
    public uint Gender { get; set; }
    public uint Tribe { get; set; }
    public uint BodyType { get; set; }
    public uint Height { get; set; }
    public uint Face { get; set; }
    public uint HairStyle { get; set; }
    public uint HairHighlight { get; set; }
    public uint SkinColor { get; set; }
    public uint EyeHeterochromia { get; set; }
    public uint HairColor { get; set; }
    public uint HairHighlightColor { get; set; }
    public uint FacialFeature { get; set; }
    public uint FacialFeatureColor { get; set; }
    public uint Eyebrows { get; set; }
    public uint EyeColor { get; set; }
    public uint EyeShape { get; set; }
    public uint Nose { get; set; }
    public uint Jaw { get; set; }
    public uint Mouth { get; set; }
    public uint LipColor { get; set; }
    public uint BustOrTone1 { get; set; }
    public uint ExtraFeature1 { get; set; }
    public uint ExtraFeature2OrBust { get; set; }
    public uint FacePaint { get; set; }
    public uint FacePaintColor { get; set; }
}

public sealed class GameNpcResolvedEquipment
{
    public uint MainHand { get; set; }
    public uint OffHand { get; set; }
    public uint Head { get; set; }
    public uint Body { get; set; }
    public uint Hands { get; set; }
    public uint Legs { get; set; }
    public uint Feet { get; set; }
    public uint Ears { get; set; }
    public uint Neck { get; set; }
    public uint Wrists { get; set; }
    public uint LeftRing { get; set; }
    public uint RightRing { get; set; }
}

public enum GameNpcResolvedAppearanceKind
{
    Humanoid,
    Monster,
    Unknown,
}
