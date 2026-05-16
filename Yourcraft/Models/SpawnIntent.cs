using System.Numerics;

namespace Yourcraft.Models;

public sealed class SpawnIntent
{
    public string NpcId { get; set; } = string.Empty;

    public bool ShouldBeSpawned { get; set; }

    public bool SuppressedUntilUserSpawn { get; set; }

    public bool RespawnAfterGpose { get; set; } = true;

    public string LastRuntimeId { get; set; } = string.Empty;

    public Vector3 LastSpawnPosition { get; set; }

    public string LastAppearanceSource { get; set; } = string.Empty;

    public bool AutoPlayAnimation { get; set; }

    public DespawnReason? LastDespawnReason { get; set; }
}
