using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class ActorBubbleOverlayWindow : Window
{
    private readonly ActorBubbleService bubbleService;
    private readonly RealNpcSpawnService actorService;
    private readonly IGameGui gameGui;

    public ActorBubbleOverlayWindow(ActorBubbleService bubbleService, RealNpcSpawnService actorService, IGameGui gameGui)
        : base("Actor Bubbles##LocalQuestRebornActorBubbles", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground)
    {
        this.bubbleService = bubbleService;
        this.actorService = actorService;
        this.gameGui = gameGui;
        this.IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(viewport.Size, ImGuiCond.Always);
    }

    public override bool DrawConditions()
        => this.bubbleService.ActiveBubbles.Count > 0;

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        foreach (var bubble in this.bubbleService.ActiveBubbles.ToArray())
        {
            var actor = this.actorService.GetActor(bubble.RuntimeId);
            if (actor == null || !actor.IsValid)
                continue;

            var scaleY = actor.LastKnownScale.Y > 0.01f ? actor.LastKnownScale.Y : 1f;
            var worldPosition = actor.LastKnownPosition + new Vector3(0f, 2.15f * scaleY, 0f);
            if (!this.gameGui.WorldToScreen(worldPosition, out var screenPosition))
                continue;

            DrawBubble(drawList, screenPosition, bubble.Text);
        }
    }

    private static void DrawBubble(ImDrawListPtr drawList, Vector2 anchor, string text)
    {
        const float maxWidth = 320f;
        var textSize = ImGui.CalcTextSize(text, false, maxWidth);
        var padding = new Vector2(10f, 7f);
        var size = textSize + padding * 2f;
        var min = anchor - new Vector2(size.X * 0.5f, size.Y + 16f);
        var max = min + size;
        var bg = ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.06f, 0.84f));
        var border = ImGui.GetColorU32(new Vector4(1f, 0.88f, 0.55f, 0.92f));
        var fg = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));

        drawList.AddRectFilled(min, max, bg, 8f);
        drawList.AddRect(min, max, border, 8f, ImDrawFlags.None, 1.5f);
        drawList.AddTriangleFilled(
            new Vector2(anchor.X - 7f, max.Y - 1f),
            new Vector2(anchor.X + 7f, max.Y - 1f),
            new Vector2(anchor.X, max.Y + 8f),
            bg);
        drawList.AddText(min + padding, fg, text);
    }
}
