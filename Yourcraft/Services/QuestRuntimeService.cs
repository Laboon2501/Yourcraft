using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class QuestRuntimeService
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly QuestDatabase database;

    public QuestRuntimeService(IClientState clientState, IObjectTable objectTable, QuestDatabase database)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.database = database;
    }

    public CustomNpc? NearbyNpc { get; private set; }

    public Vector3? PlayerPosition => this.objectTable.LocalPlayer?.Position;

    public uint TerritoryType => this.clientState.TerritoryType;

    public void Update()
    {
        this.NearbyNpc = this.FindNearbyNpc();
    }

    public bool CompleteTalkObjectiveForNpc(CustomNpc npc, bool forceRealNpcMatch = false)
        => false;

    public bool IsNpcNearby(CustomNpc npc)
    {
        var player = this.PlayerPosition;
        if (player == null || this.TerritoryType != npc.TerritoryType)
            return false;

        return CalculateXZDistance(player.Value, npc.Position) <= npc.InteractRadius;
    }

    public IEnumerable<VirtualNpcDistance> GetCurrentTerritoryNpcs()
    {
        var player = this.PlayerPosition;
        foreach (var npc in this.database.Npcs.Where(npc => npc.TerritoryType == this.TerritoryType))
        {
            float? distance = player == null ? null : CalculateXZDistance(player.Value, npc.Position);
            yield return new VirtualNpcDistance(
                npc,
                distance,
                distance != null && distance.Value <= npc.InteractRadius);
        }
    }

    private CustomNpc? FindNearbyNpc()
    {
        return this.database.Npcs
            .Where(this.IsNpcNearby)
            .OrderBy(npc => CalculateXZDistance(this.PlayerPosition!.Value, npc.Position))
            .FirstOrDefault();
    }

    public static float CalculateXZDistance(Vector3 playerPosition, Vector3Data objectivePosition)
    {
        var deltaX = playerPosition.X - objectivePosition.X;
        var deltaZ = playerPosition.Z - objectivePosition.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}

public sealed record VirtualNpcDistance(
    CustomNpc Npc,
    float? XZDistance,
    bool IsInteractable);
