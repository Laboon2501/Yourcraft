using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class RuntimeActorInstance
{
    public string RuntimeId { get; set; } = Guid.NewGuid().ToString("N");

    public string NpcId { get; set; } = string.Empty;

    public string NpcName { get; set; } = string.Empty;

    public string ObjectIndex { get; set; } = "不可用";

    public string Address { get; set; } = "不可用";

    public object? CharacterObject { get; set; }

    public string SpawnSource { get; set; } = "VirtualFallback";

    public DateTime SpawnTime { get; set; } = DateTime.Now;

    public Vector3 LastKnownPosition { get; set; }

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

    public bool IsLookingAtPlayer { get; set; }

    public string LastLookAtError { get; set; } = string.Empty;

    public DateTime LastLookAtUpdateAt { get; set; } = DateTime.MinValue;
}
