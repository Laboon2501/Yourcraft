using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationRigService
{
    private readonly ActorAnimationService animationService;
    private readonly IPluginLog log;

    public ActorAnimationRigService(ActorAnimationService animationService, IPluginLog log)
    {
        this.animationService = animationService;
        this.log = log;
    }

    public bool ApplyAnimationRigOverride(RuntimeActorInstance actor, out string reason)
    {
        if (actor.AnimationRigMode == ActorAnimationRigMode.Current || actor.AnimationRigPreset == ActorAnimationRigPreset.Current)
            return this.RestoreAnimationRig(actor, out reason);

        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigStatus =
            $"Unsupported current build: no verified animation-only data-path override for {actor.AnimationRigPreset}. Rig selection is saved but no native appearance/customize fields were written.";
        this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        reason = $"{actor.AnimationRigStatus} Replay result: {replayReason}";
        this.log.Debug("Animation rig override skipped as unsupported. RuntimeId={RuntimeId}, Rig={Rig}, Replay={Replay}", actor.RuntimeId, actor.AnimationRigPreset, replayReason);
        return false;
    }

    public bool RestoreAnimationRig(RuntimeActorInstance actor, out string reason)
    {
        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigMode = ActorAnimationRigMode.Current;
        actor.AnimationRigPreset = ActorAnimationRigPreset.Current;
        actor.AnimationRigStatus = "Current: using actor's own animation data path. No native rig override active.";
        this.ReapplyCurrentAnimationWithRig(actor, out var replayReason);
        reason = $"{actor.AnimationRigStatus} Replay result: {replayReason}";
        return true;
    }

    public bool ReapplyCurrentAnimationWithRig(RuntimeActorInstance actor, out string reason)
    {
        var animationId = actor.CurrentAnimationId != 0 ? actor.CurrentAnimationId : actor.DefaultAnimationId;
        if (animationId == 0)
        {
            reason = "No current/default animation to replay.";
            return false;
        }

        return this.animationService.PlayTransientTimeline(actor, animationId, out reason);
    }

}
