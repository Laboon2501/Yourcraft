using Dalamud.Configuration;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Dictionary<string, QuestProgress> QuestProgresses { get; set; } = [];

    public bool ShowTracker { get; set; } = true;

    public bool ShowInteractionHint { get; set; } = true;

    public Vector2 TrackerPosition { get; set; } = new(1420f, 340f);

    public List<ProtectedBgPartSlot> ProtectedBgPartSlots { get; set; } = [];

    public List<ProtectedBgPartResourcePath> ProtectedBgPartResourcePaths { get; set; } = [];
}
