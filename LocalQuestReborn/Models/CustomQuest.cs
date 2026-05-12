namespace LocalQuestReborn.Models;

public sealed class CustomQuest
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = "未命名任务";

    public string GiverNpcId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public List<string> StartDialogue { get; set; } = [];

    public string AcceptText { get; set; } = "接受任务";

    public List<QuestObjective> Objectives { get; set; } = [];

    public List<string> ProgressDialogue { get; set; } = [];

    public List<string> CompleteDialogue { get; set; } = [];

    public string RewardsText { get; set; } = string.Empty;
}
