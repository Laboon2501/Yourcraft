using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

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

        if (!TryValidateActorTarget(actor, out reason))
        {
            actor.LastAnimationError = reason;
            return false;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            reason = $"Actor address unavailable: {actor.Address}";
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

        if (!TryValidateActorTarget(actor, out reason))
        {
            actor.LastLipTalkError = reason;
            return false;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            reason = $"Actor address unavailable: {actor.Address}";
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

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            reason = $"Actor address unavailable: {actor.Address}";
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

        if (!TryReadAddress(actor, out var address) || address == 0)
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

        if (!TryReadAddress(actor, out var address) || address == 0)
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

    private static bool TryReadAddress(RuntimeActorInstance actor, out nint address)
    {
        address = 0;
        var raw = actor.Address?.Trim() ?? string.Empty;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            address = (nint)hex;
            return true;
        }

        if (ulong.TryParse(raw, out var value))
        {
            address = (nint)value;
            return true;
        }

        return false;
    }

    private static bool TryValidateActorTarget(RuntimeActorInstance actor, out string reason)
    {
        var objectIndex = actor.LastKnownObjectIndex;
        if (int.TryParse(actor.ObjectIndex, out var parsedIndex))
            objectIndex = parsedIndex;

        if (objectIndex <= 0)
        {
            reason = $"invalid Actor objectIndex={objectIndex}; refusing to write facial/lip data to objectIndex 0/local player.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
