using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class InteractionHintWindow : Window
{
    private readonly Configuration configuration;
    private readonly InteractionService interaction;

    public InteractionHintWindow(Configuration configuration, InteractionService interaction)
        : base("Yourcraft Interaction Hint##YourcraftHint", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.configuration = configuration;
        this.interaction = interaction;
        this.IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + viewport.WorkSize.Y - 300f),
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.72f);
    }

    public override bool DrawConditions()
        => this.configuration.ShowInteractionHint && this.interaction.CurrentNpc != null;

    public override void Draw()
    {
        var npc = this.interaction.CurrentNpc;
        if (npc == null)
            return;

        ImGui.TextColored(new Vector4(0.96f, 0.78f, 0.28f, 1f), Localization.T($"按 /lqr talk 与 {npc.Name} 交谈", $"Use /lqr talk to speak with {npc.Name}"));
    }
}
