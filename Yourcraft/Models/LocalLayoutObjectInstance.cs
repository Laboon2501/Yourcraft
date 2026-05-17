using System.Numerics;
using System.Text.Json.Serialization;

namespace Yourcraft.Models;

public sealed class LocalLayoutObjectInstance
{
    public string Id { get; set; } = string.Empty;

    public string InstanceId
    {
        get => this.Id;
        set => this.Id = value;
    }

    public string TemplateSourceSlotAddress { get; set; } = string.Empty;

    public string TemplateSlotAddress
    {
        get => this.TemplateSourceSlotAddress;
        set => this.TemplateSourceSlotAddress = value;
    }

    public string TemplateResourcePath { get; set; } = string.Empty;

    public string TemplateTransform { get; set; } = string.Empty;

    public string SourceResourcePath { get; set; } = string.Empty;

    public string SourceKind { get; set; } = "LoadedLayout";

    public string SourceSharedGroupPath { get; set; } = string.Empty;

    public string SourceParentAddress { get; set; } = string.Empty;

    public string SourceParentKey { get; set; } = string.Empty;

    public string SourceStableKey { get; set; } = string.Empty;

    public int SourceChildIndex { get; set; } = -1;

    public string OriginalResourcePath { get; set; } = string.Empty;

    public string OriginalSlotResourcePath
    {
        get => this.OriginalResourcePath;
        set => this.OriginalResourcePath = value;
    }

    public string CurrentResourcePath { get; set; } = string.Empty;

    public string CurrentModelPath
    {
        get => this.CurrentResourcePath;
        set => this.CurrentResourcePath = value;
    }

    public string CustomModelPath { get; set; } = string.Empty;

    public bool ModelOverrideApplied { get; set; }

    public string OriginalModelResourcePath { get; set; } = string.Empty;

    public string OccupiedSlotAddress { get; set; } = string.Empty;

    public LocalLayoutTransformMode TransformMode { get; set; } = LocalLayoutTransformMode.VisualOnly;

    public LocalLayoutTransformMode CollisionMode
    {
        get => this.TransformMode;
        set => this.TransformMode = value;
    }

    public bool CollisionEnabled
    {
        get => this.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision;
        set => this.TransformMode = value ? LocalLayoutTransformMode.FullLayoutWithCollision : LocalLayoutTransformMode.VisualOnly;
    }

    public string GraphicsObjectAddress { get; set; } = string.Empty;

    public int VisualTransformOffset { get; set; } = 0x20;

    public Vector3 OccupiedSlotOriginalPosition { get; set; }

    public Quaternion OccupiedSlotOriginalRotation { get; set; } = Quaternion.Identity;

    public Vector3 OccupiedSlotOriginalScale { get; set; } = Vector3.One;

    public Vector3 CurrentPosition { get; set; }

    public Quaternion CurrentRotation { get; set; } = Quaternion.Identity;

    public Vector3 CurrentRotationEuler { get; set; }

    public Vector3 CurrentScale { get; set; } = Vector3.One;

    public string OriginalVisualTransform { get; set; } = "未读取";

    public string OriginalLayoutTransform { get; set; } = "未读取";

    public Vector3 OriginalVisualTranslation { get; set; }

    public Vector3 OriginalVisualPosition { get; set; }

    public Quaternion OriginalVisualRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalVisualScale { get; set; } = Vector3.One;

    public Vector3 OriginalLayoutPosition { get; set; }

    public Quaternion OriginalLayoutRotation { get; set; } = Quaternion.Identity;

    public Vector3 OriginalLayoutScale { get; set; } = Vector3.One;

    public Vector3 CurrentVisualTranslation { get; set; }

    public Matrix4x4 OriginalVisualMatrix { get; set; } = Matrix4x4.Identity;

    public Matrix4x4 CurrentVisualMatrix { get; set; } = Matrix4x4.Identity;

    public bool VisualOnlyVerified { get; set; }

    public bool Visible { get; set; }

    public bool OriginalVisible { get; set; } = true;

    public string CarrierRejectReason { get; set; } = string.Empty;

    public string CarrierWarningReason { get; set; } = string.Empty;

    public bool IsOccupied { get; set; }

    public bool IsRestored { get; set; }

    public bool IsDuplicate { get; set; }

    public bool IsInvalid { get; set; }

    public bool IsRenderInvalid { get; set; }

    public bool IsRestoring { get; set; }

    public string InstanceState { get; set; } = "Ready";

    public string LastOperation { get; set; } = string.Empty;

    public OriginalSlotSnapshot? OriginalSlotSnapshot { get; set; }

    public string RestoreDebugInfo { get; set; } = string.Empty;

    public bool ModelExperimentFailed { get; set; }

    public string TransformWriteDisabledReason { get; set; } = string.Empty;

    public string LastTransformWriteSkippedReason { get; set; } = string.Empty;

    public bool ControlledByRuntime { get; set; }

    public bool TransformMonitorActive { get; set; }

    public int TransformMonitorFrame { get; set; }

    public Vector3 TransformMonitorExpectedPosition { get; set; }

    public Vector3 TransformMonitorExpectedRotationEuler { get; set; }

