using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class AppearanceApplyService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly GameNpcAppearanceResolver gameNpcResolver;
    private readonly GameNpcAppearanceApplyService gameNpcApplyService;
    private readonly IPluginLog log;

    public AppearanceApplyService(
        IDalamudPluginInterface pluginInterface,
        GlamourerIpcProbeService glamourerIpcProbe,
        GlamourerIpcBridgeService glamourerIpcBridge,
        GameNpcAppearanceResolver gameNpcResolver,
        GameNpcAppearanceApplyService gameNpcApplyService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.glamourerIpcProbe = glamourerIpcProbe;
        this.glamourerIpcBridge = glamourerIpcBridge;
        this.gameNpcResolver = gameNpcResolver;
        this.gameNpcApplyService = gameNpcApplyService;
        this.log = log;
    }

    public bool IsHumanoidGlamourerApplyStateAvailable => this.gameNpcApplyService.IsGlamourerApplyStateAvailable;
    public string HumanoidGlamourerApplyStateSignature => this.gameNpcApplyService.GlamourerApplyStateSignature;
    public bool IsHumanoidBrioActorAppearanceAvailable => this.gameNpcApplyService.IsBrioActorAppearanceCapabilityAvailable;
    public string HumanoidBrioActorAppearanceSignature => this.gameNpcApplyService.BrioActorAppearanceSignature;
    public string HumanoidAppearanceCurrentPath => this.gameNpcApplyService.CurrentHumanoidApplyPath;
    public string HumanoidAppearanceLastResult => this.gameNpcApplyService.LastHumanoidApplyResult;
    public string HumanoidAppearanceLastException => this.gameNpcApplyService.LastHumanoidApplyException;
    public string HumanoidGlamourerApplyStateLastResult => this.gameNpcApplyService.GlamourerApplyStateLastResult;
    public string HumanoidGlamourerApplyStateLastException => this.gameNpcApplyService.GlamourerApplyStateLastException;
    public string HumanoidBrioActorAppearanceLastResult => this.gameNpcApplyService.BrioActorAppearanceLastResult;
    public string HumanoidBrioActorAppearanceLastException => this.gameNpcApplyService.BrioActorAppearanceLastException;

    public void RefreshHumanoidAppearancePaths()
        => this.gameNpcApplyService.RefreshPathStatus();

    public bool ApplyNpcAppearance(CustomNpc npc, RuntimeActorInstance actor)
    {
        try
        {
            var appearance = npc.Appearance ?? new CustomNpcAppearance();
            actor.AppearanceSourceType = appearance.SourceType.ToString();
            var success = appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.None => this.ApplyNone(actor),
                CustomNpcAppearanceSourceType.CurrentPlayer => this.ApplyCurrentPlayer(actor),
                CustomNpcAppearanceSourceType.GlamourerDesign => this.ApplyGlamourerDesign(appearance, actor),
                CustomNpcAppearanceSourceType.GameNpc => this.ApplyGameNpc(appearance, actor),
                CustomNpcAppearanceSourceType.MCDF => this.Fail(actor, "MCDF", "MCDF 外观应用暂未接入安全接口。"),
                CustomNpcAppearanceSourceType.PenumbraCollection => this.Fail(actor, "PenumbraCollection", "Penumbra Collection 切换暂未实现。"),
                _ => this.Fail(actor, appearance.SourceType.ToString(), $"未知外观来源：{appearance.SourceType}"),
            };

            actor.LastAppearanceAppliedAt = DateTime.Now;
            return success;
        }
        catch (Exception ex)
        {
            actor.LastAppearanceMethod = "Failed";
            actor.LastAppearanceError = ex.Message;
            actor.LastAppearanceApplyResult = $"外观应用异常：{ex.Message}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            this.log.Error(ex, "ApplyNpcAppearance failed. RuntimeId={RuntimeId}, NpcId={NpcId}", actor.RuntimeId, npc.Id);
            return false;
        }
    }

    private bool ApplyNone(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "None";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = "未设置外观来源，跳过处理。";
        return true;
    }

    private bool ApplyCurrentPlayer(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "CurrentPlayer";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = "保持 Brio CreateCharacter 生成的玩家 clone 外观。";
        return true;
    }

    private bool ApplyGlamourerDesign(CustomNpcAppearance appearance, RuntimeActorInstance actor)
    {
        if (string.IsNullOrWhiteSpace(appearance.GlamourerDesignId))
            return this.Fail(actor, "GlamourerDesign", "Glamourer 设计 ID 为空。");

        if (!int.TryParse(actor.ObjectIndex, out var objectIndex))
            return this.Fail(actor, "GlamourerDesign", $"actor ObjectIndex 不可用：{actor.ObjectIndex}");

        if (!this.glamourerIpcBridge.ApplyDesignToObject(appearance.GlamourerDesignId, objectIndex, out var applyReason))
            return this.Fail(actor, "GlamourerDesign", applyReason);

        var redraw = this.TryPenumbraRedraw(objectIndex);
        var ipc = this.glamourerIpcBridge.SelectedApplyDesign;
        actor.LastAppearanceMethod = $"GlamourerDesign via {ipc?.Name ?? "unknown"}";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = $"成功应用 Glamourer 设计：{appearance.DisplayName} / {appearance.GlamourerDesignId}。{applyReason}。{redraw}";
        return true;
    }

    private bool ApplyGameNpc(CustomNpcAppearance appearance, RuntimeActorInstance actor)
    {
        var resolution = this.gameNpcResolver.Resolve(appearance);
        var chain = string.Join(" -> ", resolution.Chain);
        if (!resolution.Success)
            return this.Fail(actor, "GameNpc", $"GameNpc 外观解析失败。RowId={appearance.GameNpcBaseId}，失败步骤={resolution.FailureStep}，解析链={chain}");

        if (!this.gameNpcApplyService.TryApplyModelChara(actor, resolution, out var applyReason))
        {
            actor.LastAppearanceMethod = "GameNpc";
            actor.LastAppearanceError = applyReason;
            actor.LastAppearanceApplyResult = $"{resolution.Message} Kind={resolution.Appearance.Kind}, ModelCharaId={resolution.ModelCharaId}。解析链={chain}。应用结果：{applyReason}";
            return false;
        }

        var redraw = int.TryParse(actor.ObjectIndex, out var objectIndex)
            ? this.TryPenumbraRedraw(objectIndex)
            : "ObjectIndex 不可用，跳过 redraw。";
        actor.LastAppearanceMethod = "GameNpc";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = $"GameNpc 外观已应用。Kind={resolution.Appearance.Kind}, ModelCharaId={resolution.ModelCharaId}。{applyReason} {redraw} 解析链={chain}";
        return true;
    }

    private string TryPenumbraRedraw(int objectIndex)
    {
        var ipc = this.glamourerIpcProbe.SelectedPenumbraRedraw;
        if (ipc == null)
            return "尚未发现 Penumbra Redraw IPC。请先手动点击“探测 Glamourer IPC”。";

        try
        {
            var result = ipc.InvocationKind switch
            {
                GlamourerIpcInvocationKind.RedrawIntBool => this.pluginInterface.GetIpcSubscriber<int, bool>(ipc.Name).InvokeFunc(objectIndex),
                GlamourerIpcInvocationKind.RedrawUshortBool => TryObjectIndexToUshort(objectIndex, out var index) && this.pluginInterface.GetIpcSubscriber<ushort, bool>(ipc.Name).InvokeFunc(index),
                GlamourerIpcInvocationKind.RedrawIntObject => this.pluginInterface.GetIpcSubscriber<int, object>(ipc.Name).InvokeFunc(objectIndex),
                GlamourerIpcInvocationKind.RedrawUshortObject => TryObjectIndexToUshort(objectIndex, out var index) ? this.pluginInterface.GetIpcSubscriber<ushort, object>(ipc.Name).InvokeFunc(index) : false,
                _ => false,
            };

            return $"Penumbra redraw={result}，IPC={ipc.Name}，参数类型={ipc.Signature}。";
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Penumbra redraw failed. Ipc={IpcName}, Signature={Signature}", ipc.Name, ipc.Signature);
            return $"Penumbra redraw 调用失败：IPC={ipc.Name}，参数类型={ipc.Signature}，异常={ex.Message}";
        }
    }

    private bool Fail(RuntimeActorInstance actor, string method, string reason)
    {
        actor.LastAppearanceMethod = method;
        actor.LastAppearanceError = reason;
        actor.LastAppearanceApplyResult = reason;
        return false;
    }

    private static bool TryObjectIndexToUshort(int objectIndex, out ushort value)
    {
        if (objectIndex is >= ushort.MinValue and <= ushort.MaxValue)
        {
            value = (ushort)objectIndex;
            return true;
        }

        value = 0;
        return false;
    }
}
