using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class BrioNpcBridgeService
{
    private static readonly (int Major, int Minor) MinimumApiVersion = (2, 0);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly BrioIpcProbeService ipcProbe;
    private readonly IPluginLog log;
    private readonly Dictionary<string, IGameObject> spawnedActors = new(StringComparer.OrdinalIgnoreCase);

    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<IGameObject, bool> despawnActor;
    private readonly ICallGateSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool> setModelTransform;

    public BrioNpcBridgeService(IDalamudPluginInterface pluginInterface, BrioIpcProbeService ipcProbe, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.ipcProbe = ipcProbe;
        this.log = log;
        this.apiVersion = pluginInterface.GetIpcSubscriber<(int Major, int Minor)>("Brio.ApiVersion");
        this.despawnActor = pluginInterface.GetIpcSubscriber<IGameObject, bool>("Brio.Actor.Despawn");
        this.setModelTransform = pluginInterface.GetIpcSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>("Brio.Actor.SetModelTransform");
    }

    public string LastFailureReason { get; private set; } = string.Empty;

    public BrioIpcProbeService IpcProbe => this.ipcProbe;

    public (int Major, int Minor)? CurrentApiVersion
    {
        get
        {
            return this.TryGetApiVersion(out var version, out _) ? version : null;
        }
    }

    public bool IsIpcAvailable => this.CurrentApiVersion != null;

    public bool IsCompatible
    {
        get
        {
            var version = this.CurrentApiVersion;
            return version != null && IsVersionAtLeast(version.Value, MinimumApiVersion);
        }
    }

    public string StatusText
    {
        get
        {
            var installed = this.GetBrioInstallStatus();
            if (!installed.IsInstalled)
                return "Brio 未安装。";

            if (!installed.IsLoaded)
                return "Brio 已安装但未启用或未加载。";

            if (!this.TryGetApiVersion(out var version, out var reason))
                return $"Brio 已加载，但 IPC 不可用：{reason}";

            return $"Brio IPC 可用，当前版本 {version.Major}.{version.Minor}，兼容：{IsVersionAtLeast(version, MinimumApiVersion)}。";
        }
    }

    public bool TrySpawn(CustomNpc npc, out string reason)
    {
        if (!this.EnsureReady(out reason))
            return false;

        try
        {
            if (this.spawnedActors.TryGetValue(npc.Id, out var existing))
                this.TryDespawn(npc.Id, out _);

            if (this.ipcProbe.SelectedSpawnIpc == null)
            {
                reason = "Brio 当前未暴露可用 Spawn IPC，需要走插件依赖/引用 Brio assembly 或参考 AQuestReborn 内部实现。";
                this.LastFailureReason = reason;
                return false;
            }

            var actor = this.InvokeProbedSpawn(this.ipcProbe.SelectedSpawnIpc);
            if (actor == null)
            {
                reason = $"{this.ipcProbe.SelectedSpawnIpc.Name} 返回 null。请确认 Brio 当前状态允许生成 actor。";
                this.LastFailureReason = reason;
                return false;
            }

            this.spawnedActors[npc.Id] = actor;
            var position = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);
            var moved = this.setModelTransform.InvokeFunc(actor, position, null, null, false);
            this.log.Information(
                "[BrioNpcBridgeService] Spawned Brio actor for NPC {NpcId}. Actor={ActorName}, Moved={Moved}",
                npc.Id,
                actor.Name.ToString(),
                moved);

            reason = moved
                ? $"已通过 Brio IPC 生成 actor：{npc.Name}"
                : $"已生成 actor，但设置位置失败：{npc.Name}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio spawn IPC 调用失败：{ex.Message}";
            this.LastFailureReason = reason;
            this.log.Error(
                ex,
                "Failed to spawn Brio actor for NPC {NpcId}. SelectedSpawnIpc={SpawnIpc}. Probe={Probe}",
                npc.Id,
                this.ipcProbe.SelectedSpawnIpc?.Name ?? "none",
                string.Join(" | ", this.ipcProbe.Results.Select(result => $"{result.Name}:{result.IsRegistered}:{result.Signature}:{result.ErrorMessage}")));
            return false;
        }
    }

    private IGameObject? InvokeProbedSpawn(BrioIpcProbeResult spawnIpc)
        => spawnIpc.InvocationKind switch
        {
            BrioIpcInvocationKind.NoArgumentSyncSpawn => this.pluginInterface.GetIpcSubscriber<IGameObject?>(spawnIpc.Name).InvokeFunc(),
            BrioIpcInvocationKind.NoArgumentAsyncSpawn => null,
            _ => null,
        };

    public bool TryDespawn(string npcId, out string reason)
    {
        if (!this.spawnedActors.TryGetValue(npcId, out var actor))
        {
            reason = $"没有记录到 NPC {npcId} 对应的 Brio actor。";
            return true;
        }

        try
        {
            var result = this.despawnActor.InvokeFunc(actor);
            if (result)
                this.spawnedActors.Remove(npcId);

            reason = result ? $"已删除 Brio actor：{npcId}" : $"Brio 返回删除失败：{npcId}";
            if (!result)
                this.LastFailureReason = reason;

            return result;
        }
        catch (Exception ex)
        {
            reason = $"Brio despawn IPC 调用失败：{ex.Message}";
            this.LastFailureReason = reason;
            this.log.Error(ex, "Failed to despawn Brio actor for NPC {NpcId}", npcId);
            return false;
        }
    }

    public bool TryDespawnAll(out string reason)
    {
        var failures = new List<string>();
        foreach (var npcId in this.spawnedActors.Keys.ToList())
        {
            if (!this.TryDespawn(npcId, out var singleReason))
                failures.Add(singleReason);
        }

        if (failures.Count == 0)
        {
            reason = "已清理全部 Brio actor 记录。";
            return true;
        }

        reason = string.Join("；", failures);
        this.LastFailureReason = reason;
        return false;
    }

    public bool TryApplyAppearance(CustomNpc npc, out string reason)
    {
        if (!this.spawnedActors.ContainsKey(npc.Id))
        {
            reason = $"NPC {npc.Id} 尚未通过 Brio IPC 生成 actor，无法应用外观。";
            this.LastFailureReason = reason;
            return false;
        }

        var appearance = npc.Appearance ?? new CustomNpcAppearance();
        reason = appearance.SourceType switch
        {
            CustomNpcAppearanceSourceType.GameNpc => "Brio 最小 IPC 只支持 spawn/transform/despawn，GameNpc 外观套用尚未实现。",
            CustomNpcAppearanceSourceType.GlamourerDesign => "Brio 最小 IPC 未公开直接应用 Glamourer design 的调用，后续需要 Glamourer IPC 或 Brio 外观接口。",
            CustomNpcAppearanceSourceType.PenumbraCollection => "Brio 最小 IPC 未公开直接切换 Penumbra collection 的调用。",
            CustomNpcAppearanceSourceType.MCDF => "Brio 有 MCDF IPC provider，但当前项目未绑定 BrioApiResult 类型；后续可单独封装 MCDF 调用。",
            CustomNpcAppearanceSourceType.CurrentPlayer => "SpawnExAsync 默认克隆当前玩家外观；如需重新套用当前玩家外观，需要进一步封装 Brio appearance IPC。",
            _ => "该 NPC 没有可应用的真实外观来源。",
        };

        this.log.Information("[BrioNpcBridgeService] Apply appearance skipped for {NpcId}: {Reason}", npc.Id, reason);
        this.LastFailureReason = reason;
        return false;
    }

    private bool EnsureReady(out string reason)
    {
        var installStatus = this.GetBrioInstallStatus();
        if (!installStatus.IsInstalled)
        {
            reason = "未检测到 Brio 插件。请先安装 Brio 并确认它在 Dalamud 插件列表中可用。";
            this.LastFailureReason = reason;
            return false;
        }

        if (!installStatus.IsLoaded)
        {
            reason = "检测到 Brio 已安装，但当前未加载。请先启用 Brio。";
            this.LastFailureReason = reason;
            return false;
        }

        if (!this.TryGetApiVersion(out var version, out reason))
        {
            this.LastFailureReason = reason;
            return false;
        }

        if (!IsVersionAtLeast(version, MinimumApiVersion))
        {
            reason = $"Brio IPC 版本过低：当前 {version.Major}.{version.Minor}，最低需要 {MinimumApiVersion.Major}.{MinimumApiVersion.Minor}。";
            this.LastFailureReason = reason;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryGetApiVersion(out (int Major, int Minor) version, out string reason)
    {
        try
        {
            version = this.apiVersion.InvokeFunc();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            version = default;
            reason = $"Brio.ApiVersion IPC 调用失败：{ex.Message}";
            return false;
        }
    }

    private (bool IsInstalled, bool IsLoaded) GetBrioInstallStatus()
    {
        var plugin = this.pluginInterface.InstalledPlugins.FirstOrDefault(item =>
            item.Name.Equals("Brio", StringComparison.OrdinalIgnoreCase) ||
            item.InternalName.Equals("Brio", StringComparison.OrdinalIgnoreCase));
        return (plugin != null, plugin?.IsLoaded == true);
    }

    private static bool IsVersionAtLeast((int Major, int Minor) version, (int Major, int Minor) minimum)
        => version.Major > minimum.Major ||
            (version.Major == minimum.Major && version.Minor >= minimum.Minor);
}
