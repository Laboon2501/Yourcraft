using System.Numerics;

namespace LocalQuestReborn.Models;

public sealed class MeddleStyleSceneObject
{
    public int Index { get; set; }

    public string ObjectType { get; set; } = string.Empty;

    public string ObjectName { get; set; } = string.Empty;

    public string ObjectAddress { get; set; } = string.Empty;

    public string DrawObjectAddress { get; set; } = string.Empty;

    public string DrawObjectType { get; set; } = string.Empty;

    public string ResourcePath { get; set; } = "未读取";

    public string ModelPath { get; set; } = "未读取";

    public Vector3 Position { get; set; }

    public string Rotation { get; set; } = "未读取";

    public Vector3 Scale { get; set; } = Vector3.One;

    public float DistanceToPlayer { get; set; }

    public bool IsReadyToDraw { get; set; }

    public bool IsVisible { get; set; }

    public string Source { get; set; } = string.Empty;

    public string DebugInfo { get; set; } = string.Empty;
}
