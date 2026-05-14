namespace LocalQuestReborn.Models;

public sealed class ActorActionSequenceStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Action Step";

    public ActorActionStepKind Kind { get; set; } = ActorActionStepKind.Emote;

    public ushort EmoteId { get; set; }

    public ushort TimelineId { get; set; }

    public float DurationSeconds { get; set; } = 3f;

    public bool LoopEmote { get; set; }

    public bool StayInPose { get; set; }

    public string BubbleText { get; set; } = string.Empty;

    public float BubbleDurationSeconds { get; set; } = 3f;

    public bool ShowBubbleOnEnter { get; set; } = true;
}
