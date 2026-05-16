using System.Numerics;

namespace Yourcraft.Models;

public sealed class SceneEditorInputState
{
    public SceneEditorInputInteractionState State { get; private set; }

    public string HoveredMarkerId { get; private set; } = string.Empty;

    public string ActiveMarkerId { get; private set; } = string.Empty;

    public SceneEditorGizmoAxis HoveredGizmoAxis { get; private set; }

    public SceneEditorGizmoAxis ActiveGizmoAxis { get; private set; }

    public SceneEditorGizmoMode ActiveGizmoMode { get; private set; } = SceneEditorGizmoMode.Select;

    public Vector2 DragStartMousePos { get; private set; }

    public Vector2 DragCurrentMousePos { get; private set; }

    public WorldTransform DragStartWorldTransform { get; private set; }

    public bool IsCapturingSceneInput => this.State == SceneEditorInputInteractionState.GizmoDragging;

    public void SetIdle()
    {
        if (this.State == SceneEditorInputInteractionState.GizmoDragging)
            return;

        this.State = SceneEditorInputInteractionState.Idle;
        this.HoveredMarkerId = string.Empty;
        this.ActiveMarkerId = string.Empty;
        this.HoveredGizmoAxis = SceneEditorGizmoAxis.None;
    }

    public void SetMarkerHover(string markerId)
    {
        if (this.State == SceneEditorInputInteractionState.GizmoDragging)
            return;

        this.State = SceneEditorInputInteractionState.MarkerHover;
        this.HoveredMarkerId = markerId;
        this.HoveredGizmoAxis = SceneEditorGizmoAxis.None;
    }

    public void SetMarkerPressed(string markerId)
    {
        this.State = SceneEditorInputInteractionState.MarkerPressed;
        this.HoveredMarkerId = markerId;
        this.ActiveMarkerId = markerId;
        this.HoveredGizmoAxis = SceneEditorGizmoAxis.None;
    }

    public void SetGizmoHover(SceneEditorGizmoAxis axis)
    {
        if (this.State == SceneEditorInputInteractionState.GizmoDragging)
            return;

        this.State = SceneEditorInputInteractionState.GizmoHover;
        this.HoveredMarkerId = string.Empty;
        this.HoveredGizmoAxis = axis;
    }

    public void BeginGizmoDrag(
        SceneEditorGizmoMode mode,
        SceneEditorGizmoAxis axis,
        Vector2 mousePosition,
        WorldTransform startTransform)
    {
        this.State = SceneEditorInputInteractionState.GizmoDragging;
        this.ActiveGizmoMode = mode;
        this.ActiveGizmoAxis = axis;
        this.HoveredGizmoAxis = axis;
        this.DragStartMousePos = mousePosition;
        this.DragCurrentMousePos = mousePosition;
        this.DragStartWorldTransform = startTransform;
    }

    public void UpdateDrag(Vector2 mousePosition)
        => this.DragCurrentMousePos = mousePosition;

    public void EndGizmoDrag()
    {
        this.State = SceneEditorInputInteractionState.Idle;
        this.HoveredMarkerId = string.Empty;
        this.ActiveMarkerId = string.Empty;
        this.HoveredGizmoAxis = SceneEditorGizmoAxis.None;
        this.ActiveGizmoAxis = SceneEditorGizmoAxis.None;
        this.ActiveGizmoMode = SceneEditorGizmoMode.Select;
        this.DragStartMousePos = Vector2.Zero;
        this.DragCurrentMousePos = Vector2.Zero;
        this.DragStartWorldTransform = default;
    }
}
