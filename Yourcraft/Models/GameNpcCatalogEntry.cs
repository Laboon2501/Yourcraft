using System.Text.Json.Serialization;

namespace Yourcraft.Models;

public sealed class GameNpcCatalogEntry
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public GameNpcCatalogKind SourceKind { get; set; } = GameNpcCatalogKind.Unknown;

    [JsonPropertyName("residentRowId")]
    public uint ResidentRowId { get; set; }

    [JsonPropertyName("baseRowId")]
    public uint BaseRowId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public GameNpcCatalogKind Kind { get; set; } = GameNpcCatalogKind.Unknown;

    [JsonPropertyName("rowId")]
    public uint RowId { get; set; }

    [JsonPropertyName("modelCharaId")]
    public uint ModelCharaId { get; set; }

    [JsonPropertyName("customizeId")]
    public uint? CustomizeId { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("debugInfo")]
    public string DebugInfo { get; set; } = string.Empty;

    [JsonPropertyName("race")]
    public string Race { get; set; } = string.Empty;

    [JsonPropertyName("gender")]
    public string Gender { get; set; } = string.Empty;

    [JsonPropertyName("tribe")]
    public string Tribe { get; set; } = string.Empty;

    [JsonPropertyName("equipmentInfo")]
    public string EquipmentInfo { get; set; } = string.Empty;

    [JsonPropertyName("rawDebugInfo")]
    public string RawDebugInfo { get; set; } = string.Empty;
}

public enum GameNpcCatalogKind
{
    ENpc,
    BNpc,
    ModelChara,
    Mount,
    Companion,
    Unknown,
}
