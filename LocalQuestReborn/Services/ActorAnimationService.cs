using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class ActorAnimationService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public ActorAnimationService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public bool Play(RuntimeActorInstance actor, uint animationId, out string reason)
        => this.PlayTimeline(actor, animationId, out reason);

    public bool PlayTimeline(RuntimeActorInstance actor, uint animationId, out string reason)
    {
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
}
