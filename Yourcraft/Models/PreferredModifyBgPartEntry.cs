namespace Yourcraft.Models;

public sealed class PreferredModifyBgPartSlot
{
    public uint TerritoryType { get; set; }

    public string SourceType { get; set; } = "Unknown";

    public string ResourcePath { get; set; } = string.Empty;

    public Vector3Data OriginalPosition { get; set; } = new();

    public string OriginalRotation { get; set; } = string.Empty;

    public Vector3Data OriginalScale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public string LayoutInstanceAddress { get; set; } = string.Empty;

    public string SharedGroupPath { get; set; } = string.Empty;

    public int ChildIndex { get; set; } = -1;

    public string StableKey { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

public sealed class PreferredModifyBgPartResourcePath
{
    public string ResourcePath { get; set; } = string.Empty;

    public uint TerritoryType { get; set; }

    public bool AppliesToCurrentTerritoryOnly { get; set; } = true;

    public string Note { get; set; } = string.Empty;
}
