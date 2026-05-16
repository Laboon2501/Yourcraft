namespace Yourcraft.Models;

public enum ActorLifecycleState
{
    ConfigOnly,
    SpawnPending,
    Spawning,
    BindingRuntime,
    Spawned,
    Ready,
    Despawned,
    Failed,
    SpawnFailed,
}
