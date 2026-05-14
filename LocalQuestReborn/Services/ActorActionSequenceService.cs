using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class ActorActionSequenceService
{
    private const float DefaultStepDurationSeconds = 3f;
    private const float MaxDeltaSeconds = 0.25f;

    private readonly ActorAnimationService animationService;
    private readonly ActorNativeBubbleService bubbleService;
    private readonly Dictionary<string, ActorActionSequenceRuntime> runtimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastUpdateAt = DateTime.UtcNow;

    public ActorActionSequenceService(ActorAnimationService animationService, ActorNativeBubbleService bubbleService)
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
                this.StopActorIfNeeded(actor, clearBubble: false, restoreVisibility: true);
                continue;
            }

            if (!actor.PostSpawnBehaviorReady)
            {
                actor.ActionSequenceStatus = $"Waiting post-spawn pipeline: {actor.PostSpawnPipelineState}";
                continue;
            }

            if (!actor.IsValid || actor.CharacterObject == null)
            {
                this.StopActorIfNeeded(actor, clearBubble: true, restoreVisibility: false);
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
        this.StopActorIfNeeded(actor, clearBubble: true, restoreVisibility: true);
        actor.ActionSequenceStatus = "Sequence reset.";
        actor.LastActionSequenceError = string.Empty;
    }

    public void Stop(RuntimeActorInstance actor)
        => this.StopActorIfNeeded(actor, clearBubble: true, restoreVisibility: true);

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

        var runtime = this.GetRuntime(actor);
        runtime.ExpressionPlayed = false;
        runtime.BubbleShown = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
        var success = this.EnterStep(actor, step, runtime, testOnly: true, out reason);
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
            runtime.ExpressionPlayed = false;
            runtime.LastAnimationRepeatAt = 0f;
            runtime.LastExpressionRepeatAt = 0f;
            runtime.LoopDelayElapsed = 0f;
            runtime.Generation++;
        }

        var step = actor.ActionSequence[runtime.CurrentStepIndex];
        if (!runtime.StepEntered)
        {
            if (!this.EnterStep(actor, step, runtime, testOnly: false, out var reason))
            {
                actor.LastActionSequenceError = reason;
                actor.ActionSequenceStatus = $"Step {runtime.CurrentStepIndex + 1} failed: {reason}";
                MoveNext(runtime);
                return;
            }

            runtime.StepEntered = true;
        }

        runtime.CurrentStepElapsed += delta;
        this.UpdateStep(actor, step, runtime);
        actor.ActionSequenceStatus = $"Step {runtime.CurrentStepIndex + 1}/{actor.ActionSequence.Count}: {step.Name} ({runtime.CurrentStepElapsed:F1}/{StepDuration(step):F1}s)";
        if (runtime.CurrentStepElapsed < StepDuration(step))
            return;

        this.ExitStep(actor, step);
        MoveNext(runtime);
    }

    private bool EnterStep(RuntimeActorInstance actor, ActorActionSequenceStep step, ActorActionSequenceRuntime runtime, bool testOnly, out string reason)
    {
        reason = string.Empty;
        runtime.BubbleShown = false;
        runtime.ExpressionPlayed = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
        actor.LookAtPausedByActionSequence = !step.AllowLookAtDuringStep || actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden;

        switch (step.Kind)
        {
            case ActorActionStepKind.Spawn:
                if (!this.animationService.SetSequenceVisibility(actor, true, out reason))
                    return false;
                actor.LookAtPausedByActionSequence = !step.AllowLookAtDuringStep;
                if (actor.DefaultAnimationId > 0)
                    this.animationService.PlayTransientTimeline(actor, actor.DefaultAnimationId, out _);
                break;
            case ActorActionStepKind.Despawn:
                if (step.HideBubbleOnDespawn)
                    this.bubbleService.Clear(actor);
                if (!this.animationService.SetSequenceVisibility(actor, false, out reason))
                    return false;
                actor.LookAtPausedByActionSequence = true;
                break;
            case ActorActionStepKind.Action:
                if (actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
                    break;
                if (step.AnimationId == 0)
                {
                    reason = "AnimationId is 0.";
                    return false;
                }

                if (!this.animationService.PlayTransientTimeline(actor, step.AnimationId, out reason))
                    return false;
                break;
            case ActorActionStepKind.ResetToDefaultAction:
                if (actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
                    break;
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
                if (actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden && !this.animationService.Stop(actor, out reason))
                    return false;
                break;
            case ActorActionStepKind.Wait:
                break;
            default:
                reason = $"Unknown action kind: {step.Kind}";
                return false;
        }

        if (step.ShowBubbleOnEnter &&
            !string.IsNullOrWhiteSpace(step.BubbleText) &&
            actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
        {
            this.bubbleService.Show(actor, step.BubbleText, step.BubbleDurationSeconds, step.BubbleUseAutoDuration);
            runtime.BubbleShown = true;
        }

        if (step.Kind == ActorActionStepKind.Action &&
            step.PlayExpressionWithAction &&
            step.ExpressionId != 0 &&
            step.ExpressionDelaySeconds <= 0f &&
            actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
        {
            runtime.ExpressionPlayed = this.animationService.PlayExpressionTimeline(actor, step.ExpressionId, step.ExpressionLayer, out _);
            runtime.LastExpressionRepeatAt = 0f;
        }

        actor.LastActionSequenceError = string.Empty;
        if (testOnly)
            actor.ActionSequenceStatus = $"Tested step: {step.Name}";
        return true;
    }

    private void UpdateStep(RuntimeActorInstance actor, ActorActionSequenceStep step, ActorActionSequenceRuntime runtime)
    {
        if (step.Kind != ActorActionStepKind.Action || actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
            return;

        var animationRepeat = step.RepeatAfterSeconds > 0f ? step.RepeatAfterSeconds : StepDuration(step);
        if (step.LoopAnimation && step.AnimationId != 0 && animationRepeat > 0.05f && runtime.CurrentStepElapsed - runtime.LastAnimationRepeatAt >= animationRepeat)
        {
            this.animationService.PlayTransientTimeline(actor, step.AnimationId, out _);
            runtime.LastAnimationRepeatAt = runtime.CurrentStepElapsed;
        }

        if (!step.PlayExpressionWithAction || step.ExpressionId == 0)
            return;

        if (!runtime.ExpressionPlayed && runtime.CurrentStepElapsed >= Math.Max(0f, step.ExpressionDelaySeconds))
        {
            runtime.ExpressionPlayed = this.animationService.PlayExpressionTimeline(actor, step.ExpressionId, step.ExpressionLayer, out _);
            runtime.LastExpressionRepeatAt = runtime.CurrentStepElapsed;
            return;
        }

        var expressionRepeat = step.ExpressionDurationSeconds > 0f ? step.ExpressionDurationSeconds : StepDuration(step);
        if (step.LoopExpression && runtime.ExpressionPlayed && expressionRepeat > 0.05f && runtime.CurrentStepElapsed - runtime.LastExpressionRepeatAt >= expressionRepeat)
        {
            this.animationService.PlayExpressionTimeline(actor, step.ExpressionId, step.ExpressionLayer, out _);
            runtime.LastExpressionRepeatAt = runtime.CurrentStepElapsed;
        }
    }

    private void ExitStep(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        if (!step.AllowLookAtDuringStep && actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
            actor.LookAtPausedByActionSequence = false;

        if (step.StayInPose || step.Kind != ActorActionStepKind.Action || actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
            return;

        if (actor.DefaultAnimationId > 0)
            this.animationService.Play(actor, actor.DefaultAnimationId, out _);
        else
            this.animationService.Stop(actor, out _);
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

    private void StopActorIfNeeded(RuntimeActorInstance actor, bool clearBubble, bool restoreVisibility)
    {
        var hadRuntime = this.runtimes.Remove(actor.RuntimeId);
        if (clearBubble)
            this.bubbleService.Clear(actor);

        actor.LookAtPausedByActionSequence = false;
        if (restoreVisibility && actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden && actor.IsValid && actor.CharacterObject != null)
            this.animationService.SetSequenceVisibility(actor, true, out _);

        if (hadRuntime)
            actor.ActionSequenceStatus = "Sequence stopped.";
    }

    private void StopRuntime(string runtimeId, bool clearBubble)
    {
        this.runtimes.Remove(runtimeId);
        if (clearBubble)
            this.bubbleService.Clear(runtimeId);
    }

    private static void MoveNext(ActorActionSequenceRuntime runtime)
    {
        runtime.CurrentStepIndex++;
        runtime.CurrentStepElapsed = 0f;
        runtime.StepEntered = false;
        runtime.BubbleShown = false;
        runtime.ExpressionPlayed = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
    }

    private static float StepDuration(ActorActionSequenceStep step)
        => step.DurationSeconds > 0f ? step.DurationSeconds : DefaultStepDurationSeconds;
}
