using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class GameNpcAppearanceApplyService
{
    private readonly BrioHumanoidAppearanceApplyService brioHumanoidAppearanceApply;
    private readonly GlamourerStateApplyService glamourerStateApply;
    private readonly IPluginLog log;

    public GameNpcAppearanceApplyService(
        BrioHumanoidAppearanceApplyService brioHumanoidAppearanceApply,
        GlamourerStateApplyService glamourerStateApply,
        IPluginLog log)
    {
        this.brioHumanoidAppearanceApply = brioHumanoidAppearanceApply;
        this.glamourerStateApply = glamourerStateApply;
        this.log = log;
    }

    public bool IsGlamourerApplyStateAvailable => this.glamourerStateApply.IsAvailable;

    public string GlamourerApplyStateSignature => this.glamourerStateApply.Signature;

    public string GlamourerApplyStateLastResult => this.glamourerStateApply.LastResult;

    public string GlamourerApplyStateLastException => this.glamourerStateApply.LastException;

    public bool IsBrioActorAppearanceCapabilityAvailable => this.brioHumanoidAppearanceApply.IsAvailable;

    public string BrioActorAppearanceLastResult => this.brioHumanoidAppearanceApply.LastResult;

    public string BrioActorAppearanceLastException => this.brioHumanoidAppearanceApply.LastException;

    public string BrioActorAppearanceSignature => this.brioHumanoidAppearanceApply.LastSignature;

    public string CurrentHumanoidApplyPath { get; private set; } = "未调用";

    public string LastHumanoidApplyResult { get; private set; } = string.Empty;

    public string LastHumanoidApplyException { get; private set; } = string.Empty;

    public void RefreshPathStatus()
    {
        this.glamourerStateApply.Refresh();
        this.brioHumanoidAppearanceApply.Refresh();
    }

    public bool TryApplyModelChara(RuntimeActorInstance actor, GameNpcAppearanceResolution resolution, out string reason)
    {
        if (resolution.Appearance.Kind == GameNpcResolvedAppearanceKind.Humanoid)
            return this.TryApplyHumanoid(actor, resolution, out reason);

        if (resolution.ModelCharaId == 0)
        {
            reason = "没有可应用的 ModelCharaId。";
            return false;
        }

        reason = $"已解析 Monster/ModelChara ModelCharaId={resolution.ModelCharaId}，但 v1.7 禁止直接 native 写 ModelCharaId；需要后续接入 Brio ActorAppearanceCapability 的非人形模型路径或 AppearanceManager。";
        this.log.Information("[GameNpcAppearanceApplyService] Skip native ModelCharaId write. RuntimeId={RuntimeId}, ModelCharaId={ModelCharaId}", actor.RuntimeId, resolution.ModelCharaId);
        return false;
    }

    private bool TryApplyHumanoid(RuntimeActorInstance actor, GameNpcAppearanceResolution resolution, out string reason)
    {
        this.CurrentHumanoidApplyPath = "Brio ActorAppearanceCapability";
        if (this.brioHumanoidAppearanceApply.TryApplyHumanoid(actor, resolution, out reason))
        {
            this.LastHumanoidApplyException = string.Empty;
            this.LastHumanoidApplyResult = reason;
            return true;
        }

        this.LastHumanoidApplyException = reason;
        this.LastHumanoidApplyResult =
            $"Brio ActorAppearanceCapability 路径失败：{reason}；" +
            $"Glamourer ApplyState 探测：{this.glamourerStateApply.Signature}。";
        reason = this.LastHumanoidApplyResult;
        return false;
    }
}
