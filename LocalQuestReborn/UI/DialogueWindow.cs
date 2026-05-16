using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class DialogueWindow : Window
{
    private readonly QuestStateService state;
    private readonly InteractionService interaction;
    private int lineIndex;
    private string? lastQuestId;

    public DialogueWindow(QuestStateService state, InteractionService interaction)
        : base("Yourcraft Dialogue##YourcraftDialogue", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse)
    {
        this.state = state;
        this.interaction = interaction;
        this.IsOpen = false;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var width = MathF.Min(900f, viewport.WorkSize.X - 80f);
        var height = 210f;
        var position = new Vector2(
            viewport.WorkPos.X + (viewport.WorkSize.X - width) * 0.5f,
            viewport.WorkPos.Y + viewport.WorkSize.Y - height - 46f);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.86f);
    }

    public override bool DrawConditions()
    {
        this.IsOpen = this.interaction.HasPendingDialogue;
        if (!this.IsOpen)
        {
            this.lineIndex = 0;
            this.lastQuestId = null;
        }

        return this.IsOpen;
    }

    public override void Draw()
    {
        var quest = this.interaction.CurrentQuest;
        var npc = this.interaction.CurrentNpc;
        if (quest == null || npc == null)
            return;

        if (this.lastQuestId != quest.Id)
        {
            this.lineIndex = 0;
            this.lastQuestId = quest.Id;
        }

        var status = this.state.GetStatus(quest);
        var lines = GetDialogueLines(quest, status);
        this.lineIndex = Math.Clamp(this.lineIndex, 0, Math.Max(lines.Count - 1, 0));
        var isLastLine = this.lineIndex >= lines.Count - 1;

        ImGui.TextColored(new Vector4(0.96f, 0.78f, 0.28f, 1f), npc.Name);
        ImGui.SameLine();
        ImGui.TextUnformatted($" - {quest.Title}");
        ImGui.Separator();

        ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - 28f);
        ImGui.TextWrapped(lines.Count == 0 ? "..." : lines[this.lineIndex]);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - 42f);
        if (!isLastLine)
        {
            if (ImGui.Button(Localization.T("继续", "Continue")))
                this.lineIndex++;
        }
        else if (status == QuestStatus.NotAccepted)
        {
            if (ImGui.Button(quest.AcceptText))
            {
                this.state.AcceptQuest(quest);
                this.interaction.CloseDialogue();
            }
        }
        else if (status == QuestStatus.ReadyToComplete)
        {
            if (ImGui.Button(Localization.T("完成任务", "Complete Quest")))
            {
                this.state.CompleteQuest(quest);
                this.interaction.CloseDialogue();
            }
        }
        else if (ImGui.Button(Localization.T("关闭", "Close")))
        {
            this.interaction.CloseDialogue();
        }

        ImGui.SameLine();
        if (ImGui.Button(Localization.T("关闭", "Close") + "##DialogueClose"))
            this.interaction.CloseDialogue();
    }

    private static List<string> GetDialogueLines(CustomQuest quest, QuestStatus status)
    {
        return status switch
        {
            QuestStatus.NotAccepted => quest.StartDialogue.Count == 0 ? ["……"] : quest.StartDialogue,
            QuestStatus.ReadyToComplete => quest.CompleteDialogue.Count == 0 ? ["任务已经完成，回来交付吧。"] : quest.CompleteDialogue,
            QuestStatus.Completed => quest.CompleteDialogue.Count == 0 ? [quest.RewardsText] : quest.CompleteDialogue,
            _ => quest.ProgressDialogue.Count == 0 ? ["任务正在进行中。"] : quest.ProgressDialogue,
        };
    }
}
