namespace Yourcraft.Models;

public sealed record SceneEditorTransformUndoEntry(
    string RuntimeId,
    SceneEditableKind Kind,
    string DisplayName,
    WorldTransform Before,
    WorldTransform After,
    DateTime Time,
    string Source);
