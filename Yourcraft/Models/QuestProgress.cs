namespace Yourcraft.Models;

public sealed class QuestProgress
{
    public string QuestId { get; set; } = string.Empty;

    public QuestStatus Status { get; set; } = QuestStatus.NotAccepted;

    public int CurrentObjectiveIndex { get; set; }

    public List<string> CompletedObjectiveIds { get; set; } = [];
}
