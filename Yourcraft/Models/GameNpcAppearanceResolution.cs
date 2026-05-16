using System.Text.Json.Serialization;

namespace Yourcraft.Models;

public sealed class GameNpcAppearanceResolution
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("modelCharaId")]
    public uint ModelCharaId { get; set; }

    [JsonPropertyName("customizeId")]
    public uint? CustomizeId { get; set; }

    [JsonPropertyName("residentRowId")]
    public uint ResidentRowId { get; set; }

    [JsonPropertyName("baseRowId")]
    public uint BaseRowId { get; set; }

    [JsonPropertyName("sourceKind")]
    public GameNpcKind SourceKind { get; set; } = GameNpcKind.Unknown;

    [JsonPropertyName("foundENpcBase")]
    public bool FoundENpcBase { get; set; }

    [JsonPropertyName("foundBNpcBase")]
    public bool FoundBNpcBase { get; set; }

    [JsonPropertyName("equipmentInfo")]
    public string EquipmentInfo { get; set; } = string.Empty;

    [JsonPropertyName("rawDebugInfo")]
    public string RawDebugInfo { get; set; } = string.Empty;

    [JsonPropertyName("appearance")]
    public GameNpcResolvedAppearance Appearance { get; set; } = new();

    [JsonPropertyName("chain")]
    public List<string> Chain { get; set; } = [];

    [JsonPropertyName("failureStep")]
    public string FailureStep { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
