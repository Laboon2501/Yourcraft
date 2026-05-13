# SetModel Refresh Chain Findings

## Current Result

- `BgObject.SetModel` can return `true`.
- `ModelResourceHandle.FileName` can change to the target `.mdl`.
- Observed `LoadState` remains `7`.
- The visible mesh does not change after the currently tested refresh calls.

## Disabled Path

Standalone `UpdateCulling()` is disabled in the UI and service path.

Reason: calling `UpdateCulling()` directly caused a crash. It is now only exposed through:

```text
ComputeSphereBounds -> UpdateCulling
```

The UI labels this explicitly: `UpdateCulling 必须在 bounds 已计算后调用。`

## Refresh Calls Tested

These calls are available as single-step or controlled combo experiments:

- `UpdateMaterials()`
- `UpdateRender()`
- `UpdateTransforms(true)`
- `NotifyTransformChanged()`
- `IsTransformChanged = true`
- `ComputeSphereBounds()`
- `ComputeSphereBounds -> UpdateCulling`

Known current conclusion:

```text
SetModel 当前只更新 ModelResourceHandle/FileName，
但不会自动重建当前可见 render mesh。
UpdateMaterials / UpdateRender / UpdateTransforms / NotifyTransformChanged /
ComputeSphereBounds / bounds-first UpdateCulling 暂未让外观变化。
```

## CleanupRender Result

`CleanupRender()` is now disabled.

Observed severe result:

- `SetModel -> CleanupRender -> UpdateRender` can make the model disappear.
- After `CleanupRender`, `RestoreAll` can crash.
- After `CleanupRender`, moving the instance back to the player can crash inside Scene.Object transform writes.

Conclusion:

```text
CleanupRender can break the current BgObject / GraphicsObject render state.
After that, transform writes are no longer safe.
```

The plugin no longer exposes CleanupRender buttons or CleanupRender combo chains. The service also refuses CleanupRender calls and marks the instance render state invalid.

## New Dump Focus

Each step records before/after dump and pointer diff for:

- `ModelResourceHandle`
- `cachedMatrices` at `BgObject + 0xA0`
- `stainOrBgChangeData` candidate at `BgObject + 0xA8`
- `cachedTransform` at `BgObject + 0xB0`
- `animationData` at `BgObject + 0xB8`
- visible state
- Scene object Position / Rotation / Scale

Current per-instance mdl conclusion:

```text
1. SetModel returns true and FileName changes.
2. The visible mesh does not change.
3. CleanupRender makes the model disappear.
4. CleanupRender can make later transform writes / RestoreAll unsafe.
5. Per-instance mdl replacement is paused.
6. Next research needs the true render mesh rebuild or layout object reinitialize entry.
```

## Safety Rules

- No batch SetModel.
- No SetModel during create/delete/restore.
- No standalone UpdateCulling.
- No CleanupRender.
- No resource path memory writes.
- No memcpy.
- All SetModel work remains single-instance Debug experimentation.
