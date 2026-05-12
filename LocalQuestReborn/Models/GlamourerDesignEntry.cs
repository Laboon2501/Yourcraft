using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class GlamourerDesignEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("rawJsonPreview")]
    public string RawJsonPreview { get; set; } = string.Empty;

    [JsonPropertyName("sourceDescription")]
    public string SourceDescription { get; set; } = string.Empty;
}
