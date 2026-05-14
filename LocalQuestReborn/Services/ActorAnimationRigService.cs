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

    public bool IsSupported => false;

    public string UnsupportedReason => "当前版本尚未找到安全的 animation-only data path override。为了避免改变外观，已禁用直接应用。";

    public bool ApplyAnimationRigOverride(RuntimeActorInstance actor, out string reason)
    {
        if (actor.AnimationRigMode == ActorAnimationRigMode.Current || actor.AnimationRigPreset == ActorAnimationRigPreset.Current)
            return this.RestoreAnimationRig(actor, out reason);

        actor.HasAnimationRigNativeOverride = false;
        actor.AnimationRigStatus = $"Unsupported: {this.UnsupportedReason} Rig={actor.AnimationRigPreset} 已保存，但未应用到 native 动画系统。";
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
