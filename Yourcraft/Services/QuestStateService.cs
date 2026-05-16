using Dalamud.Plugin;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class QuestStateService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly QuestDatabase database;

    public QuestStateService(IDalamudPluginInterface pluginInterface, Configuration configuration, QuestDatabase database)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.database = database;
    }

    public IReadOnlyDictionary<string, QuestProgress> Progresses => this.configuration.QuestProgresses;

    public QuestProgress GetOrCreateProgress(CustomQuest quest)
    {
        if (!this.configuration.QuestProgresses.TryGetValue(quest.Id, out var progress))
        {
            progress = new QuestProgress { QuestId = quest.Id };
            this.configuration.QuestProgresses[quest.Id] = progress;
        }

        return progress;
    }

    public QuestStatus GetStatus(CustomQuest quest)
        => this.GetOrCreateProgress(quest).Status;

    public void AcceptQuest(CustomQuest quest)
        => this.StartQuest(quest);

    public void StartQuest(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        progress.Status = QuestStatus.InProgress;
        progress.CurrentObjectiveIndex = this.FindFirstIncompleteObjectiveIndex(quest, progress);
        this.Save();
    }

    public void SetStatus(CustomQuest quest, QuestStatus status)
    {
        var progress = this.GetOrCreateProgress(quest);
        progress.Status = status;
        if (status == QuestStatus.NotAccepted)
        {
            progress.CurrentObjectiveIndex = 0;
            progress.CompletedObjectiveIds.Clear();
        }
        else if (status == QuestStatus.Completed)
        {
            progress.CurrentObjectiveIndex = quest.Objectives.Count;
            progress.CompletedObjectiveIds = quest.Objectives.Select(objective => objective.Id).ToList();
        }
        else if (status == QuestStatus.InProgress)
        {
            progress.CurrentObjectiveIndex = this.FindFirstIncompleteObjectiveIndex(quest, progress);
        }

        this.Save();
    }

    public bool CompleteCurrentObjective(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        if (progress.Status != QuestStatus.InProgress)
            return false;

        var objective = this.GetCurrentObjective(quest);
        if (objective == null)
        {
            this.CompleteQuest(quest);
            return true;
        }

        if (!progress.CompletedObjectiveIds.Contains(objective.Id))
            progress.CompletedObjectiveIds.Add(objective.Id);

        progress.CurrentObjectiveIndex++;
        if (progress.CurrentObjectiveIndex >= quest.Objectives.Count)
            progress.Status = QuestStatus.ReadyToComplete;

        this.Save();
        return true;
    }

    public void CompleteQuest(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        progress.Status = QuestStatus.Completed;
        progress.CurrentObjectiveIndex = quest.Objectives.Count;
        progress.CompletedObjectiveIds = quest.Objectives.Select(objective => objective.Id).ToList();
        this.Save();
    }

    public void ResetQuest(CustomQuest quest)
    {
        this.configuration.QuestProgresses.Remove(quest.Id);
        this.Save();
    }

    public void SetCurrentObjective(CustomQuest quest, int objectiveIndex)
    {
        var progress = this.GetOrCreateProgress(quest);
        progress.Status = QuestStatus.InProgress;
        progress.CurrentObjectiveIndex = Math.Clamp(objectiveIndex, 0, Math.Max(quest.Objectives.Count - 1, 0));
        this.Save();
    }

    public void UncompleteCurrentObjective(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        var objective = this.GetCurrentObjective(quest);
        if (objective != null)
            progress.CompletedObjectiveIds.Remove(objective.Id);

        progress.Status = QuestStatus.InProgress;
        this.Save();
    }

    public void CompleteAllPreviousObjectives(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        for (var index = 0; index < Math.Min(progress.CurrentObjectiveIndex, quest.Objectives.Count); index++)
        {
            var id = quest.Objectives[index].Id;
            if (!progress.CompletedObjectiveIds.Contains(id))
                progress.CompletedObjectiveIds.Add(id);
        }

        this.Save();
    }

    public QuestObjective? GetCurrentObjective(CustomQuest quest)
    {
        var progress = this.GetOrCreateProgress(quest);
        if (progress.Status != QuestStatus.InProgress)
            return null;

        if (progress.CurrentObjectiveIndex < 0 || progress.CurrentObjectiveIndex >= quest.Objectives.Count)
            return null;

        return quest.Objectives[progress.CurrentObjectiveIndex];
    }

    public IEnumerable<(CustomQuest Quest, QuestProgress Progress)> GetActiveQuests()
    {
        foreach (var quest in this.database.Quests)
        {
            var progress = this.GetOrCreateProgress(quest);
            if (progress.Status == QuestStatus.InProgress)
                yield return (quest, progress);
        }
    }

    public IEnumerable<(CustomQuest Quest, QuestProgress Progress)> GetTrackedQuests()
    {
        foreach (var quest in this.database.Quests)
        {
            var progress = this.GetOrCreateProgress(quest);
            if (progress.Status is QuestStatus.InProgress or QuestStatus.ReadyToComplete)
                yield return (quest, progress);
        }
    }

    public void Reset()
    {
        this.configuration.QuestProgresses.Clear();
        this.Save();
    }

    public void Save()
        => this.pluginInterface.SavePluginConfig(this.configuration);

    private int FindFirstIncompleteObjectiveIndex(CustomQuest quest, QuestProgress progress)
    {
        for (var index = 0; index < quest.Objectives.Count; index++)
        {
            if (!progress.CompletedObjectiveIds.Contains(quest.Objectives[index].Id))
                return index;
        }

        return Math.Max(quest.Objectives.Count - 1, 0);
    }
}
