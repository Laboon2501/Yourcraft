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
            ImGui.TextDisabled(T("没有可用的选择目标。", "No active picker target."));
            if (ImGui.Button(T("关闭", "Close")))
                this.IsOpen = false;
            return;
        }

        ImGui.TextUnformatted(request.Title);

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
        if (!ImGui.BeginTable("ActionTimelinePickerTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 360f)))
            return;

        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(T("名称", "Name"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("命令", "Command"), ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn(T("来源", "Source"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(T("行", "Row"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn(T("循环", "Loop"), ImGuiTableColumnFlags.WidthFixed, 60f);
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
                ImGui.SetTooltip($"ActionTimelineId={entry.ActionTimelineId}\nKey={entry.Key}\nPurpose={entry.Purpose}");

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
            ImGui.TextUnformatted(entry.IsLoopCandidate ? T("是", "yes") : string.Empty);
        }

        ImGui.EndTable();
    }

    private static string T(string chinese, string english) => Localization.T(chinese, english);
}
