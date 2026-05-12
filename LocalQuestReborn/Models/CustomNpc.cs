using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

public sealed class CustomNpc
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "未命名 NPC";

    public string NameTemplate { get; set; } = "{name}";

    public ushort TerritoryType { get; set; }

    public Vector3Data Position { get; set; } = new();

    public float InteractRadius { get; set; } = 6f;

    public uint DefaultAnimationId { get; set; }

    public bool AutoPlayDefaultAnimation { get; set; }

    public bool LookAtPlayerEnabled { get; set; }

    public float LookAtRadius { get; set; } = 8f;

    public NpcLookAtMode LookAtMode { get; set; } = NpcLookAtMode.None;

    public bool RespawnAfterGpose { get; set; } = true;

    public CustomNpcHostMode HostMode { get; set; } = CustomNpcHostMode.VirtualActor;

    public NativeHostMode NativeHostMode { get; set; } = NativeHostMode.None;

    public uint HostDataId { get; set; }

    public string HostObjectIndex { get; set; } = string.Empty;

    public ushort HostTerritoryType { get; set; }

    public string HostName { get; set; } = string.Empty;

    public bool OverrideNativeName { get; set; }

    public bool InterceptNativeTalk { get; set; }

    public bool UseLocalDialogueOnInteract { get; set; }

    public HostInterceptMode InterceptMode { get; set; } = HostInterceptMode.ManualCommand;

    public bool OverrideDialogueEnabled { get; set; } = true;

    [JsonPropertyName("appearance")]
    public CustomNpcAppearance Appearance { get; set; } = new();
}

public enum CustomNpcHostMode
{
    VirtualActor,
    ExistingEventNpcHost,
}

public enum HostInterceptMode
{
    ManualCommand,
    ConfirmKey,
    NativeTalkAddon,
}

public enum NativeHostMode
{
    None,
    SpawnedEventNpcExperiment,
    ExistingEventNpcHost,
}

public enum NpcLookAtMode
{
    None,
    BodyYaw,
    NativeLookAt,
    HeadOnly,
}
