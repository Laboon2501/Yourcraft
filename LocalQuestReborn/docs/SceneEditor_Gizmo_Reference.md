# Scene Editor Gizmo Reference

This note records the implementation patterns inspected before rewriting LocalQuestReborn's Scene Editor overlay and gizmo input model. The reference source was cloned outside this repository under `%TEMP%\lqr_scene_editor_refs` so it cannot affect this project's build.

## Sources Inspected

- Brio: `Etheirys/Brio`
  - `Brio/UI/Windows/Specialized/PosingOverlayWindow.cs`
  - `Brio/Core/ImGuizmoExtensions.cs`
  - `Brio/UI/Controls/Stateless/ImBrio.Gizmo.cs`
- Meddle: `PassiveModding/Meddle`
  - `Meddle.Plugin/UI/Layout/Overlay.cs`
  - `Meddle.Plugin/UI/Layout/Utils.cs`
- Ktisis: `ktisis-tools/Ktisis`
  - `Ktisis/Overlay/OverlayWindow.cs`
  - `Ktisis/Overlay/Gizmo.cs`
  - `Ktisis/Overlay/Selection.cs`

No code was copied from these projects. The implementation below only adopts interaction structure and safety boundaries.

## Brio Findings

- Overlay window:
  - Uses a transparent overlay window with `NoBackground`, `NoInputs`, `NoDecoration`, and no normal window chrome.
  - The overlay can temporarily remove `NoInputs` while an internal transform is being tracked, but the normal state is draw-only.
- Gizmo:
  - Uses ImGuizmo for world-space translate/rotate/scale operations.
  - Calls `ImGuizmo.BeginFrame`, `SetRect`, `SetDrawlist`, `SetOrthographic(false)`, and `AllowAxisFlip` each frame.
  - Tracks an in-progress transform separately. When `ImGuizmo.IsUsing()` ends or the mouse is released, it commits/snapshots the transform and clears the tracking state.
- Selection markers:
  - Projects actor/light positions to screen.
  - Uses small screen-space click targets.
  - Calls `ImGui.SetNextFrameWantCaptureMouse(true)` only when a clickable marker is actually hovered/clickable, not for the whole overlay.
- Input gating:
  - Builds an `OverlayUIState` with flags such as `UsingGizmo`, `HoveringGizmo`, `AnyActive`, `AnyWindowHovered`, and clickable hover/click state.
  - Disables selection/gizmo interaction when popups, UI items, or other hover targets are active.
- Rotation:
  - Brio also has a custom rotation helper that finds the nearest ring/axis point to the mouse, starts a drag only on a ring, maps the drag projection to angle deltas, and releases state on mouse-up.

## Meddle Findings

- Layout overlay:
  - Uses a full-screen transparent overlay window with `NoInputs`.
  - Draws layout dots through a draw list. The overlay itself is not an input item and does not block normal UI.
- Hit-test:
  - Projects world transform position with the active camera matrix.
  - Tests the mouse against a small screen rectangle around the dot.
  - Skips interaction if another ImGui window is hovered.
  - Calls `ImGui.SetNextFrameWantCaptureMouse(true)` only while the mouse is actually over a dot, then handles the left click.
- UX:
  - Dot size is small, about 5 px visual radius.
  - Tooltip displays the selected layout object's path and world transform.
  - Selected items change color instead of growing into a large overlay control.

## Ktisis Findings

- Overlay:
  - Starts a draw-only overlay only when skeleton dots, selection queue, or gizmo are visible.
  - Uses `NoBackground`, `NoDecoration`, and `NoInputs`.
- Gizmo:
  - Uses ImGuizmo for 3D manipulation.
  - Sets the gizmo rectangle to the main viewport each frame.
  - Tracks an owner ID for the selected gizmo target.
  - Resets ImGuizmo usage state when deselecting.
- Selection:
  - Draws screen-space dots and keeps a queue of hoverable items.
  - Uses a cursor-busy check: if ImGuizmo is using/over, any item/window is active/hovered/focused, or mouse is down, selection avoids stealing input.
  - When multiple dots are hovered, it shows a small selector near the cursor and captures mouse only for that selector.

## Adopted LocalQuestReborn Design

The new Scene Editor should follow this structure:

1. Draw-only overlay by default.
   - Keep a transparent overlay window with `NoInputs`, or draw directly to the foreground draw list.
   - Never place a full-screen `InvisibleButton`.
   - Never let a transparent window become the general mouse target.

2. Manual hit-testing.
   - Build `SceneMarker` and `SceneGizmoHandle` lists every frame.
   - Draw all markers and gizmo handles from those exact screen positions.
   - Hit-test the same positions against `ImGui.GetIO().MousePos`.
   - Prefer the nearest marker/handle; break ties with depth.

3. Input capture only on real targets.
   - If `ImGui.GetIO().WantCaptureMouse` is true and no gizmo drag is active, Scene Editor must not process marker/gizmo clicks.
   - When a marker or gizmo handle is hovered, call `ImGui.SetNextFrameWantCaptureMouse(true)` for that frame only.
   - During gizmo drag, keep capture until the left mouse button is released.
   - When the mouse is over empty game space, do nothing so the game receives normal input.

4. Explicit input state machine.
   - `Idle`: draw and hit-test only.
   - `MarkerHover`: tooltip and optional click-to-select.
   - `MarkerPressed`: one-frame selection transition, then idle.
   - `GizmoHover`: highlight a handle and prepare drag.
   - `GizmoDragging`: own the drag until mouse-up, apply transform every frame, then release.

5. Marker style.
   - Actor markers use actor/root/feet world position, not head position.
   - Visual radius should remain around 5-7 px, with a larger 10-12 px hit radius.
   - Selection should use an outline/highlight, not a large opaque circle.

6. Gizmo style.
   - Move axes use world X/Y/Z, projected to screen, with thick hit segments and colored labels.
   - Rotate handles use stable screen-space rings/handles with large hit bands; horizontal mouse delta maps to world-axis Euler deltas.
   - Scale provides a uniform center handle and per-axis handles.
   - Shift is fine adjustment; Ctrl or snap setting applies configured snapping.

7. World transform semantics.
   - `WorldPosition`, `WorldRotation`, and `WorldScale` are the only Scene Editor transform values.
   - UI inputs and gizmo drags apply directly to the same world transform.
   - No LocalPlayer/camera/spawn-template rotation may leak into Scene Editor values.

8. Scope boundaries.
   - Scene Editor must not touch actor spawning, prewarm, target resolution, appearance pipeline, GPose rebuild, BgPart creation, LocalLights native lifetime, RestoreAll, collision resolver, LookAt, bubbles, or deleted AnimationRig/DataPath code.
