using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class SceneEditorUndoService
{
    private const int MaxEntries = 64;

    private readonly Stack<SceneEditorTransformUndoEntry> entries = new();
    private readonly IPluginLog log;

    public SceneEditorUndoService(IPluginLog log)
        => this.log = log;

    public SceneEditorTransformUndoEntry? Peek
        => this.entries.Count == 0 ? null : this.entries.Peek();

    public bool HasUndo => this.entries.Count > 0;

    public string LastStatus { get; private set; } = "No Scene Editor undo yet.";

    public void Push(
        SceneEditableKind kind,
        string runtimeId,
        string displayName,
        WorldTransform before,
        WorldTransform after,
        string source)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return;

        if (NearlyEqual(before, after))
            return;

        this.entries.Push(new SceneEditorTransformUndoEntry(
            runtimeId,
            kind,
            displayName,
            before,
            after,
            DateTime.UtcNow,
            source));

        while (this.entries.Count > MaxEntries)
        {
            var keep = this.entries.Take(MaxEntries).Reverse().ToArray();
            this.entries.Clear();
            foreach (var item in keep)
                this.entries.Push(item);
        }

        this.LastStatus = $"Undo ready: {source} {displayName}";
        this.log.Debug("[SceneEditor] UndoPush kind={Kind} id={Id} source={Source}", kind, runtimeId, source);
    }

    public bool TryApplyUndo(Func<SceneEditorTransformUndoEntry, bool> apply, out string message)
    {
        if (this.entries.Count == 0)
        {
            message = "No Scene Editor transform undo entry.";
            this.LastStatus = message;
            return false;
        }

        var entry = this.entries.Peek();
        if (!apply(entry))
        {
            message = $"Undo skipped: object invalid or no longer editable ({entry.DisplayName}).";
            this.LastStatus = message;
            this.log.Debug("[SceneEditor] UndoSkipped kind={Kind} id={Id} reason=invalid", entry.Kind, entry.RuntimeId);
            return false;
        }

        this.entries.Pop();
        message = $"Undid {entry.Source}: {entry.DisplayName}";
        this.LastStatus = message;
        this.log.Information("[SceneEditor] UndoApply kind={Kind} id={Id} source={Source}", entry.Kind, entry.RuntimeId, entry.Source);
        return true;
    }

    private static bool NearlyEqual(WorldTransform left, WorldTransform right)
        => NearlyEqual(left.WorldPosition, right.WorldPosition, 0.0005f) &&
           NearlyEqual(left.WorldEulerRadians, right.WorldEulerRadians, 0.0005f) &&
           NearlyEqual(left.WorldScale, right.WorldScale, 0.0005f);

    private static bool NearlyEqual(Vector3 left, Vector3 right, float epsilon)
        => Vector3.DistanceSquared(left, right) <= epsilon * epsilon;
}
