using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class InteractionService
{
    private readonly QuestDatabase database;
    private readonly QuestRuntimeService runtime;
    private readonly QuestStateService state;
    private readonly RealNpcSpawnService realNpcSpawn;
    private readonly EventNpcHostService eventNpcHostService;
    private CustomNpc? forcedNpc;

    public InteractionService(QuestDatabase database, QuestRuntimeService runtime, QuestStateService state, RealNpcSpawnService realNpcSpawn, EventNpcHostService eventNpcHostService)
    {
        this.database = database;
        this.runtime = runtime;
        this.state = state;
        this.realNpcSpawn = realNpcSpawn;
        this.eventNpcHostService = eventNpcHostService;
    }

    public CustomNpc? CurrentNpc => this.forcedNpc ?? this.eventNpcHostService.TryGetHostByTarget() ?? this.realNpcSpawn.FindInteractableNpc(this.database.Npcs, this.runtime.PlayerPosition, this.runtime.TerritoryType) ?? this.runtime.NearbyNpc;

    public CustomQuest? CurrentQuest { get; private set; }

    public bool HasPendingDialogue { get; private set; }

    public void Talk()
    {
        var hostNpc = this.forcedNpc == null
            ? this.eventNpcHostService.TryGetHostByTarget()
            : null;
        var realNpc = this.forcedNpc == null
            ? this.realNpcSpawn.FindInteractableNpc(this.database.Npcs, this.runtime.PlayerPosition, this.runtime.TerritoryType)
            : null;
        var npc = this.forcedNpc ?? hostNpc ?? realNpc ?? this.runtime.NearbyNpc;
        if (npc == null)
            return;

        this.runtime.CompleteTalkObjectiveForNpc(npc, realNpc != null || hostNpc != null);
        if (hostNpc != null)
            this.eventNpcHostService.MarkHostInteraction(hostNpc, "/godmode talk");
        this.CurrentQuest = this.ResolveQuestForNpc(npc);
        this.HasPendingDialogue = this.CurrentQuest != null;
    }

    public void CloseDialogue()
    {
        this.HasPendingDialogue = false;
        this.CurrentQuest = null;
        this.forcedNpc = null;
    }

    public void OpenDialogue(CustomQuest quest, CustomNpc? npc = null)
    {
        this.CurrentQuest = quest;
        this.forcedNpc = npc ?? this.database.GetNpcById(quest.GiverNpcId) ?? this.runtime.NearbyNpc;
        this.HasPendingDialogue = this.CurrentQuest != null;
    }

    public void OpenDialogueForNpc(CustomNpc npc, string source = "UI 测试打开本地对话")
    {
        this.CurrentQuest = this.ResolveQuestForNpc(npc);
        this.forcedNpc = npc;
        this.HasPendingDialogue = this.CurrentQuest != null;
        if (this.HasPendingDialogue)
            this.eventNpcHostService.MarkHostInteraction(npc, source);
    }

    private CustomQuest? ResolveQuestForNpc(CustomNpc npc)
    {
        var activeTalkQuest = this.state.GetActiveQuests()
            .Select(pair => pair.Quest)
            .FirstOrDefault(quest =>
            {
                var objective = this.state.GetCurrentObjective(quest);
                return objective?.Type == QuestObjectiveType.TalkToNpc
                       && string.Equals(objective.TargetNpcId, npc.Id, StringComparison.OrdinalIgnoreCase);
            });

        if (activeTalkQuest != null)
            return activeTalkQuest;

        var availableQuest = this.database.Quests.FirstOrDefault(quest =>
            string.Equals(quest.GiverNpcId, npc.Id, StringComparison.OrdinalIgnoreCase)
            && this.state.GetStatus(quest) == QuestStatus.NotAccepted);

        if (availableQuest != null)
            return availableQuest;

        return this.database.Quests.FirstOrDefault(quest =>
            string.Equals(quest.GiverNpcId, npc.Id, StringComparison.OrdinalIgnoreCase));
    }
}
