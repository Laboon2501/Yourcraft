using System.Numerics;

namespace Yourcraft.Models;

public enum StandaloneObjectState
{
    CreatedUnvalidated,
    WaitingValidate,
    ValidatedReadOnly,
    CreatedButNotVisible,
    NeedSceneRegistration,
    Invalid,
    PositionWriteSucceeded,
    Hidden,
    LeakedUnmanaged,
}

public enum StandaloneAttachState
{
    Unknown,
    LinkedAndContained,
    LinkedButNotContained,
    Detached,
    Invalid,
}

public sealed class StandaloneObjectInstance
{
    public string Id { get; set; } = string.Empty;

    public string ObjectAddress { get; set; } = string.Empty;

    public string ModelPath { get; set; } = string.Empty;

    public string PoolName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public int CreatedFrame { get; set; }

    public StandaloneObjectState State { get; set; } = StandaloneObjectState.CreatedUnvalidated;

    public int ValidateFrameWait { get; set; } = 6;

    public int ValidateAttempts { get; set; }

    public int StableReadFrames { get; set; }

    public int MaxValidateAttempts { get; set; } = 60;

    public bool CanWritePosition
        => this.State is StandaloneObjectState.ValidatedReadOnly or StandaloneObjectState.PositionWriteSucceeded;

    public bool CanWriteRotationScale
        => this.State == StandaloneObjectState.PositionWriteSucceeded;

    public Vector3 Position { get; set; }

    public Vector3 RotationEuler { get; set; }

    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    public Vector3 Scale { get; set; } = Vector3.One;

    public bool IsVisible { get; set; }

    public bool IsValid { get; set; } = true;

    public bool OwnedByPlugin { get; set; } = true;

    public bool ManualVisibleConfirmed { get; set; }

    public bool ManualOriginalMapUnaffectedConfirmed { get; set; }

    public bool ManualHiddenConfirmed { get; set; }

    public string ModelResourceHandleAddress { get; set; } = "0x0";

    public string ModelResourcePathReadback { get; set; } = string.Empty;

    public string LoadStateReadback { get; set; } = string.Empty;

    public string TransformReadback { get; set; } = string.Empty;

    public string SceneLinkReadback { get; set; } = string.Empty;

    public StandaloneAttachState AttachState { get; set; } = StandaloneAttachState.Unknown;

    public int ParentChildScanCount { get; set; }

    public bool ParentChildScanHit { get; set; }

    public bool ParentChildScanTruncated { get; set; }

    public float ParentChildScanElapsedMs { get; set; }

    public string ParentChildScanStatus { get; set; } = string.Empty;

    public string FullParentChildScanDump { get; set; } = string.Empty;

    public string VTableReadback { get; set; } = "0x0";

    public string LastDump { get; set; } = string.Empty;

    public string RawObjectLayoutDump { get; set; } = string.Empty;

    public string ValidationStatus { get; set; } = string.Empty;

    public string ActivationStep { get; set; } = string.Empty;

    public string ActivationResult { get; set; } = string.Empty;

    public string ActivationException { get; set; } = string.Empty;

    public string BoundsReadback { get; set; } = string.Empty;

    public string SceneAttachStep { get; set; } = string.Empty;

    public string SceneAttachResult { get; set; } = string.Empty;

    public string SceneAttachException { get; set; } = string.Empty;

    public bool ManuallyVisible { get; set; }

    public bool ManuallyStillInvisible { get; set; }

    public bool ManuallyModelAbnormal { get; set; }

    public bool ManuallyGameUnstable { get; set; }

    public string LastOperation { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;
}
