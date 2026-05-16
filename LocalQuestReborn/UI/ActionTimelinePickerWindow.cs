using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class ActionTimelinePickerWindow : Window
{
    private readonly ActorAnimationPickerService picker;

    public ActionTimelinePickerWindow(ActorAnimationPickerService picker)
        : base("Yourcraft Action Picker##YourcraftActionTimelinePicker")
    {
        this.picker = picker;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760f, 500f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var request = this.picker.CurrentRequest;
        if (request == null)
        {
            ImGui.TextDisabled(T("没有活动的选择目标。", "No active picker target."));
            if (ImGui.Button(T("关闭", "Close")))
                this.IsOpen = false;
            return;
        }

        ImGui.TextUnformatted(request.Title);
        ImGui.TextDisabled(T(
            "选择后会写入输入框；仍可手动输入任意 ActionTimelineId。",
            "Selecting an entry writes it to the field; manual ActionTimelineId input is still supported."));

        var mode = request.PickerMode;
        if (ImGui.BeginCombo(T("列表模式", "List Mode"), mode.ToString()))
        {
            foreach (var value in Enum.GetValues<ActorAnimationPickerMode>())
            {
                var selected = mode == value;
                if (ImGui.Selectable(value.ToString(), selected))
                {
                    request.PickerMode = value;
                    mode = value;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var search = this.picker.SearchText;
        if (ImGui.InputText(T("搜索", "Search"), ref search, 160))
            this.picker.SearchText = search;
        ImGui.SameLine();
        if (ImGui.Button(T("刷新列表", "Refresh")))
            this.picker.Refresh();
        ImGui.SameLine();
        if (ImGui.Button(T("关闭", "Close")))
        {
            this.picker.Close();
            this.IsOpen = false;
            return;
        }

        ImGui.TextWrapped(this.picker.LastResult);
        ImGui.Separator();

        var entries = this.picker.Search();
        ImGui.TextDisabled(T(
            $"显示 {entries.Count} 条。ExpressionCandidates 如果无法可靠过滤，会显示候选和可搜索 ActionTimeline。",
            $"Showing {entries.Count} entries. Expression candidates may include searchable ActionTimelines when filtering is uncertain."));

        if (!ImGui.BeginTable("ActionTimelinePickerTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 360f)))
            return;

        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Row", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Loop?", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selectableLabel = $"{entry.ActionTimelineId}##selectActionTimeline{entry.SourceType}{entry.SourceRowId}{entry.ActionTimelineId}{entry.Purpose}";
            if (ImGui.Selectable(selectableLabel, false, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (this.picker.Apply(entry.ActionTimelineId))
                {
                    this.picker.Close();
                    this.IsOpen = false;
                    ImGui.EndTable();
                    return;
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"ActionTimelineId={entry.ActionTimelineId}\nKey={entry.Key}\nPurpose={entry.Purpose}\n仍可手动输入未列出的 ID。");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Name) ? "(unnamed)" : entry.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Command);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.SourceType);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.SourceRowId.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Slot);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.IsLoopCandidate ? "yes" : string.Empty);
        }

        ImGui.EndTable();
    }

    private static string T(string chinese, string english) => Localization.T(chinese, english);
}
