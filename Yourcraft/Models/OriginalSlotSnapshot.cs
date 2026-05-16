using System.Numerics;

namespace Yourcraft.Models;

public sealed class OriginalSlotSnapshot
{
    public string InstanceId { get; set; } = string.Empty;

    public string OccupiedSlotAddress { get; set; } = string.Empty;

    public string OriginalResourcePath { get; set; } = string.Empty;

    public string OriginalPrimaryPath { get; set; } = string.Empty;

    public string OriginalModelHandlePath { get; set; } = string.Empty;

    public Vector3 OriginalLayoutPosition { get; set; }

    public Quaternion OriginalLayoutRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalLayoutScale { get; set; } = Vector3.One;

    public Vector3 OriginalGraphicsPosition { get; set; }

    public Quaternion OriginalGraphicsRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalGraphicsScale { get; set; } = Vector3.One;

    public bool OriginalVisible { get; set; } = true;

    public uint OriginalCollisionMeshPathCrc { get; set; }

    public uint OriginalAnalyticShapeDataCrc { get; set; }

    public uint OriginalMaterialIdLow { get; set; }

    public uint OriginalMaterialMaskLow { get; set; }

    public uint OriginalMaterialIdHigh { get; set; }

    public uint OriginalMaterialMaskHigh { get; set; }

    public bool OriginalHadCollider { get; set; }

    public string OriginalSecondaryPath { get; set; } = string.Empty;

    public string OriginalSourceType { get; set; } = string.Empty;

    public string SourceLabel { get; set; } = string.Empty;
}
