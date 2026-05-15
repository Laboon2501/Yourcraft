using System.Numerics;
using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

[Serializable]
public sealed class LocalLightInstance
{
    public string Id { get; set; } = $"local-light-{Guid.NewGuid():N}";

    public string Name { get; set; } = "本地灯光";

    public bool Enabled { get; set; } = true;

    public bool Hidden { get; set; }

    public uint TerritoryId { get; set; }

    public LocalLightKind LightKind { get; set; } = LocalLightKind.Point;

    public Vector3 Position { get; set; }

    public Vector3 Rotation { get; set; }

    public Vector3 Scale { get; set; } = Vector3.One;

    public Vector3 ColorRgb { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 25f;

    public float Range { get; set; } = 25f;

    public LocalLightFalloffType FalloffType { get; set; } = LocalLightFalloffType.Quadratic;

    public float Falloff { get; set; } = 1f;

    public float LightAngle { get; set; } = 35f;

    public float FalloffAngle { get; set; } = 15f;

    public float AreaAngleX { get; set; } = 35f;

    public float AreaAngleY { get; set; } = 35f;

    public bool EnableSpecular { get; set; } = true;

    public bool EnableDynamicShadows { get; set; }

    public string LastOperation { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public string LastReadback { get; set; } = string.Empty;

    public bool ManuallyConfirmedVisible { get; set; }

    public bool ManuallyConfirmedNotVisible { get; set; }

    [JsonIgnore]
    internal nint NativeSceneLight { get; set; }

    [JsonIgnore]
    internal nint NativeRenderLight { get; set; }

    [JsonIgnore]
    internal bool NativeOperationPending { get; set; }

    [JsonIgnore]
    internal bool NeedsNativeRecreate { get; set; }

    [JsonIgnore]
    internal bool IsNativeCreated => this.NativeSceneLight != 0;
}