    public Vector3 TransformMonitorExpectedScale { get; set; } = Vector3.One;

    public string AppliedTransformPosition { get; set; } = string.Empty;

    public string TransformReadbackImmediate { get; set; } = string.Empty;

    public string TransformReadbackAfter1Frame { get; set; } = string.Empty;

    public string TransformReadbackAfter5Frames { get; set; } = string.Empty;

    public string TransformReadbackAfter30Frames { get; set; } = string.Empty;

    public string TransformOverwriteDetails { get; set; } = string.Empty;

    public bool PinTransformEnabled { get; set; }

    public bool PinTransformAutoEnabled { get; set; } = true;

    public bool PinFailed { get; set; }

    public int PinWriteFailedCount { get; set; }

    public Vector3 PinTargetPosition { get; set; }

    public Vector3 PinTargetRotationEuler { get; set; }

    public Vector3 PinTargetScale { get; set; } = Vector3.One;

    public string LastPinWriteResult { get; set; } = string.Empty;

    public string PinTransformReason { get; set; } = string.Empty;

    public string AnimationSourceBgPart { get; set; } = string.Empty;

    public string AnimationSourceResourcePath { get; set; } = string.Empty;

    public string AnimationGroupId { get; set; } = string.Empty;

    public int AnimationGroupChildIndex { get; set; } = -1;

    public bool AnimationPlaybackEnabled { get; set; }

    public AnimationPlaybackMode AnimationPlaybackMode { get; set; } = AnimationPlaybackMode.None;

    public Vector3 AnimationSourceBasePosition { get; set; }

    public Quaternion AnimationSourceBaseRotation { get; set; } = Quaternion.Identity;

    public Vector3 AnimationSourceBaseScale { get; set; } = Vector3.One;

    public Vector3 LocalPlaybackBasePosition { get; set; }

    public Quaternion LocalPlaybackBaseRotation { get; set; } = Quaternion.Identity;

    public Vector3 LocalPlaybackBaseScale { get; set; } = Vector3.One;

    public string AnimationSourceBaseTransform { get; set; } = string.Empty;

    public string LocalPlaybackBaseTransform { get; set; } = string.Empty;

    public string CurrentDelta { get; set; } = string.Empty;

    public string AnimationSourceRotation { get; set; } = string.Empty;

    public string AnimationRotationDelta { get; set; } = string.Empty;

    public string AnimationLocalTargetRotation { get; set; } = string.Empty;

    public string AnimationReadbackRotation { get; set; } = string.Empty;

    public string LastSampleTime { get; set; } = string.Empty;

    public int PlaybackFrameCount { get; set; }

    public string AnimationPlaybackLastResult { get; set; } = string.Empty;

    public int AnimationPlaybackFailedCount { get; set; }

    public bool HasCollisionMoved { get; set; }

    public bool CanRestore { get; set; }

    public string LastReadback { get; set; } = "未读取";

    public string LastError { get; set; } = string.Empty;

    public string LastModelOverrideResult { get; set; } = string.Empty;

    public string LastModelOverrideError { get; set; } = string.Empty;

    public string ApplyMdlStatus { get; set; } = "未应用";

    public string ApplyMdlError { get; set; } = string.Empty;

    public string ModelApplyStatus { get; set; } = string.Empty;

    public string ComplexModelRisk { get; set; } = string.Empty;

    public string ComplexModelRiskReason { get; set; } = string.Empty;

    public bool PendingRecreate { get; set; }

    public bool PendingVisualTransform { get; set; }

    public int PendingVisualTransformFrameWait { get; set; }

    public int PendingRecreateStabilizeAttempts { get; set; }

    public int PendingRecreateStabilizeMaxAttempts { get; set; } = 60;

    public string PendingStableGraphicsObjectAddress { get; set; } = string.Empty;

    public int PendingStableFrameCount { get; set; }

    public int PendingStableRequiredFrames { get; set; } = 3;

    public string PendingVisualTransformResult { get; set; } = string.Empty;

    public string GraphicsSafetyDump { get; set; } = string.Empty;

    public string AnimationCapabilityDump { get; set; } = string.Empty;

    public string RecreateAfterCachedMatrices { get; set; } = string.Empty;

    public string RecreateAfterStainOrBgChangeData { get; set; } = string.Empty;

    public string RecreateAfterCachedTransform { get; set; } = string.Empty;

    public string RecreateAfterAnimationData { get; set; } = string.Empty;

    public string RestoreStatus { get; set; } = string.Empty;

    public string RestoreStep { get; set; } = string.Empty;

    public string RestoreError { get; set; } = string.Empty;

    public string AfterRestorePath { get; set; } = string.Empty;

    public string AfterRestorePosition { get; set; } = string.Empty;

    public string AfterRestoreVisible { get; set; } = string.Empty;

    public string BeforeModelPath { get; set; } = string.Empty;

    public string TargetModelPath { get; set; } = string.Empty;

    public string AfterModelPath { get; set; } = string.Empty;

    public string ModelResourceHandleAddress { get; set; } = string.Empty;

    public string OriginalModelResourceHandleAddress { get; set; } = string.Empty;

