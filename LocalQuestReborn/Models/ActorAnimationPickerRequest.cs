namespace LocalQuestReborn.Models;

public sealed class ActorAnimationPickerRequest
{
    public ActorAnimationPickerTargetKind TargetKind { get; init; }

    public ActorAnimationPickerMode PickerMode { get; set; }

    public string Title { get; init; } = "选择 ActionTimeline";

    public string? NpcId { get; init; }

    public string? ActorRuntimeId { get; init; }

    public Guid? StepId { get; init; }

    public static ActorAnimationPickerRequest ForNpcDefault(string npcId, ActorAnimationPickerMode mode)
        => new()
        {
            TargetKind = ActorAnimationPickerTargetKind.NpcDefaultAction,
            PickerMode = mode,
            NpcId = npcId,
            Title = "选择 NPC 默认动画 / ActionTimelineId",
        };

    public static ActorAnimationPickerRequest ForActorCurrent(string runtimeId, ActorAnimationPickerMode mode)
        => new()
        {
            TargetKind = ActorAnimationPickerTargetKind.ActorCurrentAction,
            PickerMode = mode,
            ActorRuntimeId = runtimeId,
            Title = "选择当前 Actor 动画 / ActionTimelineId",
        };

    public static ActorAnimationPickerRequest ForActorExpression(string runtimeId, ActorAnimationPickerMode mode)
        => new()
        {
            TargetKind = ActorAnimationPickerTargetKind.ActorExpression,
            PickerMode = mode,
            ActorRuntimeId = runtimeId,
            Title = "选择当前 Actor 表情 / ExpressionTimelineId",
        };

    public static ActorAnimationPickerRequest ForStepAnimation(string runtimeId, Guid stepId, ActorAnimationPickerMode mode)
        => new()
        {
            TargetKind = ActorAnimationPickerTargetKind.StepAnimation,
            PickerMode = mode,
            ActorRuntimeId = runtimeId,
            StepId = stepId,
            Title = "选择动作步骤动画 / ActionTimelineId",
        };

    public static ActorAnimationPickerRequest ForStepExpression(string runtimeId, Guid stepId, ActorAnimationPickerMode mode)
        => new()
        {
            TargetKind = ActorAnimationPickerTargetKind.StepExpression,
            PickerMode = mode,
            ActorRuntimeId = runtimeId,
            StepId = stepId,
            Title = "选择表情 / ExpressionTimelineId",
        };
}
