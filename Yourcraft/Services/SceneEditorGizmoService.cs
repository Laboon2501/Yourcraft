using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class SceneEditorGizmoService
{
    private readonly IPluginLog log;

    public SceneEditorGizmoService(IPluginLog log)
        => this.log = log;

    public SceneEditorGizmoMode Mode { get; private set; } = SceneEditorGizmoMode.Select;

    public SceneEditorInputState InputState { get; } = new();

    public float MoveSensitivity { get; set; } = 1f;

    public float RotateSensitivityDegreesPerPixel { get; set; } = 0.35f;

    public float ScaleSensitivity { get; set; } = 0.01f;

    public bool SnapEnabled { get; set; }

    public float MoveSnapStep { get; set; } = 0.5f;

    public float RotateSnapDegrees { get; set; } = 15f;

    public float ScaleSnapStep { get; set; } = 0.1f;

    public string LastDebug { get; private set; } = "idle";

    public void SetMode(SceneEditorGizmoMode mode)
    {
        if (this.Mode == mode)
            return;

        this.Mode = mode;
        this.LastDebug = $"mode={mode}";
        this.log.Information("[SceneEditor] GizmoModeChanged mode={Mode}", mode);
    }

    public void SetIdle()
    {
        this.InputState.SetIdle();
        this.LastDebug = "idle";
    }

    public void SetMarkerHover(string runtimeId, Vector2 screenPosition, Vector2 mousePosition, float distance)
    {
        this.InputState.SetMarkerHover(runtimeId);
        this.LastDebug = $"marker hover id={runtimeId} screen={screenPosition} mouse={mousePosition} dist={distance:F1}";
    }

    public void SetGizmoHover(SceneEditorGizmoAxis axis)
    {
        this.InputState.SetGizmoHover(axis);
        this.LastDebug = $"gizmo hover mode={this.Mode} axis={axis}";
    }

    public void BeginDrag(SceneEditableRef item, SceneEditorGizmoAxis axis, Vector2 mousePosition)
    {
        this.InputState.BeginGizmoDrag(this.Mode, axis, mousePosition, item.Transform);
        this.LastDebug = $"drag start {this.Mode}/{axis} {item.Kind} {item.RuntimeId}";
        this.log.Information("[SceneEditor] GizmoDragStart kind={Kind} id={Id} mode={Mode} axis={Axis}", item.Kind, item.RuntimeId, this.Mode, axis);
    }

    public WorldTransform UpdateDrag(
        SceneEditorGizmoAxis axis,
        Vector2 mousePosition,
        Vector2 screenAxisUnit,
        float pixelsPerWorldUnit,
        bool shift,
        bool ctrl)
    {
        this.InputState.UpdateDrag(mousePosition);
        var mouseDelta = mousePosition - this.InputState.DragStartMousePos;
        var start = this.InputState.DragStartWorldTransform;
        var fine = shift ? 0.2f : 1f;
        WorldTransform result;

        switch (this.InputState.ActiveGizmoMode)
        {
            case SceneEditorGizmoMode.Move:
            {
                var delta = Vector2.Dot(mouseDelta, screenAxisUnit) / MathF.Max(1f, pixelsPerWorldUnit);
                delta *= this.MoveSensitivity * fine;
                if (ctrl || this.SnapEnabled)
                    delta = Snap(delta, this.MoveSnapStep);
                result = WorldTransform.FromEuler(
                    start.WorldPosition + AxisToVector(axis) * delta,
                    start.WorldEulerRadians,
                    start.WorldScale);
                break;
            }
            case SceneEditorGizmoMode.Rotate:
            {
                var degrees = mouseDelta.X * this.RotateSensitivityDegreesPerPixel * fine;
                if (ctrl || this.SnapEnabled)
                    degrees = Snap(degrees, this.RotateSnapDegrees);
                var radians = degrees * MathF.PI / 180f;
                result = WorldTransform.FromEuler(
                    start.WorldPosition,
                    start.WorldEulerRadians + AxisToVector(axis) * radians,
                    start.WorldScale);
                break;
            }
            case SceneEditorGizmoMode.Scale:
            {
                var scale = start.WorldScale;
                var delta = mouseDelta.X * this.ScaleSensitivity * fine;
                if (axis is SceneEditorGizmoAxis.X or SceneEditorGizmoAxis.Y or SceneEditorGizmoAxis.Z)
                    delta = Vector2.Dot(mouseDelta, screenAxisUnit) / MathF.Max(1f, pixelsPerWorldUnit) * this.ScaleSensitivity * 100f * fine;
                if (ctrl || this.SnapEnabled)
                    delta = Snap(delta, this.ScaleSnapStep);

                scale += axis == SceneEditorGizmoAxis.Uniform
                    ? new Vector3(delta)
                    : AxisToVector(axis) * delta;
                result = WorldTransform.FromEuler(start.WorldPosition, start.WorldEulerRadians, WorldTransformUtil.NormalizeScale(scale));
                break;
            }
            default:
                result = start;
                break;
        }

        this.LastDebug = $"drag {this.InputState.ActiveGizmoMode}/{axis} pos={result.WorldPosition} rot={result.WorldEulerRadians} scale={result.WorldScale}";
        return result;
    }

    public void EndDrag(SceneEditableKind kind, string runtimeId)
    {
        var mode = this.InputState.ActiveGizmoMode;
        var axis = this.InputState.ActiveGizmoAxis;
        this.LastDebug = $"drag end {mode}/{axis} {kind} {runtimeId}";
        this.log.Information("[SceneEditor] GizmoDragEnd kind={Kind} id={Id} mode={Mode} axis={Axis}", kind, runtimeId, mode, axis);
        this.InputState.EndGizmoDrag();
    }

    public static Vector3 AxisToVector(SceneEditorGizmoAxis axis)
        => axis switch
        {
            SceneEditorGizmoAxis.X => Vector3.UnitX,
            SceneEditorGizmoAxis.Y => Vector3.UnitY,
            SceneEditorGizmoAxis.Z => Vector3.UnitZ,
            _ => Vector3.One,
        };

    private static float Snap(float value, float step)
    {
        if (!float.IsFinite(step) || step <= 0.0001f)
            return value;

        return MathF.Round(value / step) * step;
    }
}
