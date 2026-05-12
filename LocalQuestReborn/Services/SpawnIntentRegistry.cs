using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed class SpawnIntentRegistry
{
    private readonly Dictionary<string, SpawnIntent> intents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SpawnIntent> GetAll()
        => this.intents.Values.OrderBy(intent => intent.NpcId, StringComparer.OrdinalIgnoreCase).ToList();

    public SpawnIntent? Get(string npcId)
        => this.intents.GetValueOrDefault(npcId);

    public int Count => this.intents.Count;

    public void MarkShouldSpawn(CustomNpc npc, string runtimeId = "")
    {
        var intent = this.GetOrCreate(npc.Id);
        intent.ShouldBeSpawned = true;
        intent.SuppressedUntilUserSpawn = false;
        intent.RespawnAfterGpose = npc.RespawnAfterGpose;
        intent.LastRuntimeId = string.IsNullOrWhiteSpace(runtimeId) ? intent.LastRuntimeId : runtimeId;
        intent.LastSpawnPosition = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);
        intent.LastAppearanceSource = npc.Appearance.SourceType.ToString();
        intent.AutoPlayAnimation = npc.AutoPlayDefaultAnimation && npc.DefaultAnimationId > 0;
        intent.LastDespawnReason = null;
    }

    public void MarkDespawned(string npcId, DespawnReason reason)
    {
        var intent = this.GetOrCreate(npcId);
        intent.LastDespawnReason = reason;
        if (reason == DespawnReason.UserRequested)
        {
            intent.ShouldBeSpawned = false;
            intent.SuppressedUntilUserSpawn = true;
        }
    }

    public void UpdateLastRuntime(CustomNpc npc, RuntimeActorInstance actor)
    {
        var intent = this.GetOrCreate(npc.Id);
        intent.LastRuntimeId = actor.RuntimeId;
        intent.LastSpawnPosition = actor.LastKnownPosition;
        intent.LastAppearanceSource = npc.Appearance.SourceType.ToString();
        intent.AutoPlayAnimation = npc.AutoPlayDefaultAnimation && npc.DefaultAnimationId > 0;
        intent.RespawnAfterGpose = npc.RespawnAfterGpose;
    }

    public void RemoveMissingNpcs(ISet<string> npcIds)
    {
        foreach (var npcId in this.intents.Keys.Where(id => !npcIds.Contains(id)).ToList())
            this.intents.Remove(npcId);
    }

    private SpawnIntent GetOrCreate(string npcId)
    {
        if (this.intents.TryGetValue(npcId, out var intent))
            return intent;

        intent = new SpawnIntent { NpcId = npcId };
        this.intents[npcId] = intent;
        return intent;
    }
}
