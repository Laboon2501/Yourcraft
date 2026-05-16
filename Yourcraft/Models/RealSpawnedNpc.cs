using System.Numerics;

namespace Yourcraft.Models;

public sealed class RealSpawnedNpc
{
    public string NpcId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public object? Character { get; set; }

    public string ObjectIndex { get; set; } = "不可用";

    public string Address { get; set; } = "不可用";

    public Vector3 LastKnownPosition { get; set; }

    public DateTime SpawnedAt { get; set; } = DateTime.Now;

    public bool IsValid { get; set; }

    public string Source { get; set; } = "未知";

    public string ExpectedName { get; set; } = string.Empty;

    public string CurrentNativeName { get; set; } = "不可用";

    public bool NativeNameSet { get; set; }

    public string LastNameError { get; set; } = string.Empty;
}
