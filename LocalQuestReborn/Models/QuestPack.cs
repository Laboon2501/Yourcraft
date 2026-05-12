namespace LocalQuestReborn.Models;

public sealed class QuestPack
{
    public string PackId { get; set; } = string.Empty;

    public string PackName { get; set; } = "未命名任务包";

    public string Author { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0.0";

    public string Description { get; set; } = string.Empty;

    public List<CustomNpc> Npcs { get; set; } = [];

    public List<CustomQuest> Quests { get; set; } = [];
}
