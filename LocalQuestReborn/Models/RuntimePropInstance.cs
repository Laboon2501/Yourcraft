using System.Numerics;
using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class RuntimePropInstance
{
    public string RuntimeId { get; set; } = Guid.NewGuid().ToString("N");

    public string PropId { get; set; } = string.Empty;

    public string PropName { get; set; } = string.Empty;

    public string ObjectIndex { get; set; } = "不可用";

    public string Address { get; set; } = "不可用";

    public string DrawObjectAddress { get; set; } = "不可用";

    public string ModelPath { get; set; } = string.Empty;

    public Vector3 Position { get; set; }

    public float Rotation { get; set; }

    public float Scale { get; set; } = 1f;

    public bool IsValid { get; set; }

    public string SpawnMethod { get; set; } = string.Empty;

    public string ObjectType { get; set; } = "未知";

    public bool IsCharacterClone { get; set; }

    public bool IsBrioProp { get; set; }

    public string PropDataFields { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public string LastModelResult { get; set; } = string.Empty;

    public string DrawObjectDump { get; set; } = string.Empty;

    public string ModelResourceDump { get; set; } = string.Empty;

    [JsonIgnore]
    public object? CharacterObject { get; set; }
}
