using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Text.Json;

namespace LocalQuestReborn.Services;

public sealed class AppearanceApplyService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly GameNpcAppearanceResolver gameNpcResolver;
    private readonly GameNpcAppearanceApplyService gameNpcApplyService;
    private readonly IPluginLog log;

    public AppearanceApplyService(
        IDalamudPluginInterface pluginInterface,
        GlamourerIpcProbeService glamourerIpcProbe,
        GlamourerIpcBridgeService glamourerIpcBridge,
        PenumbraIpcService penumbraIpc,
        GameNpcAppearanceResolver gameNpcResolver,
        GameNpcAppearanceApplyService gameNpcApplyService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.glamourerIpcProbe = glamourerIpcProbe;
        this.glamourerIpcBridge = glamourerIpcBridge;
        this.penumbraIpc = penumbraIpc;
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
    public bool HumanoidBrioActorAppearanceApplyPending => this.gameNpcApplyService.BrioActorAppearanceApplyPending;
    public string? HumanoidBrioActorAppearancePendingRuntimeId => this.gameNpcApplyService.BrioActorAppearancePendingRuntimeId;
    public DateTime? HumanoidBrioActorAppearanceApplyCompletedAt => this.gameNpcApplyService.BrioActorAppearanceApplyCompletedAt;

    public bool IsNpcAppearanceApplyPending(RuntimeActorInstance actor)
        => this.gameNpcApplyService.BrioActorAppearanceApplyPending &&
           string.Equals(this.gameNpcApplyService.BrioActorAppearancePendingRuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase);

    public string DescribeNpcPreset(CustomNpc npc)
    {
        var appearance = npc.Appearance ?? new CustomNpcAppearance();
        try
        {
            return appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.GameNpc => this.DescribeGameNpcPreset(appearance),
                CustomNpcAppearanceSourceType.GlamourerDesign => DescribeGlamourerDesignPreset(appearance),
                CustomNpcAppearanceSourceType.CurrentPlayer => "source=CurrentPlayer; hasEquipment=player-clone; fullDesign=false",
                CustomNpcAppearanceSourceType.None => "source=None; hasEquipment=false; fullDesign=false",
                CustomNpcAppearanceSourceType.PenumbraCollection => "source=PenumbraCollection; hasEquipment=false; fullDesign=false; collection-only",
                CustomNpcAppearanceSourceType.MCDF => "source=MCDF; hasEquipment=unknown; fullDesign=unknown; MCDF apply not connected",
                _ => $"source={appearance.SourceType}; hasEquipment=unknown; fullDesign=unknown",
            };
        }
        catch (Exception ex)
        {
            return $"source={appearance.SourceType}; preset diagnostic failed={ex.Message}";
        }
    }

    public void RefreshHumanoidAppearancePaths()
        => this.gameNpcApplyService.RefreshPathStatus();

    public bool ApplyNpcAppearance(CustomNpc npc, RuntimeActorInstance actor)
    {
        try
        {
            var appearance = npc.Appearance ?? new CustomNpcAppearance();
            actor.AppearanceSourceType = appearance.SourceType.ToString();
            if (!this.penumbraIpc.ApplyCollection(npc, actor, out var penumbraReason))
            {
                actor.LastPenumbraCollectionError = penumbraReason;
                this.log.Warning("Penumbra collection stage failed but appearance pipeline will continue. Actor={Actor}, Reason={Reason}", actor.RuntimeId, penumbraReason);
            }

            var success = appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.None => this.ApplyNone(actor),
                CustomNpcAppearanceSourceType.CurrentPlayer => this.ApplyCurrentPlayer(actor),
                CustomNpcAppearanceSourceType.GlamourerDesign => this.ApplyGlamourerDesign(appearance, actor),
                CustomNpcAppearanceSourceType.GameNpc => this.ApplyGameNpc(appearance, actor),
                CustomNpcAppearanceSourceType.MCDF => this.Fail(actor, "MCDF", "MCDF 外观应用暂未接入安全接口。"),
                CustomNpcAppearanceSourceType.PenumbraCollection => this.ApplyPenumbraAppearanceSource(actor),
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

    public bool ApplyNpcPresetAppearance(CustomNpc npc, RuntimeActorInstance actor)
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
                CustomNpcAppearanceSourceType.MCDF => this.Fail(actor, "MCDF", "MCDF appearance apply is not connected to a safe path yet."),
                CustomNpcAppearanceSourceType.PenumbraCollection => this.ApplyPenumbraAppearanceSource(actor),
                _ => this.Fail(actor, appearance.SourceType.ToString(), $"Unknown appearance source: {appearance.SourceType}"),
            };

            actor.LastAppearanceAppliedAt = DateTime.Now;
            return success;
        }
        catch (Exception ex)
        {
            actor.LastAppearanceMethod = "Failed";
            actor.LastAppearanceError = ex.Message;
            actor.LastAppearanceApplyResult = $"Appearance apply exception: {ex.Message}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            this.log.Error(ex, "ApplyNpcPresetAppearance failed. RuntimeId={RuntimeId}, NpcId={NpcId}", actor.RuntimeId, npc.Id);
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

    private bool ApplyPenumbraAppearanceSource(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "PenumbraCollection";
        actor.LastAppearanceError = actor.LastPenumbraCollectionError;
        actor.LastAppearanceApplyResult = string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionResult)
            ? "Penumbra collection handled by NPC-level collection stage."
            : actor.LastPenumbraCollectionResult;
        return string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionError);
    }

    private bool ApplyGlamourerDesign(CustomNpcAppearance appearance, RuntimeActorInstance actor)
    {
        if (string.IsNullOrWhiteSpace(appearance.GlamourerDesignId))
            return this.Fail(actor, "GlamourerDesign", "Glamourer 设计 ID 为空。");

        if (!int.TryParse(actor.ObjectIndex, out var objectIndex))
            return this.Fail(actor, "GlamourerDesign", $"actor ObjectIndex 不可用：{actor.ObjectIndex}");

        if (!this.glamourerIpcBridge.ApplyDesignToObject(appearance.GlamourerDesignId, objectIndex, out var applyReason))
            return this.Fail(actor, "GlamourerDesign", applyReason);

        var redraw = "post-preset Penumbra redraw skipped; collection redraw happens before preset apply.";
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

        var redraw = "post-preset Penumbra redraw skipped; collection redraw happens before preset apply.";
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

    private string DescribeGameNpcPreset(CustomNpcAppearance appearance)
    {
        var resolution = this.gameNpcResolver.Resolve(appearance);
        if (!resolution.Success)
            return $"source=GameNpc; hasEquipment=false; fullDesign=false; resolveFailed={resolution.FailureStep}; message={resolution.Message}";

        if (resolution.Appearance.Kind != GameNpcResolvedAppearanceKind.Humanoid)
            return $"source=GameNpc; kind={resolution.Appearance.Kind}; modelChara={resolution.ModelCharaId}; hasEquipment=false; fullDesign=false";

        var customize = BuildCustomizeParts(resolution.Appearance.Customize);
        var equipment = BuildEquipmentParts(resolution.Appearance.Equipment);
        return $"source=GameNpc; kind=Humanoid; hasEquipment=true; fullDesign=true; customizeHash={StableHash(string.Join(';', customize))}; equipmentHash={StableHash(string.Join(';', equipment))}; equipment={string.Join(',', equipment)}";
    }

    private static string DescribeGlamourerDesignPreset(CustomNpcAppearance appearance)
    {
        var path = appearance.Notes;
        if (string.IsNullOrWhiteSpace(path))
            return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment=unknown; fullDesign=unknown; designPath=missing";

        if (!File.Exists(path))
            return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment=unknown; fullDesign=unknown; designPathNotFound={path}";

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var hasEquipment = TryFindProperty(document.RootElement, "Equipment", out var equipmentElement);
        var hasCustomize = TryFindProperty(document.RootElement, "Customize", out var customizeElement) ||
                           TryFindProperty(document.RootElement, "Customization", out customizeElement);
        var equipmentRaw = hasEquipment ? NormalizeJson(equipmentElement) : string.Empty;
        var customizeRaw = hasCustomize ? NormalizeJson(customizeElement) : string.Empty;
        var slots = hasEquipment ? string.Join(',', ReadJsonSlotNames(equipmentElement)) : "none";
        return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment={hasEquipment}; hasCustomize={hasCustomize}; fullDesign={hasEquipment && hasCustomize}; equipmentHash={StableHash(equipmentRaw)}; customizeHash={StableHash(customizeRaw)}; slots={slots}; path={path}";
    }

    private static IEnumerable<string> BuildCustomizeParts(GameNpcResolvedCustomize customize)
    {
        yield return $"Race={customize.Race}";
        yield return $"Gender={customize.Gender}";
        yield return $"Tribe={customize.Tribe}";
        yield return $"BodyType={customize.BodyType}";
        yield return $"Height={customize.Height}";
        yield return $"Face={customize.Face}";
        yield return $"HairStyle={customize.HairStyle}";
        yield return $"SkinColor={customize.SkinColor}";
        yield return $"EyeColor={customize.EyeColor}";
    }

    private static IEnumerable<string> BuildEquipmentParts(GameNpcResolvedEquipment equipment)
    {
        yield return $"MainHand={equipment.MainHand}";
        yield return $"OffHand={equipment.OffHand}";
        yield return $"Head={equipment.Head}";
        yield return $"Body={equipment.Body}";
        yield return $"Hands={equipment.Hands}";
        yield return $"Legs={equipment.Legs}";
        yield return $"Feet={equipment.Feet}";
        yield return $"Ears={equipment.Ears}";
        yield return $"Neck={equipment.Neck}";
        yield return $"Wrists={equipment.Wrists}";
        yield return $"LeftRing={equipment.LeftRing}";
        yield return $"RightRing={equipment.RightRing}";
    }

    private static bool TryFindProperty(JsonElement element, string propertyName, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    found = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, propertyName, out found))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyName, out found))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static IEnumerable<string> ReadJsonSlotNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return ["<non-object-equipment>"];

        return element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
    }

    private static string NormalizeJson(JsonElement element)
        => JsonSerializer.Serialize(element);

    private static string StableHash(string text)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= prime;
            }

            return hash.ToString("X16");
        }
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
