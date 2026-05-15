using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class SceneEditorOverlayWindow : Window
{
    private const float MarkerVisualMin = 5f;
    private const float MarkerVisualMax = 7f;
    private const float MarkerHitRadius = 12f;
    private const float AxisWorldLength = 2.1f;
    private const float AxisHitRadius = 12f;
    private const float RotateRingHitRadius = 10f;
    private const float ScaleCenterHalfSize = 7f;

    private static readonly Vector4 ActorColor = new(0.16f, 0.62f, 1f, 0.95f);
    private static readonly Vector4 BgPartColor = new(1f, 0.68f, 0.16f, 0.95f);
    private static readonly Vector4 LightColor = new(0.82f, 0.52f, 1f, 0.95f);
    private static readonly Vector4 HiddenColor = new(0.55f, 0.55f, 0.55f, 0.72f);
    private static readonly Vector4 XColor = new(1f, 0.18f, 0.14f, 0.95f);
    private static readonly Vector4 YColor = new(0.18f, 0.95f, 0.24f, 0.95f);
    private static readonly Vector4 ZColor = new(0.18f, 0.48f, 1f, 0.95f);
    private static readonly Vector4 UniformColor = new(1f, 1f, 1f, 0.95f);

    private readonly IGameGui gameGui;
    private readonly SceneEditorService sceneEditor;
    private readonly SceneEditorSelectionService selection;

    private ActiveDrag activeDrag;
    private readonly TransformEditState panelTransformState = new();

    public SceneEditorOverlayWindow(
        IGameGui gameGui,
        SceneEditorService sceneEditor,
        SceneEditorSelectionService selection)
        : base("Scene Editor Overlay##LocalQuestRebornSceneEditorOverlay",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav)
    {
        this.gameGui = gameGui;
        this.sceneEditor = sceneEditor;
        this.selection = selection;
        this.IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(viewport.Size, ImGuiCond.Always);
    }

    public override bool DrawConditions()
        => this.sceneEditor.OverlayEnabled;

    public override void Draw()
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var drawList = ImGui.GetForegroundDrawList();
        var editables = this.sceneEditor.GetEditables();
        var markers = this.BuildMarkers(editables);
        var selected = this.sceneEditor.GetSelectedEditable();
        var selectedMarker = selected == null
            ? null
            : markers.FirstOrDefault(marker =>
                marker.Kind == selected.Kind &&
                string.Equals(marker.RuntimeId, selected.RuntimeId, StringComparison.OrdinalIgnoreCase));

        var handles = selected is { IsValid: true, TransformEditable: true }
            ? this.BuildGizmoHandles(selected)
            : Array.Empty<GizmoHandle>();

        this.sceneEditor.TryHandleUndoShortcut(
            this.activeDrag.Active,
            io.WantTextInput,
            io.KeyCtrl,
            ImGui.IsKeyPressed(ImGuiKey.Z));

        if (this.activeDrag.Active)
        {
            this.DrawMarkers(drawList, markers, null);
            this.DrawGizmo(drawList, selected, handles, this.activeDrag.Handle.Axis);
            if (selectedMarker != null && selected != null)
                this.DrawObjectPanel(selectedMarker, selected, selectedPanel: true);
            this.RequestSceneMouseCapture();
            this.sceneEditor.Gizmo.InputState.UpdateDrag(mouse);

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                this.UpdateActiveDrag(mouse, io.KeyShift, io.KeyCtrl);
                this.DrawDragHud(drawList, mouse, this.activeDrag.Kind, this.activeDrag.RuntimeId);
                return;
            }

            this.EndActiveDrag();
            return;
        }

        if (selectedMarker != null && selected != null)
            this.DrawObjectPanel(selectedMarker, selected, selectedPanel: true);

        var uiBusy = IsPluginOrImGuiUiBusy();
        if (uiBusy)
        {
            this.DrawMarkers(drawList, markers, null);
            this.DrawGizmo(drawList, selected, handles, SceneEditorGizmoAxis.None);
            this.sceneEditor.Gizmo.SetIdle();
            this.sceneEditor.SetHoveredMarker(null, Vector2.Zero, mouse, 0f);
            return;
        }

        var hoveredHandle = HitTestGizmo(handles, mouse);
        var hoveredMarker = hoveredHandle == null ? HitTestMarker(markers, mouse) : null;

        this.DrawMarkers(drawList, markers, hoveredMarker);
        this.DrawGizmo(drawList, selected, handles, hoveredHandle?.Axis ?? SceneEditorGizmoAxis.None);

        this.UpdateHoverState(hoveredMarker, hoveredHandle, mouse);

        if (hoveredHandle != null)
        {
            this.RequestSceneMouseCapture();
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && selected != null)
                this.BeginDrag(selected, hoveredHandle, mouse);
            return;
        }

        if (hoveredMarker != null)
        {
            this.RequestSceneMouseCapture();
            if (selectedMarker == null ||
                !string.Equals(selectedMarker.RuntimeId, hoveredMarker.RuntimeId, StringComparison.OrdinalIgnoreCase) ||
                selectedMarker.Kind != hoveredMarker.Kind)
            {
                this.DrawObjectPanel(hoveredMarker, hoveredMarker.Editable, selectedPanel: false);
            }
            this.DrawMarkerTooltip(hoveredMarker);

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                this.sceneEditor.NotifyMarkerClick(hoveredMarker.Editable, hoveredMarker.ScreenPosition, mouse);
                this.sceneEditor.Gizmo.InputState.SetMarkerPressed(hoveredMarker.RuntimeId);
                this.selection.Select(hoveredMarker.Kind, hoveredMarker.RuntimeId, SceneEditorSelectionSource.Overlay);
            }
        }
    }

    private void DrawObjectPanel(SceneMarker marker, SceneEditableRef editable, bool selectedPanel)
    {
        var viewport = ImGui.GetMainViewport();
        var panelPos = marker.ScreenPosition + new Vector2(18f, -18f);
        panelPos.X = Math.Clamp(panelPos.X, viewport.Pos.X + 8f, viewport.Pos.X + viewport.Size.X - 360f);
        panelPos.Y = Math.Clamp(panelPos.Y, viewport.Pos.Y + 8f, viewport.Pos.Y + viewport.Size.Y - 280f);

        ImGui.SetNextWindowPos(panelPos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(selectedPanel ? 0.94f : 0.84f);
        var flags = ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoFocusOnAppearing;
        if (!ImGui.Begin($"{(selectedPanel ? "Selected" : "Hover")} Scene Object##SceneEditorMiniPanel{editable.Kind}{editable.RuntimeId}", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted($"{editable.Kind}");
        ImGui.SameLine();
        ImGui.TextDisabled(ShortId(editable.RuntimeId));
        ImGui.TextWrapped(editable.DisplayName);
        if (!string.IsNullOrWhiteSpace(editable.MdlPath))
            ImGui.TextWrapped(editable.MdlPath);

        if (ImGui.Button("Select##MiniPanelSelect"))
            this.selection.Select(editable.Kind, editable.RuntimeId, SceneEditorSelectionSource.Overlay);

        var editableTransform = editable.TransformEditable && editable.IsValid;
        ImGui.SameLine();
        this.DrawMiniModeButton("Move", SceneEditorGizmoMode.Move, editableTransform);
        ImGui.SameLine();
        this.DrawMiniModeButton("Rotate", SceneEditorGizmoMode.Rotate, editableTransform);
        ImGui.SameLine();
        this.DrawMiniModeButton("Scale", SceneEditorGizmoMode.Scale, editableTransform);

        ImGui.SameLine();
        ImGui.BeginDisabled(!this.sceneEditor.Undo.HasUndo);
        if (ImGui.Button("Undo##MiniPanelUndo"))
            this.sceneEditor.TryUndoLast();
        ImGui.EndDisabled();

        if (editable.IsNativeGameObject)
        {
            ImGui.Separator();
            if (editable.IsPlayer)
                ImGui.TextDisabled("Player target: read-only.");
            else if (!editable.TransformEditable)
                ImGui.TextDisabled("Native target: read-only. Enable the existing unsafe native-write switch to move it.");
            else
                ImGui.TextColored(new Vector4(1f, 0.68f, 0.2f, 1f), "Native transform editing is experimental.");

            if (!string.IsNullOrWhiteSpace(editable.ObjectKind))
                ImGui.TextDisabled(editable.ObjectKind);
            if (!string.IsNullOrWhiteSpace(editable.DataId))
                ImGui.TextDisabled($"DataId {editable.DataId}");

            var nativeRecord = this.sceneEditor.GetNativeModificationRecord(editable);
            if (!editable.IsPlayer)
            {
                if (nativeRecord?.IsHidden == true)
                {
                    if (ImGui.Button("Restore##MiniNativeRestore"))
                        this.sceneEditor.RestoreNativeModification(nativeRecord.RecordId);
                }
                else
                {
                    ImGui.BeginDisabled(!this.sceneEditor.AllowNativeTransformWrites);
                    if (ImGui.Button("Hide##MiniNativeHide"))
                        this.sceneEditor.HideNativeObject(editable);
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered() && !this.sceneEditor.AllowNativeTransformWrites)
                        ImGui.SetTooltip("Enable the existing unsafe/native write confirmation before hiding native objects.");
                }

                ImGui.SameLine();
                ImGui.TextDisabled(nativeRecord == null ? "not recorded" : nativeRecord.Status);
            }
        }

        if (editable.Kind == SceneEditableKind.LocalActor)
            this.DrawMiniActorActions(editable);

        if (editable.Kind is SceneEditableKind.LocalBgPart or SceneEditableKind.NativeBgPart)
            this.DrawMiniBgPartActions(editable);

        if (selectedPanel && editableTransform)
            this.DrawMiniTransformEditor(editable);
        else
            DrawReadOnlyTransform(editable);

        if (editable.Kind == SceneEditableKind.LocalLight)
            this.DrawMiniLocalLightControls(editable);

        ImGui.End();
    }

    private void DrawMiniModeButton(string label, SceneEditorGizmoMode mode, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        var active = this.sceneEditor.GizmoMode == mode;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.95f, 0.55f, 0.18f, 0.82f));
        if (ImGui.Button($"{label}##MiniPanel{label}"))
            this.sceneEditor.SetGizmoMode(mode);
        if (active)
            ImGui.PopStyleColor();
        ImGui.EndDisabled();
    }

    private void DrawMiniBgPartActions(SceneEditableRef editable)
    {
        ImGui.Separator();
        ImGui.TextDisabled("BgPart");
        if (!string.IsNullOrWhiteSpace(editable.MdlPath) && ImGui.Button("Copy mdl##MiniCopyMdl"))
            ImGui.SetClipboardText(editable.MdlPath);

        ImGui.SameLine();
        ImGui.BeginDisabled(!this.sceneEditor.AllowNativeTransformWrites);
        if (ImGui.Button("Copy 1##MiniCopyOne"))
            this.sceneEditor.TryCopyOneBgPart(editable, new Vector3(0.6f, 0f, 0.6f));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered() && !this.sceneEditor.AllowNativeTransformWrites)
            ImGui.SetTooltip("Unsafe/native writes are disabled, or FullLayoutWithCollision has not been confirmed.");

        if (ImGui.Button("Candidate##MiniCandidate"))
            this.sceneEditor.TryMarkBgPartCandidate(editable);
        ImGui.SameLine();
        if (ImGui.Button("Preferred##MiniPreferred"))
            this.sceneEditor.TryPreferBgPart(editable);
        ImGui.SameLine();
        if (ImGui.Button("Protect##MiniProtect"))
            this.sceneEditor.TryProtectBgPart(editable);
    }

    private void DrawMiniActorActions(SceneEditableRef editable)
    {
        var actor = this.sceneEditor.GetLocalActor(editable.RuntimeId);
        if (actor == null)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Actor");
        var lookAt = actor.LookAtPlayerEnabled;
        if (ImGui.Checkbox("Look at player##MiniActorLookAt", ref lookAt))
        {
            actor.LookAtPlayerEnabled = lookAt;
            this.sceneEditor.UpdateLocalActorLookAt(actor.RuntimeId, actor.LookAtPlayerEnabled, actor.LookAtRadius);
        }

        var radius = actor.LookAtRadius <= 0.1f ? 8f : actor.LookAtRadius;
        if (DrawCompactFloatControl("Look radius##MiniActorLookRadius", ref radius, 0.1f, 0.1f, 1f, 0.1f, 80f))
        {
            actor.LookAtRadius = MathF.Max(0.1f, radius);
            this.sceneEditor.UpdateLocalActorLookAt(actor.RuntimeId, actor.LookAtPlayerEnabled, actor.LookAtRadius);
        }

        var animation = (int)Math.Min(actor.CurrentAnimationId == 0 ? actor.DefaultAnimationId : actor.CurrentAnimationId, int.MaxValue);
        if (DrawCompactIntControl("Animation ID##MiniActorAnimation", ref animation, 1, 0, int.MaxValue))
            actor.CurrentAnimationId = (uint)Math.Max(0, animation);

        if (ImGui.Button("Play##MiniActorPlay"))
            this.sceneEditor.PlayLocalActorAnimation(actor.RuntimeId, actor.CurrentAnimationId);
        ImGui.SameLine();
        if (ImGui.Button("Idle##MiniActorIdle"))
            this.sceneEditor.StopLocalActorAnimation(actor.RuntimeId);
    }

    private void DrawMiniLocalLightControls(SceneEditableRef editable)
    {
        var light = this.sceneEditor.GetLocalLight(editable.RuntimeId);
        if (light == null)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Local Light");
        var changed = false;
        var color = light.ColorRgb;
        changed |= DrawCompactVector3("RGB##MiniLightRgb", ref color, 0.1f, 0f, 1f);
        light.ColorRgb = Vector3.Clamp(color, Vector3.Zero, Vector3.One);

        var intensity = light.Intensity;
        if (DrawCompactFloatControl("Intensity##MiniLightIntensity", ref intensity, 0.1f, 0.01f, 1f, 0f, float.MaxValue))
        {
            light.Intensity = MathF.Max(0f, intensity);
            changed = true;
        }

        var range = light.Range;
        if (DrawCompactFloatControl("Range##MiniLightRange", ref range, 0.1f, 0.01f, 1f, 0f, float.MaxValue))
        {
            light.Range = MathF.Max(0f, range);
            changed = true;
        }

        var falloff = light.Falloff;
        if (DrawCompactFloatControl("Falloff##MiniLightFalloff", ref falloff, 0.1f, 0.01f, 1f, 0f, float.MaxValue))
        {
            light.Falloff = MathF.Max(0f, falloff);
            changed = true;
        }

        var spot = light.LightAngle;
        if (DrawCompactFloatControl("SpotAngle##MiniLightSpot", ref spot, 0.1f, 0.01f, 1f, 0f, 90f))
        {
            light.LightAngle = Math.Clamp(spot, 0f, 90f);
            changed = true;
        }

        var falloffAngle = light.FalloffAngle;
        if (DrawCompactFloatControl("FalloffAngle##MiniLightFalloffAngle", ref falloffAngle, 0.1f, 0.01f, 1f, 0f, 90f))
        {
            light.FalloffAngle = Math.Clamp(falloffAngle, 0f, 90f);
            changed = true;
        }

        var areaX = light.AreaAngleX;
        if (DrawCompactFloatControl("Area X##MiniLightAreaX", ref areaX, 0.1f, 0.01f, 1f, 0f, 90f))
        {
            light.AreaAngleX = Math.Clamp(areaX, 0f, 90f);
            changed = true;
        }

        var areaY = light.AreaAngleY;
        if (DrawCompactFloatControl("Area Y##MiniLightAreaY", ref areaY, 0.1f, 0.01f, 1f, 0f, 90f))
        {
            light.AreaAngleY = Math.Clamp(areaY, 0f, 90f);
            changed = true;
        }

        var specular = light.EnableSpecular;
        if (ImGui.Checkbox("Specular highlights##MiniLightSpecular", ref specular))
        {
            light.EnableSpecular = specular;
            changed = true;
        }

        var shadows = light.EnableDynamicShadows;
        if (ImGui.Checkbox("Dynamic shadows##MiniLightShadows", ref shadows))
        {
            light.EnableDynamicShadows = shadows;
            changed = true;
        }

        if (changed)
            this.sceneEditor.RequestLocalLightApply(light.Id);
    }

    private void DrawMiniTransformEditor(SceneEditableRef editable)
    {
        this.panelTransformState.Bind(editable, this.sceneEditor.TransformGeneration);
        var before = editable.Transform;
        var changed = false;

        var position = this.panelTransformState.PositionInput;
        if (DrawCompactVector3("World Position##MiniPanel", ref position, 0.1f))
        {
            this.panelTransformState.PositionInput = position;
            changed = true;
        }

        var eulerDegrees = RadiansToDegrees(this.panelTransformState.EulerInput);
        if (DrawCompactVector3("World Rotation##MiniPanel", ref eulerDegrees, 0.1f))
        {
            this.panelTransformState.EulerInput = DegreesToRadians(eulerDegrees);
            changed = true;
        }

        var scale = this.panelTransformState.ScaleInput;
        if (DrawCompactVector3("World Scale##MiniPanel", ref scale, 0.1f, 0.01f, float.MaxValue))
        {
            this.panelTransformState.ScaleInput = WorldTransformUtil.NormalizeScale(scale);
            changed = true;
        }

        if (changed)
        {
            var after = this.panelTransformState.ToWorldTransform();
            if (this.sceneEditor.ApplyWorldTransform(editable.Kind, editable.RuntimeId, after))
                this.sceneEditor.PushTransformUndo(editable.Kind, editable.RuntimeId, editable.DisplayName, before, after, "PanelInput");
        }

        if (ImGui.Button("Read current##MiniReadTransform"))
            this.panelTransformState.Bind(editable, uint.MaxValue);
        ImGui.SameLine();
        if (ImGui.Button("Save to config##MiniSaveTransform"))
            this.sceneEditor.ApplyWorldTransform(editable.Kind, editable.RuntimeId, this.panelTransformState.ToWorldTransform());
    }

    private static bool DrawCompactVector3(string label, ref Vector3 vector, float step, float min = float.MinValue, float max = float.MaxValue)
    {
        var changed = false;
        ImGui.TextUnformatted(LabelText(label));
        ImGui.SameLine();
        var x = vector.X;
        changed |= DrawCompactFloatControl($"X##{label}", ref x, step, step * 0.1f, step * 10f, min, max);
        var y = vector.Y;
        changed |= DrawCompactFloatControl($"Y##{label}", ref y, step, step * 0.1f, step * 10f, min, max);
        var z = vector.Z;
        changed |= DrawCompactFloatControl($"Z##{label}", ref z, step, step * 0.1f, step * 10f, min, max);
        if (changed)
            vector = new Vector3(x, y, z);
        return changed;
    }

    private static bool DrawCompactFloatControl(string label, ref float value, float step = 0.1f, float shiftStep = 0.01f, float ctrlStep = 1f, float min = float.MinValue, float max = float.MaxValue)
    {
        var changed = false;
        var io = ImGui.GetIO();
        var effectiveStep = io.KeyCtrl ? ctrlStep : io.KeyShift ? shiftStep : step;
        ImGui.PushID(label);
        ImGui.TextUnformatted(LabelText(label));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(74f);
        var local = value;
        if (ImGui.InputFloat("##value", ref local, 0f, 0f, "%.3f"))
        {
            value = Math.Clamp(local, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("-"))
        {
            value = Math.Clamp(value - effectiveStep, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("+"))
        {
            value = Math.Clamp(value + effectiveStep, min, max);
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    private static bool DrawCompactIntControl(string label, ref int value, int step, int min, int max)
    {
        var changed = false;
        ImGui.PushID(label);
        ImGui.TextUnformatted(LabelText(label));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(74f);
        var local = value;
        if (ImGui.InputInt("##value", ref local, 0))
        {
            value = Math.Clamp(local, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("-"))
        {
            value = Math.Clamp(value - step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("+"))
        {
            value = Math.Clamp(value + step, min, max);
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    private static string LabelText(string label)
    {
        var index = label.IndexOf("##", StringComparison.Ordinal);
        return index < 0 ? label : label[..index];
    }

    private static void DrawReadOnlyTransform(SceneEditableRef editable)
    {
        ImGui.Separator();
        ImGui.TextDisabled($"Position {editable.Transform.WorldPosition.X:F2}, {editable.Transform.WorldPosition.Y:F2}, {editable.Transform.WorldPosition.Z:F2}");
        var euler = RadiansToDegrees(editable.Transform.WorldEulerRadians);
        ImGui.TextDisabled($"Rotation {euler.X:F1}, {euler.Y:F1}, {euler.Z:F1}");
        ImGui.TextDisabled($"Scale {editable.Transform.WorldScale.X:F2}, {editable.Transform.WorldScale.Y:F2}, {editable.Transform.WorldScale.Z:F2}");
    }

    private static bool InputVector3(string label, ref Vector3 vector)
    {
        var changed = false;
        var x = vector.X;
        var y = vector.Y;
        var z = vector.Z;
        if (ImGui.InputFloat($"{label} X", ref x))
            changed = true;
        if (ImGui.InputFloat($"{label} Y", ref y))
            changed = true;
        if (ImGui.InputFloat($"{label} Z", ref z))
            changed = true;
        if (changed)
            vector = new Vector3(x, y, z);
        return changed;
    }

    private static Vector3 DegreesToRadians(Vector3 degrees)
        => degrees * (MathF.PI / 180f);

    private static string ShortId(string id)
        => string.IsNullOrWhiteSpace(id) ? "none" : id.Length <= 8 ? id : id[..8];

    private List<SceneMarker> BuildMarkers(IReadOnlyList<SceneEditableRef> editables)
    {
        var markers = new List<SceneMarker>(editables.Count);
        foreach (var editable in editables)
        {
            if (!editable.IsValid && editable.Kind != SceneEditableKind.LocalLight)
                continue;

            if (!this.gameGui.WorldToScreen(editable.MarkerWorldPosition, out var screen))
                continue;

            markers.Add(new SceneMarker(
                editable,
                editable.RuntimeId,
                editable.Kind,
                editable.MarkerWorldPosition,
                screen,
                Math.Clamp(this.sceneEditor.MarkerRadius, MarkerVisualMin, MarkerVisualMax),
                MarkerHitRadius,
                editable.Transform.WorldPosition.LengthSquared(),
                editable.DisplayName,
                editable.MdlPath));
        }

        return markers;
    }

    private void DrawMarkers(ImDrawListPtr drawList, IReadOnlyList<SceneMarker> markers, SceneMarker? hovered)
    {
        foreach (var marker in markers)
        {
            var selected = this.selection.IsSelected(marker.Kind, marker.RuntimeId);
            var hoveredThis = hovered != null && string.Equals(hovered.RuntimeId, marker.RuntimeId, StringComparison.Ordinal);
            var radius = marker.VisualRadius + (hoveredThis ? 1f : 0f);
            var fill = MarkerColor(marker.Editable, selected);
            var outline = selected
                ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.98f))
                : ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.78f));

            drawList.AddCircleFilled(marker.ScreenPosition, radius, fill, 18);
            drawList.AddCircle(marker.ScreenPosition, radius + (selected ? 2.25f : 1.5f), outline, 18, selected ? 2f : 1.25f);
        }
    }

    private void DrawMarkerTooltip(SceneMarker marker)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(marker.Kind.ToString());
        ImGui.TextUnformatted(marker.DisplayName);
        if (!string.IsNullOrWhiteSpace(marker.MdlPath))
            ImGui.TextWrapped(marker.MdlPath);
        ImGui.TextUnformatted($"World: {marker.WorldPosition}");
        ImGui.EndTooltip();
    }

    private GizmoHandle[] BuildGizmoHandles(SceneEditableRef selected)
    {
        if (this.sceneEditor.Gizmo.Mode == SceneEditorGizmoMode.Select)
            return Array.Empty<GizmoHandle>();

        if (!this.gameGui.WorldToScreen(selected.Transform.WorldPosition, out var originScreen))
            return Array.Empty<GizmoHandle>();

        return this.sceneEditor.Gizmo.Mode switch
        {
            SceneEditorGizmoMode.Move => this.BuildAxisHandles(selected.Transform.WorldPosition, originScreen, includeUniform: false),
            SceneEditorGizmoMode.Rotate => BuildRotateHandles(originScreen),
            SceneEditorGizmoMode.Scale => this.BuildAxisHandles(selected.Transform.WorldPosition, originScreen, includeUniform: true),
            _ => Array.Empty<GizmoHandle>(),
        };
    }

    private void DrawGizmo(ImDrawListPtr drawList, SceneEditableRef? selected, IReadOnlyList<GizmoHandle> handles, SceneEditorGizmoAxis hoveredAxis)
    {
        if (selected is not { IsValid: true } || handles.Count == 0)
            return;

        foreach (var handle in handles)
        {
            var color = AxisColor(handle.Axis);
            var hovered = hoveredAxis == handle.Axis;
            var drawColor = ImGui.GetColorU32(hovered ? new Vector4(color.X, color.Y, color.Z, 1f) : color);

            switch (handle.Kind)
            {
                case GizmoHandleKind.Segment:
                    drawList.AddLine(handle.ScreenStart, handle.ScreenEnd, drawColor, hovered ? 4.25f : 2.5f);
                    if (this.sceneEditor.Gizmo.Mode == SceneEditorGizmoMode.Scale)
                    {
                        var min = handle.ScreenEnd - new Vector2(5f);
                        var max = handle.ScreenEnd + new Vector2(5f);
                        drawList.AddRectFilled(min, max, drawColor);
                        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f)));
                    }
                    else
                    {
                        drawList.AddCircleFilled(handle.ScreenEnd, hovered ? 6.25f : 4.75f, drawColor, 16);
                    }

                    drawList.AddText(handle.ScreenEnd + new Vector2(5f, -8f), drawColor, handle.Axis.ToString());
                    break;
                case GizmoHandleKind.Ring:
                    drawList.AddCircle(handle.ScreenStart, handle.RingRadius, drawColor, 96, hovered ? 3.5f : 1.8f);
                    drawList.AddText(handle.ScreenStart + new Vector2(handle.RingRadius + 6f, -8f), drawColor, handle.Axis.ToString());
                    break;
                case GizmoHandleKind.Point:
                    var pointMin = handle.ScreenStart - new Vector2(ScaleCenterHalfSize);
                    var pointMax = handle.ScreenStart + new Vector2(ScaleCenterHalfSize);
                    drawList.AddRectFilled(pointMin, pointMax, drawColor);
                    drawList.AddRect(pointMin, pointMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.78f)), 0f, ImDrawFlags.None, hovered ? 2.5f : 1.25f);
                    break;
            }
        }
    }

    private GizmoHandle[] BuildAxisHandles(Vector3 originWorld, Vector2 originScreen, bool includeUniform)
    {
        var handles = new List<GizmoHandle>(includeUniform ? 4 : 3);
        this.AddAxisHandle(handles, originWorld, originScreen, Vector3.UnitX, SceneEditorGizmoAxis.X);
        this.AddAxisHandle(handles, originWorld, originScreen, Vector3.UnitY, SceneEditorGizmoAxis.Y);
        this.AddAxisHandle(handles, originWorld, originScreen, Vector3.UnitZ, SceneEditorGizmoAxis.Z);

        if (includeUniform)
            handles.Add(GizmoHandle.ForPoint(SceneEditorGizmoAxis.Uniform, originScreen, Vector2.UnitX, Vector3.One, 100f, ScaleCenterHalfSize + 3f));

        return handles.ToArray();
    }

    private void AddAxisHandle(
        List<GizmoHandle> result,
        Vector3 originWorld,
        Vector2 originScreen,
        Vector3 axisWorld,
        SceneEditorGizmoAxis axis)
    {
        if (!this.gameGui.WorldToScreen(originWorld + axisWorld * AxisWorldLength, out var endScreen))
            return;

        var screenAxis = endScreen - originScreen;
        var screenLength = screenAxis.Length();
        if (screenLength < 8f)
            return;

        var screenUnit = screenAxis / screenLength;
        result.Add(GizmoHandle.ForSegment(axis, originScreen, endScreen, screenUnit, axisWorld, screenLength / AxisWorldLength));
    }

    private static GizmoHandle[] BuildRotateHandles(Vector2 originScreen)
    {
        return
        [
            GizmoHandle.ForRing(SceneEditorGizmoAxis.X, originScreen, 34f, Vector2.UnitX, Vector3.UnitX),
            GizmoHandle.ForRing(SceneEditorGizmoAxis.Y, originScreen, 51f, Vector2.UnitX, Vector3.UnitY),
            GizmoHandle.ForRing(SceneEditorGizmoAxis.Z, originScreen, 68f, Vector2.UnitX, Vector3.UnitZ),
        ];
    }

    private void UpdateHoverState(SceneMarker? marker, GizmoHandle? handle, Vector2 mouse)
    {
        if (handle != null)
        {
            this.sceneEditor.Gizmo.SetGizmoHover(handle.Axis);
            this.sceneEditor.SetHoveredMarker(null, Vector2.Zero, mouse, 0f);
            return;
        }

        if (marker != null)
        {
            this.sceneEditor.SetHoveredMarker(marker.Editable, marker.ScreenPosition, mouse, marker.HitDistance(mouse));
            return;
        }

        this.sceneEditor.Gizmo.SetIdle();
        this.sceneEditor.SetHoveredMarker(null, Vector2.Zero, mouse, 0f);
    }

    private void BeginDrag(SceneEditableRef selected, GizmoHandle handle, Vector2 mouse)
    {
        this.activeDrag = new ActiveDrag(selected.Kind, selected.RuntimeId, handle, selected.Transform);
        this.RequestSceneMouseCapture();
        this.sceneEditor.Gizmo.BeginDrag(selected, handle.Axis, mouse);
    }

    private void UpdateActiveDrag(Vector2 mouse, bool shift, bool ctrl)
    {
        this.RequestSceneMouseCapture();
        var updated = this.sceneEditor.Gizmo.UpdateDrag(
            this.activeDrag.Handle.Axis,
            mouse,
            this.activeDrag.Handle.ScreenAxisUnit,
            this.activeDrag.Handle.PixelsPerWorldUnit,
            shift,
            ctrl);

        this.sceneEditor.ApplyWorldTransform(this.activeDrag.Kind, this.activeDrag.RuntimeId, updated);
    }

    private void EndActiveDrag()
    {
        var after = this.sceneEditor.GetEditables().FirstOrDefault(item =>
            item.Kind == this.activeDrag.Kind &&
            string.Equals(item.RuntimeId, this.activeDrag.RuntimeId, StringComparison.OrdinalIgnoreCase))?.Transform;
        if (after.HasValue)
        {
            this.sceneEditor.PushTransformUndo(
                this.activeDrag.Kind,
                this.activeDrag.RuntimeId,
                this.sceneEditor.GetEditables().FirstOrDefault(item =>
                    item.Kind == this.activeDrag.Kind &&
                    string.Equals(item.RuntimeId, this.activeDrag.RuntimeId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? this.activeDrag.RuntimeId,
                this.activeDrag.StartTransform,
                after.Value,
                this.sceneEditor.Gizmo.Mode switch
                {
                    SceneEditorGizmoMode.Move => "GizmoMove",
                    SceneEditorGizmoMode.Rotate => "GizmoRotate",
                    SceneEditorGizmoMode.Scale => "GizmoScale",
                    _ => "Gizmo",
                });
        }

        this.sceneEditor.Gizmo.EndDrag(this.activeDrag.Kind, this.activeDrag.RuntimeId);
        this.activeDrag = default;
    }

    private void DrawDragHud(ImDrawListPtr drawList, Vector2 mouse, SceneEditableKind kind, string runtimeId)
    {
        var selected = this.sceneEditor.GetEditables().FirstOrDefault(item =>
            item.Kind == kind && string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
            return;

        var text = this.sceneEditor.Gizmo.Mode switch
        {
            SceneEditorGizmoMode.Move => $"Position {selected.Transform.WorldPosition.X:F2}, {selected.Transform.WorldPosition.Y:F2}, {selected.Transform.WorldPosition.Z:F2}",
            SceneEditorGizmoMode.Rotate => $"Rotation {RadiansToDegrees(selected.Transform.WorldEulerRadians).X:F1}, {RadiansToDegrees(selected.Transform.WorldEulerRadians).Y:F1}, {RadiansToDegrees(selected.Transform.WorldEulerRadians).Z:F1}",
            SceneEditorGizmoMode.Scale => $"Scale {selected.Transform.WorldScale.X:F2}, {selected.Transform.WorldScale.Y:F2}, {selected.Transform.WorldScale.Z:F2}",
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(text))
            return;

        var pos = mouse + new Vector2(18f, 18f);
        drawList.AddRectFilled(pos - new Vector2(7f, 5f), pos + ImGui.CalcTextSize(text) + new Vector2(7f, 5f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.68f)), 4f);
        drawList.AddText(pos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), text);
    }

    private void RequestSceneMouseCapture()
    {
        ImGui.SetNextFrameWantCaptureMouse(true);
    }

    private static bool IsPluginOrImGuiUiBusy()
    {
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
            return true;

        if (ImGui.IsAnyItemActive() || ImGui.IsAnyItemFocused())
            return true;

        return false;
    }

    private static SceneMarker? HitTestMarker(IReadOnlyList<SceneMarker> markers, Vector2 mouse)
    {
        SceneMarker? best = null;
        var bestDistance = float.MaxValue;
        foreach (var marker in markers)
        {
            var distance = marker.HitDistance(mouse);
            if (distance > marker.HitRadius)
                continue;

            if (distance < bestDistance || (MathF.Abs(distance - bestDistance) < 0.001f && best != null && marker.Depth < best.Depth))
            {
                best = marker;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static GizmoHandle? HitTestGizmo(IReadOnlyList<GizmoHandle> handles, Vector2 mouse)
    {
        GizmoHandle? best = null;
        var bestDistance = float.MaxValue;
        foreach (var handle in handles)
        {
            var distance = handle.HitDistance(mouse);
            if (distance > handle.HitRadius)
                continue;

            if (distance < bestDistance)
            {
                best = handle;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static uint MarkerColor(SceneEditableRef item, bool selected)
    {
        var color = item.Kind switch
        {
            SceneEditableKind.LocalActor => ActorColor,
            SceneEditableKind.NativeActor => ActorColor,
            SceneEditableKind.EventNpc => ActorColor,
            SceneEditableKind.Player => ActorColor,
            SceneEditableKind.LocalBgPart => BgPartColor,
            SceneEditableKind.NativeBgPart => BgPartColor,
            SceneEditableKind.LocalLight => item.IsHidden ? HiddenColor : LightColor,
            SceneEditableKind.NativeLight => item.IsHidden ? HiddenColor : LightColor,
            _ => UniformColor,
        };

        if (selected)
            color = new Vector4(1f, 0.55f, 0.15f, 1f);

        return ImGui.GetColorU32(color);
    }

    private static Vector4 AxisColor(SceneEditorGizmoAxis axis)
        => axis switch
        {
            SceneEditorGizmoAxis.X => XColor,
            SceneEditorGizmoAxis.Y => YColor,
            SceneEditorGizmoAxis.Z => ZColor,
            SceneEditorGizmoAxis.Uniform => UniformColor,
            _ => UniformColor,
        };

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return Vector2.Distance(point, start);

        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + segment * t);
    }

    private static Vector3 RadiansToDegrees(Vector3 radians)
        => radians * (180f / MathF.PI);

    private sealed record SceneMarker(
        SceneEditableRef Editable,
        string RuntimeId,
        SceneEditableKind Kind,
        Vector3 WorldPosition,
        Vector2 ScreenPosition,
        float VisualRadius,
        float HitRadius,
        float Depth,
        string DisplayName,
        string MdlPath)
    {
        public float HitDistance(Vector2 mouse)
            => Vector2.Distance(mouse, this.ScreenPosition);
    }

    private sealed record GizmoHandle(
        SceneEditorGizmoAxis Axis,
        GizmoHandleKind Kind,
        Vector2 ScreenStart,
        Vector2 ScreenEnd,
        Vector2 ScreenAxisUnit,
        Vector3 WorldAxis,
        float PixelsPerWorldUnit,
        float HitRadius,
        float RingRadius)
    {
        public static GizmoHandle ForSegment(SceneEditorGizmoAxis axis, Vector2 start, Vector2 end, Vector2 screenAxisUnit, Vector3 worldAxis, float pixelsPerWorldUnit)
            => new(axis, GizmoHandleKind.Segment, start, end, screenAxisUnit, worldAxis, pixelsPerWorldUnit, AxisHitRadius, 0f);

        public static GizmoHandle ForRing(SceneEditorGizmoAxis axis, Vector2 center, float ringRadius, Vector2 screenAxisUnit, Vector3 worldAxis)
            => new(axis, GizmoHandleKind.Ring, center, center + Vector2.UnitX * ringRadius, screenAxisUnit, worldAxis, 100f, RotateRingHitRadius, ringRadius);

        public static GizmoHandle ForPoint(SceneEditorGizmoAxis axis, Vector2 center, Vector2 screenAxisUnit, Vector3 worldAxis, float pixelsPerWorldUnit, float hitRadius)
            => new(axis, GizmoHandleKind.Point, center, center, screenAxisUnit, worldAxis, pixelsPerWorldUnit, hitRadius, 0f);

        public float HitDistance(Vector2 mouse)
            => this.Kind switch
            {
                GizmoHandleKind.Segment => DistanceToSegment(mouse, this.ScreenStart, this.ScreenEnd),
                GizmoHandleKind.Ring => MathF.Abs(Vector2.Distance(mouse, this.ScreenStart) - this.RingRadius),
                GizmoHandleKind.Point => Vector2.Distance(mouse, this.ScreenStart),
                _ => float.MaxValue,
            };
    }

    private enum GizmoHandleKind
    {
        Segment,
        Ring,
        Point,
    }

    private readonly record struct ActiveDrag(
        SceneEditableKind Kind,
        string RuntimeId,
        GizmoHandle Handle,
        WorldTransform StartTransform)
    {
        public bool Active => !string.IsNullOrWhiteSpace(this.RuntimeId);
    }
}
