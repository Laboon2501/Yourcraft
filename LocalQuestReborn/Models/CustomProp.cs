namespace LocalQuestReborn.Models;

public sealed class CustomProp
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "本地场景物体";

    public ushort TerritoryType { get; set; }

    public Vector3Data Position { get; set; } = new();

    public float Rotation { get; set; }

    public float Scale { get; set; } = 1f;

    public string ModelPath { get; set; } = "bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl";

    public bool Visible { get; set; } = true;

    public string Notes { get; set; } = string.Empty;
}
