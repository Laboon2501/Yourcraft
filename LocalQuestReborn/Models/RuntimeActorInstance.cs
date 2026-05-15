using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class RuntimeActorInstance
{
    public string RuntimeId { get; set; } = Guid.NewGuid().ToString("N");

    public string NpcId { get; set; } = string.Empty;

    public string TemplateNpcId { get; set; } = string.Empty;

    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ushort SpawnedTerritoryType { get; set; }

    public string SpawnedTerritoryName { get; set; } = string.Empty;

    public string ObjectIndex { get; set; } = "不可用";

    public string Address { get; set; } = "不可用";

    public object? CharacterObject { get; set; }

    public string SpawnSource { get; set; } = "VirtualFallback";

    public DateTime SpawnTime { get; set; } = DateTime.Now;

    public int SortOrder { get; set; } = int.MaxValue;

    public long SpawnSequence { get; set; }

    public Vector3 LastKnownPosition { get; set; }

    public Quaternion LastKnownRotation { get; set; } = Quaternion.Identity;

    public Vector3 LastKnownRotationEuler { get; set; }

    public Vector3 LastKnownScale { get; set; } = Vector3.One;

    public Vector3 SpawnPosition { get; set; }

    public Vector3 SpawnRotationEuler { get; set; }

    public Vector3 SpawnScale { get; set; } = Vector3.One;

    public Vector3 TransformEditPosition { get; set; }

    public Vector3 TransformEditRotationEuler { get; set; }

    public Vector3 TransformEditScale { get; set; } = Vector3.One;

    public bool HasSavedTransform { get; set; }

    public string LastTransformReadback { get; set; } = string.Empty;

    public string LastTransformError { get; set; } = string.Empty;

    public string SavedTransformSnapshot { get; set; } = string.Empty;

    public bool IsValid { get; set; }

    public string LastError { get; set; } = string.Empty;

    public string LastMoveMethod { get; set; } = "未移动";

    public string LastMoveError { get; set; } = string.Empty;

    public Vector3 LastMoveBeforePosition { get; set; }

    public Vector3 LastMoveTargetPosition { get; set; }

    public Vector3 LastMoveAfterPosition { get; set; }

    public bool LastMoveDistanceReasonable { get; set; }

    public bool LastMoveActorValidAfter { get; set; }

    public string LastAppearanceMethod { get; set; } = "未应用";

    public string LastAppearanceError { get; set; } = string.Empty;

    public string AppearanceSourceType { get; set; } = string.Empty;

    public string LastAppearanceApplyResult { get; set; } = string.Empty;

    public DateTime? LastAppearanceAppliedAt { get; set; }

    public string LastAppearanceValidationResult { get; set; } = string.Empty;

    public string LastAppearanceVerificationState { get; set; } = string.Empty;

    public string LastAppearanceResidualSlots { get; set; } = string.Empty;

    public int LastAppearanceRedrawFallbackCount { get; set; }

    public string LastAppearanceBeforeSummary { get; set; } = string.Empty;

    public string LastAppearanceAfterSummary { get; set; } = string.Empty;

    public string LastLocalPlayerAppearanceSummary { get; set; } = string.Empty;

    public string LastAppearancePresetSummary { get; set; } = string.Empty;

    public string LastAppearanceClearEquipmentResult { get; set; } = string.Empty;

    public int LastAppearanceRetryCount { get; set; }

    public bool PostSpawnBehaviorReady { get; set; } = true;

    public string PostSpawnPipelineState { get; set; } = "Ready";

    public string PostSpawnPipelineStatus { get; set; } = string.Empty;

    public PenumbraCollectionMode PenumbraMode { get; set; } = PenumbraCollectionMode.DoNotTouch;

    public Guid? PenumbraCollectionId { get; set; }

    public string PenumbraCollectionNameCache { get; set; } = string.Empty;

    public bool WeAppliedPenumbraCollection { get; set; }

    public int LastAppliedPenumbraGameObjectIndex { get; set; } = -1;

    public Guid? LastAppliedPenumbraCollectionId { get; set; }

    public string LastPenumbraCollectionResult { get; set; } = string.Empty;

    public string LastPenumbraCollectionError { get; set; } = string.Empty;

    public string ExpectedName { get; set; } = string.Empty;

    public string CurrentNativeName { get; set; } = "不可用";

    public bool NativeNameSet { get; set; }

    public string DesiredDisplayName { get; set; } = string.Empty;

    public string NativeNameReadback { get; set; } = "不可用";

    public string NameSetResult { get; set; } = string.Empty;

    public string IsTargetableReadback { get; set; } = "未知";

    public string ObjectKindReadback { get; set; } = "未知";

    public string SubKindReadback { get; set; } = "未知";

    public string DataIdReadback { get; set; } = "未知";

    public string EntityIdReadback { get; set; } = "未知";

    public bool CurrentTargetMatched { get; set; }

    public string HoverOrTargetDebugInfo { get; set; } = string.Empty;

    public uint DefaultAnimationId { get; set; }

    public uint CurrentAnimationId { get; set; }

    public bool AnimationEnabled { get; set; }

    public string LastAnimationResult { get; set; } = string.Empty;

    public string LastAnimationError { get; set; } = string.Empty;

    public bool EnableActionSequence { get; set; }

    public bool ActionSequenceLoop { get; set; }

    public float ActionSequenceLoopDelay { get; set; }

    public List<ActorActionSequenceStep> ActionSequence { get; set; } = [];

    public string ActionSequenceStatus { get; set; } = "Stopped";

    public string LastActionSequenceError { get; set; } = string.Empty;

    public ActorVisibilityRuntimeState VisibilityRuntimeState { get; set; } = ActorVisibilityRuntimeState.Visible;

    public bool LookAtPausedByActionSequence { get; set; }

    public ActorAnimationRigMode AnimationRigMode { get; set; } = ActorAnimationRigMode.Current;

    public ActorAnimationRigPreset AnimationRigPreset { get; set; } = ActorAnimationRigPreset.Current;

    public byte CustomRigRace { get; set; }

    public byte CustomRigSex { get; set; }

    public byte CustomRigTribe { get; set; }

    public bool HasAnimationRigNativeOverride { get; set; }

    public byte OriginalRigRace { get; set; }

    public byte OriginalRigSex { get; set; }

    public byte OriginalRigBodyType { get; set; }

    public byte OriginalRigTribe { get; set; }

    public byte AppliedRigRace { get; set; }

    public byte AppliedRigSex { get; set; }

    public byte AppliedRigBodyType { get; set; }

    public byte AppliedRigTribe { get; set; }

    public string AnimationRigStatus { get; set; } = "Current";

    public string AnimationRigDebugReport { get; set; } = string.Empty;

    public string AnimationPathResolverStatus { get; set; } = string.Empty;

    public bool LookAtPlayerEnabled { get; set; }

    public float LookAtRadius { get; set; } = 8f;

    public NpcLookAtMode LookAtMode { get; set; } = NpcLookAtMode.None;

    public bool IsLookingAtPlayer { get; set; }

    public bool LookAtRegistered { get; set; }

    public string LookAtTargetDebug { get; set; } = "none";

    public string LastLookAtError { get; set; } = string.Empty;

    public DateTime LastLookAtUpdateAt { get; set; } = DateTime.MinValue;
}
