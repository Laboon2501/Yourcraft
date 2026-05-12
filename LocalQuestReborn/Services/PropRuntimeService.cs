using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed class PropRuntimeService
{
    private readonly IObjectTable objectTable;
    private readonly QuestDatabase database;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly BrioPropBridgeService brioPropBridge;
    private readonly PropModelService propModelService;
    private readonly IPluginLog log;
    private readonly Dictionary<string, RuntimePropInstance> props = new(StringComparer.OrdinalIgnoreCase);

    public PropRuntimeService(
        IObjectTable objectTable,
        QuestDatabase database,
        BrioAssemblyBridgeService brioAssemblyBridge,
        BrioPropBridgeService brioPropBridge,
        PropModelService propModelService,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.database = database;
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.brioPropBridge = brioPropBridge;
        this.propModelService = propModelService;
        this.log = log;
    }

    public IReadOnlyList<RuntimePropInstance> Props => this.props.Values.OrderBy(prop => prop.PropName, StringComparer.OrdinalIgnoreCase).ThenBy(prop => prop.RuntimeId).ToList();

    public string LastMessage { get; private set; } = "场景物体运行态已就绪。";

    public string PropModelLastResult => this.propModelService.LastResult;

    public bool EnableUnsafeNativeWrites => this.brioAssemblyBridge.EnableUnsafeNativeWrites;

    public RuntimePropInstance? Get(string runtimeId)
        => this.props.TryGetValue(runtimeId, out var prop) ? prop : null;

    public IReadOnlyList<RuntimePropInstance> GetByPropId(string propId)
        => this.props.Values.Where(prop => string.Equals(prop.PropId, propId, StringComparison.OrdinalIgnoreCase)).ToList();

    public RuntimePropInstance? GetLatestByPropId(string propId)
        => this.props.Values
            .Where(prop => string.Equals(prop.PropId, propId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(prop => prop.RuntimeId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    public RuntimePropInstance? SpawnUnique(CustomProp prop, bool usePropDataMode)
    {
        this.LastMessage = "CreateCharacter 无法生成场景物体，只会生成角色 clone。Prop 生成入口已禁用，等待真实 Brio Prop 加载链接入。";
        return null;
    }

    public RuntimePropInstance? SpawnNew(CustomProp prop, bool usePropDataMode)
    {
        this.LastMessage = "CreateCharacter 无法生成场景物体，只会生成角色 clone。Prop 生成入口已禁用，等待真实 Brio Prop 加载链接入。";
        return null;
    }

    public void CleanupCharacterCloneProps()
    {
        var clones = this.props.Values.Where(prop => prop.IsCharacterClone || !prop.IsBrioProp).ToList();
        foreach (var clone in clones)
            this.Despawn(clone.RuntimeId);

        this.LastMessage = clones.Count == 0
            ? "没有需要清理的 Character clone Prop 实例。"
            : $"已清理 {clones.Count} 个 Character clone Prop 实例。";
    }

    public void Despawn(string runtimeId)
    {
        if (!this.props.TryGetValue(runtimeId, out var prop))
            return;

        var success = this.brioPropBridge.TryDespawn(prop, out var reason);
        this.props.Remove(runtimeId);
        this.LastMessage = success ? reason : $"删除 Prop 失败：{reason}";
    }

    public void DespawnAllForProp(string propId)
    {
        foreach (var instance in this.GetByPropId(propId).ToList())
            this.Despawn(instance.RuntimeId);

        this.LastMessage = $"已删除配置 Prop {propId} 的全部运行实例。";
    }

    public void DespawnAll()
    {
        foreach (var instance in this.props.Values.ToList())
            this.Despawn(instance.RuntimeId);

        this.LastMessage = "已删除全部 Runtime Prop。";
    }

    public void MoveToPlayer(string runtimeId)
    {
        var player = this.objectTable.LocalPlayer;
        if (player == null)
        {
            this.LastMessage = "LocalPlayer 不可用。";
            return;
        }

        this.Move(runtimeId, player.Position);
    }

    public void MoveToConfig(string runtimeId)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
            return;

        var prop = this.database.GetPropById(instance.PropId);
        if (prop == null)
        {
            this.LastMessage = $"配置 Prop 不存在：{instance.PropId}";
            return;
        }

        this.Move(runtimeId, new Vector3(prop.Position.X, prop.Position.Y, prop.Position.Z));
    }

    public void Move(string runtimeId, Vector3 position)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"找不到 Runtime Prop：{runtimeId}";
            return;
        }

        var success = this.brioAssemblyBridge.TryMoveProp(instance, position, out var reason);
        this.LastMessage = success ? reason : $"移动 Prop 失败：{reason}";
    }

    public void SavePositionToConfig(string runtimeId)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
            return;

        var prop = this.database.GetPropById(instance.PropId);
        if (prop == null)
        {
            this.LastMessage = $"配置 Prop 不存在：{instance.PropId}";
            return;
        }

        this.brioAssemblyBridge.RefreshProp(instance);
        prop.Position = new Vector3Data { X = instance.Position.X, Y = instance.Position.Y, Z = instance.Position.Z };
        this.database.Save();
        this.LastMessage = $"已保存 Runtime Prop 位置到配置：{prop.Id}";
    }

    public void ApplyModelPath(string runtimeId)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
            return;

        var prop = this.database.GetPropById(instance.PropId);
        if (prop != null)
            instance.ModelPath = prop.ModelPath;

        _ = this.propModelService.ApplyModelPath(instance, out var result);
        this.LastMessage = result;
    }

    public void DumpDrawObject(string runtimeId)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
            return;

        this.brioAssemblyBridge.RefreshProp(instance);
        this.propModelService.DumpDrawObject(instance);
        this.LastMessage = "已刷新 Prop DrawObject dump。";
    }

    public void DumpModelResource(string runtimeId)
    {
        var instance = this.Get(runtimeId);
        if (instance == null)
            return;

        this.brioAssemblyBridge.RefreshProp(instance);
        this.propModelService.DumpModelResource(instance);
        this.LastMessage = "已刷新 Prop 模型/资源 dump。";
    }

    public void Refresh()
    {
        foreach (var instance in this.props.Values)
            this.brioAssemblyBridge.RefreshProp(instance);
    }

    private bool TrySpawnRawMdlExperiment(CustomProp prop, string runtimeId, out RuntimePropInstance instance, out string reason)
    {
        if (!this.brioAssemblyBridge.TrySpawnProp(prop, runtimeId, true, out instance, out reason))
            return false;

        this.brioAssemblyBridge.RefreshProp(instance);
        instance.ObjectType = instance.CharacterObject?.GetType().FullName ?? "null";
        instance.IsCharacterClone = instance.ObjectType.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
                                    instance.ObjectType.Contains("Chara", StringComparison.OrdinalIgnoreCase) ||
                                    instance.ObjectType.Contains("Npc", StringComparison.OrdinalIgnoreCase);
        instance.IsBrioProp = false;
        instance.LastError = "这不是 Prop，只是 Character clone。Raw mdl path 模式当前未实现安全生成。";
        reason = $"Raw mdl path 模式未实现真实 Prop 生成。当前 CreateCharacter/SpawnFlags.Prop 结果：{instance.ObjectType}。这不是 Prop，只是 Character clone。";
        return false;
    }

    public void CleanupPropsForMissingConfigs()
    {
        var existing = this.database.Props.Select(prop => prop.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in this.props.Values.Where(instance => !existing.Contains(instance.PropId)).ToList())
            this.Despawn(instance.RuntimeId);
    }
}
