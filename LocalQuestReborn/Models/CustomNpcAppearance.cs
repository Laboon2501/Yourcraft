using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class CustomNpcAppearance
{
    [JsonPropertyName("sourceType")]
    public CustomNpcAppearanceSourceType SourceType { get; set; } = CustomNpcAppearanceSourceType.None;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("glamourerDesignId")]
    public string GlamourerDesignId { get; set; } = string.Empty;

    [JsonPropertyName("penumbraCollectionName")]
    public string PenumbraCollectionName { get; set; } = string.Empty;

    [JsonPropertyName("mcdfPath")]
    public string McdfPath { get; set; } = string.Empty;

    [JsonPropertyName("gameNpcName")]
    public string GameNpcName { get; set; } = string.Empty;

    [JsonPropertyName("gameNpcKind")]
    public GameNpcKind GameNpcKind { get; set; } = GameNpcKind.Unknown;

    [JsonPropertyName("gameNpcBaseId")]
    public uint GameNpcBaseId { get; set; }

    [JsonPropertyName("gameNpcModelId")]
    public uint GameNpcModelId { get; set; }

    [JsonPropertyName("gameNpcCustomizeId")]
    public uint GameNpcCustomizeId { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

public enum CustomNpcAppearanceSourceType
{
    None,
    CurrentPlayer,
    GlamourerDesign,
    PenumbraCollection,
    MCDF,
    GameNpc,
}

public enum GameNpcKind
{
    ENpc,
    BNpc,
    ModelChara,
    Mount,
    Companion,
    Monster,
    Unknown,
}
