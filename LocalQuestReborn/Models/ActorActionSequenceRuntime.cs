namespace LocalQuestReborn.Models;

public sealed class ActorActionSequenceRuntime
{
    public string ActorInstanceId { get; set; } = string.Empty;

    public int CurrentStepIndex { get; set; }

    public float CurrentStepElapsed { get; set; }

    public bool StepEntered { get; set; }

    public bool BubbleShown { get; set; }

    public bool ExpressionPlayed { get; set; }

    public float LastAnimationRepeatAt { get; set; }

    public float LastExpressionRepeatAt { get; set; }

    public float LoopDelayElapsed { get; set; }

    public uint Generation { get; set; }

    public bool Running { get; set; }
}
