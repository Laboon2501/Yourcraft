using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class RuntimeActorRegistry
{
    private readonly Dictionary<string, RuntimeActorInstance> instances = new(StringComparer.OrdinalIgnoreCase);

    public void Add(RuntimeActorInstance instance)
        => this.instances[instance.RuntimeId] = instance;

    public bool Remove(string runtimeId)
        => this.instances.Remove(runtimeId);

    public RuntimeActorInstance? GetByRuntimeId(string runtimeId)
        => this.instances.GetValueOrDefault(runtimeId);

    public IReadOnlyList<RuntimeActorInstance> GetByNpcId(string npcId)
        => this.instances.Values
            .Where(instance => string.Equals(instance.NpcId, npcId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(instance => instance.SpawnTime)
            .ToList();

    public IReadOnlyList<RuntimeActorInstance> GetAll()
        => this.instances.Values
            .OrderByDescending(instance => instance.SpawnTime)
            .ToList();

    public RuntimeActorInstance? GetLatestByNpcId(string npcId)
        => this.instances.Values
            .Where(instance => string.Equals(instance.NpcId, npcId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.SpawnTime)
            .FirstOrDefault();

    public void Clear()
        => this.instances.Clear();
}
