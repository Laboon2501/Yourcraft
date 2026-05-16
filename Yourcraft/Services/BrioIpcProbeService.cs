using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Yourcraft.Services;

public sealed class BrioIpcProbeService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly List<BrioIpcProbeResult> results = [];

    public BrioIpcProbeService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.LastProbeMessage = "尚未手动探测 Brio IPC。";
    }

    public IReadOnlyList<BrioIpcProbeResult> Results => this.results;

    public string LastProbeMessage { get; private set; } = string.Empty;

    public BrioIpcProbeResult? SelectedSpawnIpc { get; private set; }

    public void Probe()
    {
        this.results.Clear();
        this.SelectedSpawnIpc = null;

        this.ProbeNoArgumentValue<(int Major, int Minor)>("Brio.ApiVersion", "无参数 -> (int Major, int Minor)");
        this.ProbeNoArgumentValue<string>("Brio.Version", "无参数 -> string");

        this.ProbeSpawnNoArgumentSync("Brio.Actor.Spawn");
        this.ProbeSpawnNoArgumentAsync("Brio.Actor.SpawnAsync");
        this.ProbeSpawnNoArgumentSync("Brio.Actor.Create");
        this.ProbeSpawnNoArgumentAsync("Brio.Actor.CreateAsync");
        this.ProbeActorBool("Brio.Actor.Delete", "IGameObject -> bool");
        this.ProbeActorBool("Brio.Actor.Despawn", "IGameObject -> bool");
        this.ProbeActorAppearance("Brio.Actor.ApplyAppearance");
        this.ProbeActorAppearance("Brio.Actor.SetAppearance");
        this.ProbeSpawnNoArgumentSync("Brio.Scene.SpawnActor");
        this.ProbeActorBool("Brio.Scene.DeleteActor", "IGameObject -> bool");
        this.ProbeSpawnNoArgumentSync("Brio.GPose.SpawnActor");

        this.SelectedSpawnIpc = this.results.FirstOrDefault(result =>
            result.IsRegistered &&
            result.IsSpawnCandidate &&
            (result.InvocationKind == BrioIpcInvocationKind.NoArgumentSyncSpawn ||
             result.InvocationKind == BrioIpcInvocationKind.NoArgumentAsyncSpawn));

        this.LastProbeMessage = this.SelectedSpawnIpc == null
            ? "未发现可用 Spawn IPC。Brio 当前未暴露可用 Spawn IPC，需要走插件依赖/引用 Brio assembly 或参考 AQuestReborn 内部实现。"
            : $"发现可用 Spawn IPC：{this.SelectedSpawnIpc.Name} ({this.SelectedSpawnIpc.Signature})";

        this.log.Information(
            "Brio IPC probe finished. Registered={RegisteredCount}, Spawn={SpawnName}",
            this.results.Count(result => result.IsRegistered),
            this.SelectedSpawnIpc?.Name ?? "none");
    }

    private void ProbeNoArgumentValue<T>(string name, string signature)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<T>(name);
            this.results.Add(new BrioIpcProbeResult(name, signature, subscriber.HasFunction, string.Empty, false, BrioIpcInvocationKind.NoArgumentValue));
        }
        catch (Exception ex)
        {
            this.results.Add(new BrioIpcProbeResult(name, signature, false, ex.Message, false, BrioIpcInvocationKind.NoArgumentValue));
            this.log.Warning(ex, "Failed to probe Brio IPC {Name}", name);
        }
    }

    private void ProbeSpawnNoArgumentSync(string name)
    {
        const string signature = "无参数 -> IGameObject?";
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<IGameObject?>(name);
            this.results.Add(new BrioIpcProbeResult(name, signature, subscriber.HasFunction, string.Empty, true, BrioIpcInvocationKind.NoArgumentSyncSpawn));
        }
        catch (Exception ex)
        {
            this.results.Add(new BrioIpcProbeResult(name, signature, false, ex.Message, true, BrioIpcInvocationKind.NoArgumentSyncSpawn));
            this.log.Warning(ex, "Failed to probe Brio IPC {Name}", name);
        }
    }

    private void ProbeSpawnNoArgumentAsync(string name)
    {
        const string signature = "无参数 -> Task<IGameObject?>";
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<Task<IGameObject?>>(name);
            this.results.Add(new BrioIpcProbeResult(name, signature, subscriber.HasFunction, string.Empty, true, BrioIpcInvocationKind.NoArgumentAsyncSpawn));
        }
        catch (Exception ex)
        {
            this.results.Add(new BrioIpcProbeResult(name, signature, false, ex.Message, true, BrioIpcInvocationKind.NoArgumentAsyncSpawn));
            this.log.Warning(ex, "Failed to probe Brio IPC {Name}", name);
        }
    }

    private void ProbeActorBool(string name, string signature)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<IGameObject, bool>(name);
            this.results.Add(new BrioIpcProbeResult(name, signature, subscriber.HasFunction, string.Empty, false, BrioIpcInvocationKind.ActorBool));
        }
        catch (Exception ex)
        {
            this.results.Add(new BrioIpcProbeResult(name, signature, false, ex.Message, false, BrioIpcInvocationKind.ActorBool));
            this.log.Warning(ex, "Failed to probe Brio IPC {Name}", name);
        }
    }

    private void ProbeActorAppearance(string name)
    {
        const string signature = "IGameObject, string -> bool";
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<IGameObject, string, bool>(name);
            this.results.Add(new BrioIpcProbeResult(name, signature, subscriber.HasFunction, string.Empty, false, BrioIpcInvocationKind.ActorAppearance));
        }
        catch (Exception ex)
        {
            this.results.Add(new BrioIpcProbeResult(name, signature, false, ex.Message, false, BrioIpcInvocationKind.ActorAppearance));
            this.log.Warning(ex, "Failed to probe Brio IPC {Name}", name);
        }
    }
}

public sealed record BrioIpcProbeResult(
    string Name,
    string Signature,
    bool IsRegistered,
    string ErrorMessage,
    bool IsSpawnCandidate,
    BrioIpcInvocationKind InvocationKind);

public enum BrioIpcInvocationKind
{
    NoArgumentValue,
    NoArgumentSyncSpawn,
    NoArgumentAsyncSpawn,
    ActorBool,
    ActorAppearance,
}
