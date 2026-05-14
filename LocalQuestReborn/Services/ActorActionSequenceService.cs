using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class ActorActionSequenceService
{
    private const float DefaultStepDurationSeconds = 3f;
    private const float MaxDeltaSeconds = 0.25f;

    private readonly ActorAnimationService animationService;
    private readonly ActorBubbleService bubbleService;
    private readonly Dictionary<string, ActorActionSequenceRuntime> runtimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastUpdateAt = DateTime.UtcNow;

    public ActorActionSequenceService(ActorAnimationService animationService, ActorBubbleService bubbleService)
    {
        this.animationService = animationService;
        this.bubbleService = bubbleService;
    }

    public void Update(IEnumerable<RuntimeActorInstance> actors)
    {
        var actorList = actors.ToList();
        var now = DateTime.UtcNow;
        var delta = Math.Clamp((float)(now - this.lastUpdateAt).TotalSeconds, 0f, MaxDeltaSeconds);
        this.lastUpdateAt = now;

        foreach (var actor in actorList)
        {
            if (!actor.EnableActionSequence || actor.ActionSequence.Count == 0)
            {
                this.StopRuntime(actor.RuntimeId, clearBubble: false);
                continue;
            }

            if (!actor.IsValid || actor.CharacterObject == null)
            {
                this.StopRuntime(actor.RuntimeId, clearBubble: true);
                actor.ActionSequenceStatus = "Actor invalid; sequence paused.";
                continue;
            }

            this.Advance(actor, delta);
        }

        var validIds = actorList.Select(actor => actor.RuntimeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var runtimeId in this.runtimes.Keys.Where(id => !validIds.Contains(id)).ToArray())
            this.StopRuntime(runtimeId, clearBubble: true);

        this.bubbleService.Update(actorList);
    }

    public void Reset(RuntimeActorInstance actor)
    {
        this.runtimes.Remove(actor.RuntimeId);
        this.bubbleService.Clear(actor.RuntimeId);
        actor.ActionSequenceStatus = "Sequence reset.";
        actor.LastActionSequenceError = string.Empty;
    }

    public void Stop(RuntimeActorInstance actor)
        => this.StopRuntime(actor.RuntimeId, clearBubble: true);

    public void StopAll()
    {
        this.runtimes.Clear();
        this.bubbleService.ClearAll();
    }

    public bool TestStep(RuntimeActorInstance actor, ActorActionSequenceStep step, out string reason)
    {
        if (!actor.IsValid || actor.CharacterObject == null)
        {
            reason = "Actor is invalid.";
            actor.LastActionSequenceError = reason;
            return false;
        }

        var success = this.EnterStep(actor, step, testOnly: true, out reason);
        actor.ActionSequenceStatus = success ? $"Tested step: {step.Name}" : "Step test failed.";
        return success;
    }

    private void Advance(RuntimeActorInstance actor, float delta)
    {
        var runtime = this.GetRuntime(actor);
        runtime.Running = true;

        if (runtime.CurrentStepIndex >= actor.ActionSequence.Count)
        {
            if (!actor.ActionSequenceLoop)
            {
                runtime.CurrentStepIndex = Math.Max(0, actor.ActionSequence.Count - 1);
                runtime.Running = false;
                actor.ActionSequenceStatus = "Sequence finished.";
                return;
            }

            runtime.LoopDelayElapsed += delta;
            if (runtime.LoopDelayElapsed < Math.Max(0f, actor.ActionSequenceLoopDelay))
            {
                actor.ActionSequenceStatus = $"Loop delay {runtime.LoopDelayElapsed:F1}/{actor.ActionSequenceLoopDelay:F1}s";
                return;
            }

            runtime.CurrentStepIndex = 0;
            runtime.CurrentStepElapsed = 0f;
            runtime.StepEntered = false;
            runtime.BubbleShown = false;
            runtime.LoopDelayElapsed = 0f;
            runtime.Generation++;
        }

        var step = actor.ActionSequence[runtime.CurrentStepIndex];
        if (!runtime.StepEntered)
        {
            if (!this.EnterStep(actor, step, testOnly: false, out var reason))
            {
                actor.LastActionSequenceError = reason;
                actor.ActionSequenceStatus = $"Step {runtime.CurrentStepIndex + 1} failed: {reason}";
                runtime.CurrentStepIndex++;
                runtime.CurrentStepElapsed = 0f;
                runtime.StepEntered = false;
                runtime.BubbleShown = false;
                return;
            }

            runtime.StepEntered = true;
            runtime.BubbleShown = step.ShowBubbleOnEnter && !string.IsNullOrWhiteSpace(step.BubbleText);
        }

        runtime.CurrentStepElapsed += delta;
        actor.ActionSequenceStatus = $"Step {runtime.CurrentStepIndex + 1}/{actor.ActionSequence.Count}: {step.Name} ({runtime.CurrentStepElapsed:F1}/{StepDuration(step):F1}s)";
        if (runtime.CurrentStepElapsed < StepDuration(step))
            return;

        this.ExitStep(actor, step);
        runtime.CurrentStepIndex++;
        runtime.CurrentStepElapsed = 0f;
        runtime.StepEntered = false;
        runtime.BubbleShown = false;
    }

    private bool EnterStep(RuntimeActorInstance actor, ActorActionSequenceStep step, bool testOnly, out string reason)
    {
        reason = string.Empty;
        switch (step.Kind)
        {
            case ActorActionStepKind.Emote:
                if (step.EmoteId == 0)
                {
                    reason = "EmoteId is 0.";
                    return false;
                }

                if (!this.animationService.PlayTransientTimeline(actor, step.EmoteId, out reason))
                    return false;
                break;
            case ActorActionStepKind.Timeline:
                if (step.TimelineId == 0)
                {
                    reason = "TimelineId is 0.";
                    return false;
                }

                if (!this.animationService.PlayTransientTimeline(actor, step.TimelineId, out reason))
                    return false;
                break;
            case ActorActionStepKind.ResetToDefault:
                if (actor.DefaultAnimationId > 0)
                {
                    if (!this.animationService.Play(actor, actor.DefaultAnimationId, out reason))
                        return false;
                }
                else if (!this.animationService.Stop(actor, out reason))
                {
                    return false;
                }
                break;
            case ActorActionStepKind.Idle:
                if (!this.animationService.Stop(actor, out reason))
                    return false;
                break;
            case ActorActionStepKind.Wait:
                break;
            default:
                reason = $"Unknown action kind: {step.Kind}";
                return false;
        }

        if (step.ShowBubbleOnEnter && !string.IsNullOrWhiteSpace(step.BubbleText))
            this.bubbleService.Show(actor, step.BubbleText, step.BubbleDurationSeconds);

        actor.LastActionSequenceError = string.Empty;
        if (testOnly)
            actor.ActionSequenceStatus = $"Tested step: {step.Name}";
        return true;
    }

    private void ExitStep(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        if (step.StayInPose)
            return;

        if (step.Kind is ActorActionStepKind.Emote or ActorActionStepKind.Timeline)
        {
            if (actor.DefaultAnimationId > 0)
                this.animationService.Play(actor, actor.DefaultAnimationId, out _);
            else
                this.animationService.Stop(actor, out _);
        }
    }

    private ActorActionSequenceRuntime GetRuntime(RuntimeActorInstance actor)
    {
        if (this.runtimes.TryGetValue(actor.RuntimeId, out var runtime))
            return runtime;

        runtime = new ActorActionSequenceRuntime
        {
            ActorInstanceId = actor.RuntimeId,
            Running = true,
        };
        this.runtimes[actor.RuntimeId] = runtime;
        return runtime;
    }

    private void StopRuntime(string runtimeId, bool clearBubble)
    {
        this.runtimes.Remove(runtimeId);
        if (clearBubble)
            this.bubbleService.Clear(runtimeId);
    }

    private static float StepDuration(ActorActionSequenceStep step)
        => step.DurationSeconds > 0f ? step.DurationSeconds : DefaultStepDurationSeconds;
}
