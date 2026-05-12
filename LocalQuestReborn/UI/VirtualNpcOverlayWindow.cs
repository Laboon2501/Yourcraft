using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class VirtualNpcOverlayWindow : Window
{
    private readonly QuestRuntimeService runtime;

    public VirtualNpcOverlayWindow(QuestRuntimeService runtime)
        : base("本地 NPC 站牌##VirtualNpcOverlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.runtime = runtime;
        this.IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            new Vector2(viewport.WorkPos.X + viewport.WorkSize.X - 360f, viewport.WorkPos.Y + viewport.WorkSize.Y * 0.52f),
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.72f);
    }

    public override bool DrawConditions()
        => this.runtime.GetCurrentTerritoryNpcs().Any();

    public override void Draw()
    {
        var npcs = this.runtime.GetCurrentTerritoryNpcs()
            .OrderBy(item => item.XZDistance ?? float.MaxValue)
            .Take(8)
            .ToList();
        var nearestInteractable = this.runtime.NearbyNpc;

        ImGui.PushTextWrapPos(310f);
        ImGui.TextColored(new Vector4(0.96f, 0.78f, 0.28f, 1f), "本地 NPC");

        foreach (var item in npcs)
        {
            var npc = item.Npc;
            var isNearestInteractable = nearestInteractable?.Id == npc.Id && item.IsInteractable;
            var isNear = item.XZDistance != null && item.XZDistance.Value <= 20f;
            var color = isNearestInteractable
                ? new Vector4(0.45f, 1f, 0.55f, 1f)
                : isNear
                    ? new Vector4(0.96f, 0.78f, 0.28f, 1f)
                    : new Vector4(0.78f, 0.78f, 0.78f, 1f);

            ImGui.Separator();
            ImGui.TextColored(color, isNearestInteractable ? "◆" : "●");
            ImGui.SameLine();
            ImGui.TextColored(color, npc.Name);

            var distanceText = item.XZDistance == null ? "距离：不可用" : $"距离：{item.XZDistance.Value:F1}m";
            if (isNear || item.IsInteractable)
            {
                ImGui.TextUnformatted(distanceText);
                if (isNearestInteractable)
                    ImGui.TextColored(new Vector4(0.45f, 1f, 0.55f, 1f), $"/lqr talk 与 {npc.Name} 交谈");
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(distanceText);
            }
        }

        ImGui.PopTextWrapPos();
    }
}