    public string SetModelReturnValue { get; set; } = string.Empty;

    public string LastSetModelException { get; set; } = string.Empty;

    public string ModelVisibilityReadback { get; set; } = string.Empty;

    public string ModelTransformReadback { get; set; } = string.Empty;

    public string ModelResourceHandleDump { get; set; } = string.Empty;

    public string BeforeModelResourceHandleDump { get; set; } = string.Empty;

    public string AfterModelResourceHandleDump { get; set; } = string.Empty;

    public string ModelPointerDiff { get; set; } = string.Empty;

    public string ModelBoundsReadback { get; set; } = string.Empty;

    public string ModelResourceHandleVTable { get; set; } = string.Empty;

    public string ModelResourceHandleType { get; set; } = string.Empty;

    public string ModelResourceHandleFileType { get; set; } = string.Empty;

    public string ModelResourceHandleLoadState { get; set; } = string.Empty;

    public string ModelResourceHandleId { get; set; } = string.Empty;

    public string ModelResourceCategoryReadback { get; set; } = string.Empty;

    public string ModelResourceCategoryGuess { get; set; } = string.Empty;

    public string ModelResourceCategoryConfidence { get; set; } = string.Empty;

    public string SetModelSignatureReadback { get; set; } = string.Empty;

    public string ManualVisualConfirmation { get; set; } = string.Empty;

    public string RecreateSnapshotGraphicsObject { get; set; } = string.Empty;

    public int RecreateSnapshotIndexInPool { get; set; } = -1;

    public string RecreateSnapshotTransform { get; set; } = string.Empty;

    public string RecreateSnapshotTransformMode { get; set; } = string.Empty;

    public string RecreateSnapshotColliderAddress { get; set; } = string.Empty;

    public string RecreateSnapshotOriginalPath { get; set; } = string.Empty;

    public string RecreateSnapshotTargetPath { get; set; } = string.Empty;

    public string RecreateSnapshotModelResourceHandle { get; set; } = string.Empty;

    public string RecreatePinnedPathAddress { get; set; } = string.Empty;

    public string RecreatePathPointerAddress { get; set; } = string.Empty;

    public string RecreateAfterGraphicsObject { get; set; } = string.Empty;

    public string RecreateAfterModelResourceHandle { get; set; } = string.Empty;

    public string RecreateAfterVisible { get; set; } = string.Empty;

    public string RecreateAfterTransform { get; set; } = string.Empty;

    public string RecreateAfterColliderAddress { get; set; } = string.Empty;

    public string RecreateLayoutRestoreResult { get; set; } = string.Empty;

    public string RecreateVisualReapplyResult { get; set; } = string.Empty;

    public string RecreateCollisionModeResult { get; set; } = string.Empty;

    public string RecreateLastResult { get; set; } = string.Empty;

    public string RecreateLastError { get; set; } = string.Empty;

    public string RecreateManualConfirmation { get; set; } = string.Empty;

    [JsonIgnore]
    public byte[] RecreateTargetPathBuffer { get; set; } = [];

    public string CollisionSourceBgPartAddress { get; set; } = string.Empty;

    public string CollisionSourceResourcePath { get; set; } = string.Empty;

    public string CollisionSourceColliderType { get; set; } = string.Empty;

    public uint CollisionSourceMeshPathCrc { get; set; }

    public uint CollisionSourceAnalyticShapeDataCrc { get; set; }

    public uint CollisionSourceMaterialIdLow { get; set; }

    public uint CollisionSourceMaterialMaskLow { get; set; }

    public uint CollisionSourceMaterialIdHigh { get; set; }

    public uint CollisionSourceMaterialMaskHigh { get; set; }

    public string CollisionSourceSecondaryPath { get; set; } = string.Empty;

    public uint CollisionSnapshotMeshPathCrc { get; set; }

    public uint CollisionSnapshotAnalyticShapeDataCrc { get; set; }

    public uint CollisionSnapshotMaterialIdLow { get; set; }

    public uint CollisionSnapshotMaterialMaskLow { get; set; }

    public uint CollisionSnapshotMaterialIdHigh { get; set; }

    public uint CollisionSnapshotMaterialMaskHigh { get; set; }

    public string CollisionSnapshotColliderAddress { get; set; } = string.Empty;

    public string CollisionSnapshotColliderType { get; set; } = string.Empty;

    public string CollisionSnapshotSecondaryPath { get; set; } = string.Empty;

    public string CollisionAfterColliderAddress { get; set; } = string.Empty;

    public uint CollisionAfterMeshPathCrc { get; set; }

    public uint CollisionAfterAnalyticShapeDataCrc { get; set; }

    public string CollisionAfterColliderType { get; set; } = string.Empty;

    public string CollisionAfterSecondaryPath { get; set; } = string.Empty;

    public bool CollisionApplied { get; set; }

    public string CollisionError { get; set; } = string.Empty;

    public string CollisionSourceResolveResult { get; set; } = string.Empty;

    public string CollisionExperimentLastResult { get; set; } = string.Empty;

    public string CollisionExperimentLastError { get; set; } = string.Empty;

    public string CollisionExperimentManualConfirmation { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}
