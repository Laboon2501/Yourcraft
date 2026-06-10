namespace Yourcraft.Models;

public sealed class SceneEditorNativeModificationRecord
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");

    public string StableKey { get; set; } = string.Empty;

    public string TerritoryKey { get; set; } = string.Empty;

    public string NativeBgPartStableKey { get; set; } = string.Empty;

    public string NativeBgPartSgbPath { get; set; } = string.Empty;

    public string NativeBgPartAssetPath { get; set; } = string.Empty;

    public string NativeBgPartModelPath { get; set; } = string.Empty;

    public int NativeBgPartInitialIndex { get; set; } = -1;

    public string NativeBgPartInitialAddress { get; set; } = string.Empty;

    public string NamePath { get; set; } = string.Empty;

    public string LayoutInstanceKey { get; set; } = string.Empty;

    public string LayoutInstanceAddress { get; set; } = string.Empty;

    public int LayoutInstanceIndexAtRecordTime { get; set; } = -1;

    public string RuntimeIdAtRecordTime { get; set; } = string.Empty;

    public string NativeAddress { get; set; } = string.Empty;

    public SceneEditableKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string MdlPath { get; set; } = string.Empty;

    public string ObjectKind { get; set; } = string.Empty;

    public string LayoutSource { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SharedGroupPath { get; set; } = string.Empty;

    public string ParentStableKey { get; set; } = string.Empty;

    public int ChildIndex { get; set; } = -1;

    public uint TerritoryId { get; set; }

    public int ObjectIndexAtRecordTime { get; set; } = -1;

    public string DataId { get; set; } = string.Empty;

    public bool IsInteractableNpc { get; set; }

    public bool IsHidden { get; set; }

    public bool IsModified { get; set; }

    public bool RuntimeOnly { get; set; }

    public bool PreferredModifyAdded { get; set; }

    public string PreferredModifyStatus { get; set; } = string.Empty;

    public string UsedByLocalBgPartInstanceId { get; set; } = string.Empty;

    public string UsedByLocalBgPartSlotAddress { get; set; } = string.Empty;

    public string UsedByLocalBgPartMdlPath { get; set; } = string.Empty;

    public bool UseFullLayoutTransform { get; set; }

    public Vector3Data OriginalPosition { get; set; } = new();

    public Vector3Data OriginalRotationEuler { get; set; } = new();

    public Vector3Data OriginalScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public Vector3Data CurrentPosition { get; set; } = new();

    public Vector3Data CurrentRotationEuler { get; set; } = new();

    public Vector3Data CurrentScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

    public string Reason { get; set; } = string.Empty;

    public string Status { get; set; } = "Modified";

    public Vector3Data HiddenPosition { get; set; } = new();

    public Vector3Data HiddenRotationEuler { get; set; } = new();

    public Vector3Data HiddenScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public SceneEditorNativeLightState OriginalLightState { get; set; } = new();

    public SceneEditorNativeLightState CurrentLightState { get; set; } = new();
}

public sealed class SceneEditorNativeLightState
{
    public bool HasState { get; set; }

    public Vector3Data Position { get; set; } = new();

    public Vector3Data RotationEuler { get; set; } = new();

    public Vector3Data Scale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public bool Visible { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public bool HasRenderState { get; set; }

    public Vector3Data Color { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public float Intensity { get; set; }

    public float Range { get; set; }

    public float Falloff { get; set; }

    public float SpotAngle { get; set; }

    public float FalloffAngle { get; set; }
}
