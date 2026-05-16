# Standalone BgObject Spawn Experiment

## Goal

This experiment is separate from the stable slot-backed copy system.

- Slot-backed copy uses an existing `BgPartsLayoutInstance` as a carrier and can restore that carrier.
- Standalone spawn tries to create a new `FFXIV.Client.Graphics.Scene.BgObject` directly with `BgObject.Create(...)`.
- It must not occupy an existing BgPart slot, mutate `LayoutManager` / `LayerManager` instance containers, write path tables, create collision, or call `CreateSecondary`.

## Known Entry

Current function-body research identified the native create path as:

```text
BgObject.Create(
  CStringPointer modelGamePath,
  CStringPointer poolName,
  BgObject* existingAllocation = null
)
```

`CreatePrimary` normally calls it with:

```text
modelGamePath = *(char**)pathOrType
poolName = "Client.LayoutEngine.Layer.BgPartsLayoutInstance"
existingAllocation = layout BgObject pool slot or null
```

The new debug experiment calls:

```text
BgObject.Create(modelPath, "Yourcraft.StandaloneBgObject", null)
```

and then writes `Graphics.Scene.Object.Position / Rotation / Scale` followed by:

```text
IsTransformChanged = true
NotifyTransformChanged()
UpdateTransforms(true)
UpdateRender()
```

## Probe Added

`StandaloneBgObjectProbeService` can dump a selected visible BgPart:

- graphics object address and vtable
- model resource handle, path, load state, category
- `Position`, `Rotation`, `Scale`
- `IsVisible`, `IsTransformChanged`
- object/draw flags where readable
- parent / child / sibling scene links

This is read-only for existing map objects.

## Standalone Instance State

`StandaloneObjectInstance` records:

- id
- object address
- model path
- created time
- position / rotation / scale
- visible / valid
- model handle readback
- scene link readback
- manual confirmation fields
- last error

## v11.0 Hotfix: CreateOnly / Delayed Read Validation

The first standalone experiment crashed because `Create(...)` immediately called transform writing on the returned pointer.

The current flow is intentionally split:

1. `CreateOnly`
   - calls `BgObject.Create(modelPath, poolName, null)`;
   - records the returned pointer;
   - does not write `Position`, `Rotation`, or `Scale`;
   - does not call `NotifyTransformChanged`, `UpdateTransforms`, or `UpdateRender`.
2. Delayed read-only validation
   - waits several framework frames;
   - reads vtable, model handle, load state, visible, and transform;
   - requires stable readback for two frames.
3. Manual single-field write experiment
   - only appears after `ValidatedReadOnly`;
   - first writes only `Scene.Object.Position`;
   - rotation/scale are gated behind a successful position write.

If the object is readable but `visible=false`, it is marked as `NeedSceneRegistration`.
That means the next research target is scene registration, not transform writing.

## Delete / Cleanup Policy

This version deliberately does not call `CleanupRender`, `Dtor`, or unknown remove-from-world functions for standalone objects.

The debug “delete” action is state-aware:

- unvalidated objects are only marked as unmanaged/leaked in the plugin list;
- validated objects may be hidden/moved only through a separate manual button.

When the manual hide path is allowed, it only:

1. sets `IsVisible=false`,
2. moves the object underground,
3. calls transform/render update,
4. marks the plugin-owned record invalid.

This avoids touching the render lifetime path that previously caused crashes in other BgPart experiments, but it may leak the allocation until map reload / process cleanup. The UI explicitly reports this risk.

## Expected Outcomes

If `BgObject.Create(..., null)` is enough:

- a new model appears at the requested position,
- existing map BgParts do not change,
- transform edits work through `Graphics.Scene.Object`,
- hide/delete makes it disappear without affecting slot-backed restore logic.

If it creates an object but it is invisible:

- likely missing scene root / parent registration,
- likely missing `OnAddedToWorld`,
- likely missing culling/render scene registration,
- next investigation should inspect `Object.AddChild`, `Object.OnAddedToWorld`, and existing BgObject parent/child/world pointers before trying any additional lifecycle calls.

## v11.1: Render Activation Steps

Current field result:

- `BgObject.Create(...)` can return a valid pointer.
- `ModelResourceHandle` can be valid with `LoadState=7`.
- `IsVisible` can read `true`.
- single-field `Position` write can read back correctly.
- the model may still not be visible in the game view.

That points at missing render activation, culling registration, or visible mesh submission rather than basic object allocation.

The Debug UI now exposes single-step activation buttons for one selected standalone object:

- `IsTransformChanged = true`
- `NotifyTransformChanged()`
- `UpdateTransforms(true)`
- `UpdateMaterials()`
- `UpdateRender()`
- `ComputeSphereBounds()`
- `ComputeSphereBounds() -> UpdateCulling()`
- `UpdateTransforms(true) -> UpdateRender()`
- `NotifyTransformChanged() -> UpdateTransforms(true) -> UpdateRender()`
- `ComputeSphereBounds() -> UpdateCulling() -> UpdateRender()`

Each step immediately dumps:

- object address and vtable
- model handle, path, category, load state
- visible state
- object/draw/outline flags
- `IsTransformChanged`
- transform
- parent / child / sibling scene links
- render pointer candidates such as cached matrices, cached transform, stain/bg-change data, and animation data
- optional sphere bounds readback

The UI also supports creating standalone objects with either:

- `Yourcraft.StandaloneBgObject`
- `Client.LayoutEngine.Layer.BgPartsLayoutInstance`

The latter is the pool name observed in the real `CreatePrimary -> BgObject.Create` call path.

If all activation chains still produce an invisible object, the next step is function-body research for:

- `Object.OnAddedToWorld`
- `Object.AddChild`
- `BgObject.Create` helper calls around `0x14041E7C0` / `0x14041E8B0`
- real visible BgObject parent/root relationships
- culling/render scene registration

## Safety Boundaries

Forbidden in this experiment:

- no existing BgPart slot occupation
- no `LayoutManager` / `LayerManager` instance container mutation
- no path table writes
- no controller/listener pointer copying
- no `CreateSecondary`
- no collision
- no batching until single-instance behavior is verified
- no `CleanupRender`

The stable slot-backed copy flow remains unchanged.
