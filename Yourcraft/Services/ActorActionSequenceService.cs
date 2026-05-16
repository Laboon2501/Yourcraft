using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class ActorActionSequenceService
{
    private const float DefaultStepDurationSeconds = 3f;
    private const float MaxDeltaSeconds = 0.25f;

    private readonly ActorAnimationService animationService;
    private readonly ActorNativeBubbleService bubbleService;
    private readonly BrioCapabilityBridgeService transformBridge;
    private readonly ActorLipSyncPresetService lipSyncPresets;
    private readonly Dictionary<string, ActorActionSequenceRuntime> runtimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastUpdateAt = DateTime.UtcNow;

    public ActorActionSequenceService(ActorAnimationService animationService, ActorNativeBubbleService bubbleService, BrioCapabilityBridgeService transformBridge, ActorLipSyncPresetService lipSyncPresets)
    {
        this.animationService = animationService;
        this.bubbleService = bubbleService;
        this.transformBridge = transformBridge;
        this.lipSyncPresets = lipSyncPresets;
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
        runtime.LipTalkPlayed = false;
        runtime.BubbleShown = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
        runtime.LastLipTalkRepeatAt = 0f;
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
                this.RestoreSequenceOrigin(actor, runtime, "sequence finished");
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
            this.RestoreSequenceOrigin(actor, runtime, "loop restart");
            runtime.CurrentStepElapsed = 0f;
            runtime.StepEntered = false;
            runtime.BubbleShown = false;
            runtime.ExpressionPlayed = false;
            runtime.LipTalkPlayed = false;
            runtime.LastAnimationRepeatAt = 0f;
            runtime.LastExpressionRepeatAt = 0f;
            runtime.LastLipTalkRepeatAt = 0f;
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
        runtime.LipTalkPlayed = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
        runtime.LastLipTalkRepeatAt = 0f;
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
            case ActorActionStepKind.Move:
                this.EnsureSequenceOrigin(actor, runtime);
                runtime.IsSequenceMoving = true;
                if (step.MoveUseAbsoluteWorldTarget)
                {
                    runtime.MoveStartWorldPosition = runtime.SequenceOriginTransform.WorldPosition + runtime.CurrentSequenceMoveOffset;
                    runtime.MoveEndWorldPosition = step.MoveWorldTarget;
                }
                else
                {
                    runtime.MoveStartWorldPosition = runtime.SequenceOriginTransform.WorldPosition + step.MoveStartWorldOffset;
                    runtime.MoveEndWorldPosition = runtime.SequenceOriginTransform.WorldPosition + step.MoveEndWorldOffset;
                }

                if (step.PlayMoveAnimationOnEnter && step.MoveAnimationId != 0 && actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
                    this.animationService.PlayTransientTimeline(actor, step.MoveAnimationId, out _);
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

        if (step.Kind == ActorActionStepKind.Action &&
            step.PlayLipTalkWithAction &&
            step.LipTalkDelaySeconds <= 0f &&
            actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
        {
            runtime.LipTalkPlayed = this.ApplyStepLipTalk(actor, step);
            runtime.LastLipTalkRepeatAt = 0f;
        }

        actor.LastActionSequenceError = string.Empty;
        if (testOnly)
            actor.ActionSequenceStatus = $"Tested step: {step.Name}";
        return true;
    }

    private void UpdateStep(RuntimeActorInstance actor, ActorActionSequenceStep step, ActorActionSequenceRuntime runtime)
    {
        if (step.Kind == ActorActionStepKind.Move)
        {
            this.UpdateMoveStep(actor, step, runtime);
            return;
        }

        if (step.Kind != ActorActionStepKind.Action || actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
            return;

        var animationRepeat = step.RepeatAfterSeconds > 0f ? step.RepeatAfterSeconds : StepDuration(step);
        if (step.LoopAnimation && step.AnimationId != 0 && animationRepeat > 0.05f && runtime.CurrentStepElapsed - runtime.LastAnimationRepeatAt >= animationRepeat)
        {
            this.animationService.PlayTransientTimeline(actor, step.AnimationId, out _);
            runtime.LastAnimationRepeatAt = runtime.CurrentStepElapsed;
        }

        if (step.PlayExpressionWithAction && step.ExpressionId != 0 &&
            !runtime.ExpressionPlayed && runtime.CurrentStepElapsed >= Math.Max(0f, step.ExpressionDelaySeconds))
        {
            runtime.ExpressionPlayed = this.animationService.PlayExpressionTimeline(actor, step.ExpressionId, step.ExpressionLayer, out _);
            runtime.LastExpressionRepeatAt = runtime.CurrentStepElapsed;
            return;
        }

        if (step.PlayExpressionWithAction && step.ExpressionId != 0)
        {
            var expressionRepeat = step.ExpressionDurationSeconds > 0f ? step.ExpressionDurationSeconds : StepDuration(step);
            if (step.LoopExpression && runtime.ExpressionPlayed && expressionRepeat > 0.05f && runtime.CurrentStepElapsed - runtime.LastExpressionRepeatAt >= expressionRepeat)
            {
                this.animationService.PlayExpressionTimeline(actor, step.ExpressionId, step.ExpressionLayer, out _);
                runtime.LastExpressionRepeatAt = runtime.CurrentStepElapsed;
            }
        }

        if (!step.PlayLipTalkWithAction)
            return;

        if (!runtime.LipTalkPlayed && runtime.CurrentStepElapsed >= Math.Max(0f, step.LipTalkDelaySeconds))
        {
            runtime.LipTalkPlayed = this.ApplyStepLipTalk(actor, step);
            runtime.LastLipTalkRepeatAt = runtime.CurrentStepElapsed;
            return;
        }

        var lipRepeat = step.LipTalkDurationSeconds > 0f ? step.LipTalkDurationSeconds : StepDuration(step);
        if (step.LoopLipTalk && runtime.LipTalkPlayed && lipRepeat > 0.05f && runtime.CurrentStepElapsed - runtime.LastLipTalkRepeatAt >= lipRepeat)
        {
            this.ApplyStepLipTalk(actor, step);
            runtime.LastLipTalkRepeatAt = runtime.CurrentStepElapsed;
        }
    }

    private void ExitStep(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        if (!step.AllowLookAtDuringStep && actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden)
            actor.LookAtPausedByActionSequence = false;

        if (step.Kind == ActorActionStepKind.Move)
        {
            if (step.MoveRestoreAtStepEnd)
                this.RestoreSequenceOrigin(actor, this.GetRuntime(actor), "move step end");
            else
                this.GetRuntime(actor).IsSequenceMoving = false;
            return;
        }

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
        var hadRuntime = this.runtimes.TryGetValue(actor.RuntimeId, out var runtime);
        if (clearBubble)
            this.bubbleService.Clear(actor);

        actor.LookAtPausedByActionSequence = false;
        if (hadRuntime && runtime != null)
            this.RestoreSequenceOrigin(actor, runtime, "sequence stop");
        this.runtimes.Remove(actor.RuntimeId);
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

    private void EnsureSequenceOrigin(RuntimeActorInstance actor, ActorActionSequenceRuntime runtime)
    {
        if (runtime.HasSequenceOrigin)
            return;

        var rotation = ActorTransformUtil.NormalizeRotation(actor.TransformEditRotationEuler);
        var scale = ActorTransformUtil.NormalizeScale(actor.TransformEditScale == Vector3.Zero ? Vector3.One : actor.TransformEditScale);
        runtime.SequenceOriginTransform = WorldTransform.FromEuler(actor.TransformEditPosition, rotation, scale);
        runtime.HasSequenceOrigin = true;
    }

    private void UpdateMoveStep(RuntimeActorInstance actor, ActorActionSequenceStep step, ActorActionSequenceRuntime runtime)
    {
        this.EnsureSequenceOrigin(actor, runtime);
        if (actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
            return;

        var duration = Math.Max(0.01f, MoveDuration(step));
        var t = Math.Clamp(runtime.CurrentStepElapsed / duration, 0f, 1f);
        if (step.MoveInterpolation == ActorMoveInterpolation.SmoothStep)
            t = t * t * (3f - 2f * t);

        var position = Vector3.Lerp(runtime.MoveStartWorldPosition, runtime.MoveEndWorldPosition, t);
        runtime.CurrentSequenceMoveOffset = position - runtime.SequenceOriginTransform.WorldPosition;
        var rotationEuler = runtime.SequenceOriginTransform.WorldEulerRadians;
        if (step.MoveAffectsRotation)
            rotationEuler = new Vector3(0f, DegreesToRadians(step.MoveYawDegrees), 0f);
        else if (step.MoveFaceDirection && !actor.LookAtPlayerEnabled)
        {
            var direction = runtime.MoveEndWorldPosition - runtime.MoveStartWorldPosition;
            if (direction.LengthSquared() > 0.0001f)
                rotationEuler = new Vector3(0f, MathF.Atan2(direction.X, direction.Z), 0f);
        }

        this.ApplyRuntimeSequenceTransform(actor, position, rotationEuler, runtime.SequenceOriginTransform.WorldScale, out var reason);
        actor.ActionSequenceStatus = $"Move runtime world position={position}; {reason}";
    }

    private void RestoreSequenceOrigin(RuntimeActorInstance actor, ActorActionSequenceRuntime runtime, string reason)
    {
        if (!runtime.HasSequenceOrigin)
            return;

        this.ApplyRuntimeSequenceTransform(
            actor,
            runtime.SequenceOriginTransform.WorldPosition,
            runtime.SequenceOriginTransform.WorldEulerRadians,
            runtime.SequenceOriginTransform.WorldScale,
            out _);
        runtime.CurrentSequenceMoveOffset = Vector3.Zero;
        runtime.IsSequenceMoving = false;
        actor.ActionSequenceStatus = $"Sequence origin restored: {reason}";
    }

    private bool ApplyRuntimeSequenceTransform(RuntimeActorInstance actor, Vector3 position, Vector3 rotationEuler, Vector3 scale, out string reason)
    {
        var configPosition = actor.TransformEditPosition;
        var configRotation = ActorTransformUtil.NormalizeRotation(actor.TransformEditRotationEuler);
        var configScale = ActorTransformUtil.NormalizeScale(actor.TransformEditScale == Vector3.Zero ? Vector3.One : actor.TransformEditScale);
        var spawnPosition = actor.SpawnPosition;
        var spawnRotation = ActorTransformUtil.NormalizeRotation(actor.SpawnRotationEuler);
        var spawnScale = ActorTransformUtil.NormalizeScale(actor.SpawnScale);
        var hasSaved = actor.HasSavedTransform;

        var success = this.transformBridge.TryApplyModelTransform(actor, position, rotationEuler, scale, out reason);

        actor.TransformEditPosition = configPosition;
        actor.TransformEditRotationEuler = configRotation;
        actor.TransformEditScale = configScale;
        actor.SpawnPosition = spawnPosition;
        actor.SpawnRotationEuler = spawnRotation;
        actor.SpawnScale = spawnScale;
        actor.HasSavedTransform = hasSaved;
        return success;
    }

    private static void MoveNext(ActorActionSequenceRuntime runtime)
    {
        runtime.CurrentStepIndex++;
        runtime.CurrentStepElapsed = 0f;
        runtime.StepEntered = false;
        runtime.BubbleShown = false;
        runtime.ExpressionPlayed = false;
        runtime.LipTalkPlayed = false;
        runtime.LastAnimationRepeatAt = 0f;
        runtime.LastExpressionRepeatAt = 0f;
        runtime.LastLipTalkRepeatAt = 0f;
    }

    private static float StepDuration(ActorActionSequenceStep step)
        => step.Kind == ActorActionStepKind.Move
            ? MoveDuration(step)
            : step.DurationSeconds > 0f ? step.DurationSeconds : DefaultStepDurationSeconds;

    private static float MoveDuration(ActorActionSequenceStep step)
        => step.MoveDurationSeconds > 0f ? step.MoveDurationSeconds : step.DurationSeconds > 0f ? step.DurationSeconds : DefaultStepDurationSeconds;

    private bool ApplyStepLipTalk(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        if (!this.lipSyncPresets.TryResolveTimelineId(step.LipTalkKey, step.LipTalkId, out var timelineId, out var entry, out var resolveReason))
        {
            actor.LastLipTalkError = resolveReason;
            actor.LastLipTalkResult = string.Empty;
            return false;
        }

        step.LipTalkKey = entry.InternalKey;
        step.LipTalkId = (ushort)Math.Min(timelineId, ushort.MaxValue);
        var success = this.animationService.ApplyLipTalk(actor, timelineId, out var reason);
        actor.LastLipTalkResult = success ? $"{entry.DisplayName}: {reason}" : string.Empty;
        actor.LastLipTalkError = success ? string.Empty : reason;
        return success;
    }

    private static float DegreesToRadians(float degrees)
        => degrees * (MathF.PI / 180f);
}
