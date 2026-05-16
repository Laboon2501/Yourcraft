namespace Yourcraft.Models;

public enum LocalLayoutTransformMode
{
    VisualOnly,
    FullLayoutWithCollision,
}

public enum CarrierAllocationPolicy
{
    PreferredListThenAnyValid,
    PreferredSameModelThenFarthestSafe,
    StrictFarthestSafe,
    DebugNearest,
    SafeOnly,
    ExpandedStatic,
    AnyValidBgPart,
}

public enum CarrierRejectReason
{
    None,
    TemplateSlot,
    Protected,
    Occupied,
    Reserved,
    InvalidGraphicsObject,
    InvalidModelHandle,
    DynamicControlled,
    SharedGroupChild,
    UnsafeComplex,
    TerrainLike,
    FloorLike,
    WallLike,
    StructureLike,
    TooLarge,
    TooCloseImportantGeometry,
    UserBlacklist,
    Unknown,
}
