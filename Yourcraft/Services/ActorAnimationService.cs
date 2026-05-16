using Dalamud.Plugin.Services;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class ActorAnimationService
{
    private const int TimelineLipsOverrideOffset = 0x2E8;

    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public ActorAnimationService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public event Action<RuntimeActorInstance, uint, string>? TimelinePlayRequested;

    public bool EnableActorTargetDebugLog { get; set; }

    public bool Play(RuntimeActorInstance actor, uint animationId, out string reason)
        => this.PlayTimeline(actor, animationId, out reason);

    public bool PlayTimeline(RuntimeActorInstance actor, uint animationId, out string reason)
        => this.PlayTimeline(actor, animationId, updateDefaultAnimation: true, out reason);

    public bool PlayTransientTimeline(RuntimeActorInstance actor, uint animationId, out string reason)
        => this.PlayTimeline(actor, animationId, updateDefaultAnimation: false, out reason);

    public bool PlayExpressionTimeline(RuntimeActorInstance actor, uint expressionId, ActorExpressionLayer layer, out string reason)
    {
        if (expressionId == 0 || layer == ActorExpressionLayer.None)
        {
            reason = "ExpressionId is 0 or expression layer is None.";
            return false;
        }

        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false, native expression write skipped.";
            actor.LastAnimationError = reason;
            return false;
        }

        if (!this.TryResolveActorAddress(actor, "ExpressionTimeline", out var address, out reason))
        {
            actor.LastAnimationError = reason;
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                character->Timeline.TimelineSequencer.PlayTimeline((ushort)expressionId);
            }

            actor.LastAnimationError = string.Empty;
            actor.LastAnimationResult = $"Expression timeline played: {expressionId} ({layer})";
            reason = actor.LastAnimationResult;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Expression timeline failed: {ex.Message}";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "Expression failed";
            this.log.Warning(ex, "Failed to play actor expression timeline. RuntimeId={RuntimeId}, ExpressionId={ExpressionId}", actor.RuntimeId, expressionId);
            return false;
        }
    }

    public bool ApplyLipTalk(RuntimeActorInstance actor, uint lipTimelineId, out string reason)
    {
        if (lipTimelineId > ushort.MaxValue)
        {
            reason = $"Lip talk ActionTimelineId is out of ushort range: {lipTimelineId}.";
            actor.LastLipTalkError = reason;
            return false;
        }

        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false, native lip talk write skipped.";
            actor.LastLipTalkError = reason;
            return false;
        }

        if (!this.TryResolveActorAddress(actor, "LipTalk", out var address, out reason))
        {
            actor.LastLipTalkError = reason;
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                var timeline = (byte*)&character->Timeline;
                *(ushort*)(timeline + TimelineLipsOverrideOffset) = (ushort)lipTimelineId;
            }

            actor.CurrentLipTalkId = lipTimelineId;
            actor.LastLipTalkError = string.Empty;
            actor.LastLipTalkResult = lipTimelineId == 0
                ? "Lip talk override cleared."
                : $"Lip talk override applied: {lipTimelineId}";
            reason = actor.LastLipTalkResult;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Lip talk override failed: {ex.Message}";
            actor.LastLipTalkError = reason;
            actor.LastLipTalkResult = "Lip talk failed";
            this.log.Warning(ex, "Failed to apply actor lip talk override. RuntimeId={RuntimeId}, LipTimelineId={LipTimelineId}", actor.RuntimeId, lipTimelineId);
            return false;
        }
    }

    public bool SetSequenceVisibility(RuntimeActorInstance actor, bool visible, out string reason)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false, native draw visibility write skipped.";
            actor.LastAnimationError = reason;
            return false;
        }

        if (!this.TryResolveActorAddress(actor, "SequenceVisibility", out var address, out reason))
        {
            actor.LastAnimationError = reason;
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                if (visible)
                {
                    character->Alpha = 1f;
                    character->GameObject.EnableDraw();
                }
                else
                {
                    character->Alpha = 0f;
                    character->GameObject.DisableDraw();
                }
            }

            actor.VisibilityRuntimeState = visible ? ActorVisibilityRuntimeState.Visible : ActorVisibilityRuntimeState.SequenceHidden;
            actor.LastAnimationError = string.Empty;
            actor.LastAnimationResult = visible ? "Actor sequence visibility restored." : "Actor hidden by action sequence.";
            reason = actor.LastAnimationResult;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Actor sequence visibility write failed: {ex.Message}";
            actor.LastAnimationError = reason;
            this.log.Warning(ex, "Failed to update actor sequence visibility. RuntimeId={RuntimeId}, Visible={Visible}", actor.RuntimeId, visible);
            return false;
        }
    }

    private bool PlayTimeline(RuntimeActorInstance actor, uint animationId, bool updateDefaultAnimation, out string reason)
    {
        if (updateDefaultAnimation)
            actor.DefaultAnimationId = animationId;
        if (animationId == 0)
        {
            reason = "动画 ID 为 0。";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "未调用";
            return false;
        }

        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false，native 动画写入已禁用。";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "未调用";
            return false;
        }

        if (!this.TryResolveActorAddress(actor, updateDefaultAnimation ? "BaseTimeline" : "TransientTimeline", out var address, out reason))
        {
            reason = $"无法读取 actor Address：{actor.Address}";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "未调用";
            return false;
        }

        try
        {
            this.TimelinePlayRequested?.Invoke(actor, animationId, updateDefaultAnimation ? "BaseDefault" : "BaseTransient");
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                character->Timeline.BaseOverride = (ushort)animationId;
                character->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal, 0);
                character->Timeline.TimelineSequencer.PlayTimeline((ushort)animationId);
            }

            actor.CurrentAnimationId = animationId;
            actor.AnimationEnabled = true;
            actor.LastAnimationError = string.Empty;
            actor.LastAnimationResult = $"调用成功：Address={actor.Address}, Timeline={animationId}";
            reason = actor.LastAnimationResult;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"播放动画失败：{ex.Message}";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "调用失败";
            this.log.Warning(ex, "Failed to play actor animation. RuntimeId={RuntimeId}, AnimationId={AnimationId}", actor.RuntimeId, animationId);
            return false;
        }
    }

    public bool Stop(RuntimeActorInstance actor, out string reason)
        => this.PlayIdle(actor, out reason);

    private bool PlayIdle(RuntimeActorInstance actor, out string reason)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false，native 动画写入已禁用。";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "未调用";
            return false;
        }

        if (!this.TryResolveActorAddress(actor, "IdleTimeline", out var address, out reason))
        {
            reason = $"无法读取 actor Address：{actor.Address}";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "未调用";
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                character->Timeline.BaseOverride = 0;
                character->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal, 0);
                character->Timeline.TimelineSequencer.PlayTimeline(3);
            }

            actor.CurrentAnimationId = 0;
            actor.AnimationEnabled = false;
            actor.LastAnimationError = string.Empty;
            actor.LastAnimationResult = $"已恢复 idle：Address={actor.Address}";
            reason = actor.LastAnimationResult;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"停止动画失败：{ex.Message}";
            actor.LastAnimationError = reason;
            actor.LastAnimationResult = "调用失败";
            this.log.Warning(ex, "Failed to stop actor animation. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    private bool TryResolveActorAddress(RuntimeActorInstance actor, string operation, out nint address, out string reason)
    {
        if (!this.brioAssemblyBridge.TryResolveRuntimeActorNativeTarget(actor, out address, out var objectIndex, out reason))
        {
            this.LogTargetDebug(actor, operation, $"failed: {reason}");
            return false;
        }

        this.LogTargetDebug(actor, operation, $"ok: objectIndex={objectIndex}; address=0x{address:X}; {reason}");
        return true;
    }

    private void LogTargetDebug(RuntimeActorInstance actor, string operation, string details)
    {
        if (!this.EnableActorTargetDebugLog)
            return;

        this.log.Information(
            "[ActorFacialLipTarget] operation={Operation} runtime={RuntimeId} config={ConfigId} name={Name} objectIndex={ObjectIndex} lastKnownObjectIndex={LastKnownObjectIndex} address={Address} target={Target} details={Details}",
            operation,
            actor.RuntimeId,
            actor.ConfigId,
            actor.DisplayName,
            actor.ObjectIndex,
            actor.LastKnownObjectIndex,
            actor.Address,
            actor.LastTransformTargetDebug,
            details);
    }
}
