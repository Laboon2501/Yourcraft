namespace Yourcraft.Models;

[Flags]
public enum SceneEditorTransformComponents
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Scale = 4,
    All = Position | Rotation | Scale,
}
