using System.Numerics;

namespace Yourcraft.Models;

public sealed record SceneEditableRef(
    string RuntimeId,
    SceneEditableKind Kind,
    nint NativePtr,
    int ObjectIndex,
    string DisplayName,
    string MdlPath,
    bool IsPluginCreated,
    WorldTransform Transform,
    bool IsValid,
    bool IsHidden)
{
    public bool IsNativeGameObject { get; init; }

    public bool IsPlayer { get; init; }

    public bool IsInteractableNpc { get; init; }

    public string StableKey { get; init; } = string.Empty;

    public bool TransformEditable { get; init; } = true;

    public string ObjectKind { get; init; } = string.Empty;

    public string DataId { get; init; } = string.Empty;

    public string NativeInfo { get; init; } = string.Empty;

    public string RotationText { get; init; } = string.Empty;

    public string LightInfo { get; init; } = string.Empty;

    public LayoutProbeInstance? LayoutProbe { get; init; }

    public Vector3 MarkerWorldPosition
        => this.Transform.WorldPosition;
}
