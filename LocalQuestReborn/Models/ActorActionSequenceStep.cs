using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class ActorActionSequenceStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Action Step";

    public ActorActionStepKind Kind { get; set; } = ActorActionStepKind.Action;

    public ushort AnimationId { get; set; }

    public bool LoopAnimation { get; set; }

    public bool StayInPose { get; set; }

    public float RepeatAfterSeconds { get; set; }

    public ushort ExpressionId { get; set; }

    public bool PlayExpressionWithAction { get; set; }

    public float ExpressionDelaySeconds { get; set; }

    public float ExpressionDurationSeconds { get; set; }

    public bool LoopExpression { get; set; }

    public float ExpressionWeight { get; set; } = 1f;

    public ActorExpressionLayer ExpressionLayer { get; set; } = ActorExpressionLayer.Facial;

    public float DurationSeconds { get; set; } = 3f;

    public string BubbleText { get; set; } = string.Empty;

    public float BubbleDurationSeconds { get; set; } = 3f;

    public bool BubbleUseAutoDuration { get; set; } = true;

    public bool ShowBubbleOnEnter { get; set; } = true;

    public bool HideBubbleOnDespawn { get; set; } = true;

    public bool AllowLookAtDuringStep { get; set; } = true;

    public Vector3 MoveStartWorldOffset { get; set; }

    public Vector3 MoveEndWorldOffset { get; set; }

    public bool MoveUseAbsoluteWorldTarget { get; set; }

    public Vector3 MoveWorldTarget { get; set; }

    public float MoveDurationSeconds { get; set; } = 3f;

    public ActorMoveInterpolation MoveInterpolation { get; set; } = ActorMoveInterpolation.Linear;

    public bool MoveFaceDirection { get; set; }

    public bool MoveRestoreAtStepEnd { get; set; }

    public bool MoveAffectsRotation { get; set; }

    public float MoveYawDegrees { get; set; }

    public ushort MoveAnimationId { get; set; }

    public bool PlayMoveAnimationOnEnter { get; set; }

    [Obsolete("Use AnimationId.")]
    public ushort EmoteId
    {
        get => this.AnimationId;
        set => this.AnimationId = value;
    }

    [Obsolete("Use AnimationId.")]
    public ushort TimelineId
    {
        get => this.AnimationId;
        set => this.AnimationId = value;
    }

    [Obsolete("Use LoopAnimation.")]
    public bool LoopEmote
    {
        get => this.LoopAnimation;
        set => this.LoopAnimation = value;
    }
}
