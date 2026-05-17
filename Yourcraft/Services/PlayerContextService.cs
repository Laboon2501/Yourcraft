using Dalamud.Plugin.Services;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class PlayerContextService
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;

    public PlayerContextService(IClientState clientState, IObjectTable objectTable)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
    }

    public Vector3? PlayerPosition => this.objectTable.LocalPlayer?.Position;

    public uint TerritoryType => this.clientState.TerritoryType;

    public void Update()
    {
    }
}
