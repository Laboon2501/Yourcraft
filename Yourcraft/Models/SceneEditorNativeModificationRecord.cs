namespace Yourcraft.Models;

public sealed class SceneEditorNativeModificationRecord
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");

    public string StableKey { get; set; } = string.Empty;

    public string RuntimeIdAtRecordTime { get; set; } = string.Empty;

    public string NativeAddress { get; set; } = string.Empty;

    public SceneEditableKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string MdlPath { get; set; } = string.Empty;

    public uint TerritoryId { get; set; }

    public int ObjectIndexAtRecordTime { get; set; } = -1;

    public string DataId { get; set; } = string.Empty;

    public bool IsInteractableNpc { get; set; }

    public bool IsHidden { get; set; }

    public bool IsModified { get; set; }

    public bool RuntimeOnly { get; set; }

    public bool PreferredModifyAdded { get; set; }

    public string PreferredModifyStatus { get; set; } = string.Empty;

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
}
