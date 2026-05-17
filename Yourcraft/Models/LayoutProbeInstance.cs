using System.Numerics;

namespace Yourcraft.Models;

public sealed class LayoutProbeInstance
{
    public int Index { get; set; }

    public string Key { get; set; } = string.Empty;

    public string StableKey { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string InstanceType { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string LayerAddress { get; set; } = string.Empty;

    public Vector3 Position { get; set; }

    public Quaternion RotationQuaternion { get; set; } = Quaternion.Identity;

    public string Rotation { get; set; } = "未读取";

    public Vector3 Scale { get; set; } = Vector3.One;

    public string ResourcePath { get; set; } = "未读取";

    public bool Visible { get; set; } = true;

    public string LayerId { get; set; } = "未读取";

    public string GroupId { get; set; } = "未读取";

    public float DistanceToPlayer { get; set; }

    public string Source { get; set; } = string.Empty;

    public string SourceKind { get; set; } = "LoadedLayout";

    public string SharedGroupPath { get; set; } = string.Empty;

    public string ParentAddress { get; set; } = string.Empty;

    public string ParentKey { get; set; } = string.Empty;

    public int ChildIndex { get; set; } = -1;

    public string DebugInfo { get; set; } = string.Empty;

    public string CarrierRejectReason { get; set; } = string.Empty;

    public string CarrierWarningReason { get; set; } = string.Empty;

    public string CarrierAllocationStage { get; set; } = string.Empty;
}
