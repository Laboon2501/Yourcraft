using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class SceneEditorSelectionService
{
    private readonly IPluginLog log;

    public SceneEditorSelectionService(IPluginLog log)
        => this.log = log;

    public string SelectedRuntimeId { get; private set; } = string.Empty;

    public SceneEditableKind? SelectedKind { get; private set; }

    public SceneEditorSelectionSource LastSource { get; private set; }

    public uint Generation { get; private set; }

    public bool HasSelection => this.SelectedKind.HasValue && !string.IsNullOrWhiteSpace(this.SelectedRuntimeId);

    public bool IsSelected(SceneEditableKind kind, string runtimeId)
        => this.SelectedKind == kind && string.Equals(this.SelectedRuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase);

    public bool Select(SceneEditableKind kind, string runtimeId, SceneEditorSelectionSource source)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return this.Clear(source);

        if (this.IsSelected(kind, runtimeId))
            return false;

        this.SelectedKind = kind;
        this.SelectedRuntimeId = runtimeId;
        this.LastSource = source;
        this.Generation++;
        this.log.Information("[SceneEditor] Selection changed source={Source} kind={Kind} id={Id}", source, kind, runtimeId);
        return true;
    }

    public bool Clear(SceneEditorSelectionSource source)
    {
        if (!this.HasSelection)
            return false;

        this.SelectedKind = null;
        this.SelectedRuntimeId = string.Empty;
        this.LastSource = source;
        this.Generation++;
        this.log.Information("[SceneEditor] Selection cleared source={Source}", source);
        return true;
    }
}
