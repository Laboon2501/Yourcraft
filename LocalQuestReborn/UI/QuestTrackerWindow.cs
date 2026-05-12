using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class QuestTrackerWindow : Window
{
    private readonly Configuration configuration;
    private readonly QuestStateService state;

    public QuestTrackerWindow(Configuration configuration, QuestStateService state)
        : base("任务追踪##Tracker", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.configuration = configuration;
        this.state = state;
        this.IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var position = this.configuration.TrackerPosition;
        if (position.X <= 0 || position.Y <= 0)
            position = new Vector2(viewport.WorkPos.X + viewport.WorkSize.X - 390f, viewport.WorkPos.Y + 250f);

        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.48f);
    }

    public override bool DrawConditions()
        => this.configuration.ShowTracker && this.state.GetTrackedQuests().Any();

    public override void Draw()
    {
        ImGui.PushTextWrapPos(340f);

        foreach (var (quest, progress) in this.state.GetTrackedQuests())
        {
            ImGui.TextColored(new Vector4(0.96f, 0.78f, 0.28f, 1f), quest.Title);

            if (progress.Status == QuestStatus.ReadyToComplete)
            {
                ImGui.BulletText("返回交付 NPC");
            }
            else
            {
                foreach (var objective in quest.Objectives)
                {
                    var done = progress.CompletedObjectiveIds.Contains(objective.Id);
                    var current = this.state.GetCurrentObjective(quest)?.Id == objective.Id;
                    var prefix = done ? "✓" : current ? "◆" : "◇";
                    ImGui.BulletText($"{prefix} {objective.Description}");
                }
            }

            ImGui.Spacing();
        }

        ImGui.PopTextWrapPos();
    }
}
