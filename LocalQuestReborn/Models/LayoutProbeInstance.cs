using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class LayoutProbeInstance
{
    public int Index { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string InstanceType { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string LayerAddress { get; set; } = string.Empty;

    public Vector3 Position { get; set; }

    public string Rotation { get; set; } = "未读取";

    public Vector3 Scale { get; set; } = Vector3.One;

    public string ResourcePath { get; set; } = "未读取";

    public bool Visible { get; set; } = true;

    public string LayerId { get; set; } = "未读取";

    public string GroupId { get; set; } = "未读取";

    public float DistanceToPlayer { get; set; }

    public string Source { get; set; } = string.Empty;

    public string DebugInfo { get; set; } = string.Empty;
}
