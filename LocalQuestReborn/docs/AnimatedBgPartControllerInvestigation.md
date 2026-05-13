# Animated / Dynamic Material BgPart Controller Investigation

## Scope

v9.3 adds a read-first probe for comparing:

- A: an original map BgPart that animates or has dynamic material behavior in its native layout context
- B: a LocalQuestReborn instance recreated from `DestroyPrimary -> CreatePrimary` that appears static or unsafe

The probe does not call `CleanupRender`, does not copy controller/listener pointers, does not batch anything, and does not connect this to the formal object creation flow.

## Implemented Probe

New service:

- `AnimatedBgPartControllerProbeService`

UI location:

- `Debug`
- `Animated / Dynamic Material BgPart Controller 取证（只读，60 帧采样）`

Buttons:

- `单帧 dump A/B controller 字段`
- `开始 60 帧只读采样`
- `UpdateMaterials 前后对比`
- `取消采样`

## Dumped Fields

### BgPartsLayoutInstance

The probe reads:

- `GraphicsObject`
- `Collider`
- `CollisionUpdateListener`
- collision CRCs and material masks/ids
- primary and secondary paths
- `Layer`
- `Layout`
- layout transform
- raw pointer-sized fields from the beginning of the instance as a flags/key/context candidate block

The raw block is intentionally read-only and not interpreted as writable state.

### BgObject / Graphics.Scene.Object

The probe reads:

- `ModelResourceHandle`
- `FileName`
- `LoadState`
- resource category
- `IsVisible`
- `IsTransformChanged`
- `Position`
- `Rotation`
- `Scale`
- `cachedMatrices` at `BgObject + 0xA0`
- `stainOrBgChangeData` at `BgObject + 0xA8`
- `cachedTransform` at `BgObject + 0xB0`
- `animationData` at `BgObject + 0xB8`
- raw material pointer candidates around `BgObject + 0xC0`

### Layer / SharedGroup / Event Context

The current runtime-accessible probe records:

- layer pointer
- layout pointer
- LayoutProbe `Source`
- LayoutProbe `LayerAddress`
- LayoutProbe key

It does not yet prove whether the instance is driven by a `SharedGroup`, event controller, AET controller, or timeline controller. The 60-frame diff is meant to identify whether the public/raw BgObject-side pointers change in the original object while remaining static in the recreated instance.

### Material / Shader

The probe records material pointer candidates before/after:

- 60-frame sampling
- explicit `UpdateMaterials()` comparison

`UpdateMaterials()` is exposed as a controlled comparison button. It does not call `CleanupRender` and does not copy unknown controller pointers.

## How To Read Results

The important comparison is field variability across 60 frames.

If original object changes but recreated local instance does not:

- changing `cachedMatrices` suggests render matrix/controller activity
- changing `cachedTransform` suggests a transform/controller update path
- changing `animationData` suggests an animation data object or timeline-like controller
- changing `stainOrBgChangeData` or material candidates suggests material, UV, texture, or BG change data driving the visible effect

If none of these fields change but the original object visibly animates, the likely driver is deeper than the currently dumped pointers: render material state, shader constants, a parent shared group/event update controller, or an external layout update callback.

## Current Conclusions

1. The original object is probably not animated by `ModelResourceHandle.FileName` alone. Recreate can replace a static mesh, but animated/dynamic resources need additional update context.
2. The missing piece may be `animationData`, material/BG change data, a layer/shared group/event controller, or an update listener not recreated by `CreatePrimary`.
3. `CollisionUpdateListener` is readable but unrelated to visual animation unless collision and visual state share a broader layout update path.
4. Controller/listener pointers must not be copied blindly. They likely reference owning layer/group state and may not be valid for another instance.
5. The smallest next experiment, after enough samples, is to identify one pointer that changes on the original object and is absent/static on the local instance, then locate its owning type and safe initialization path. Direct pointer copying remains out of scope.

## Open Questions

- Does the original animated BgPart have non-null `animationData` while the recreated instance has null?
- Does `stainOrBgChangeData` change every frame for dynamic material models?
- Are AET and screen-like models driven by `SharedGroupLayoutInstance` or event object controllers rather than the BgPart itself?
- Does `UpdateMaterials()` change any material candidate pointer, or is it only refreshing already-bound material state?

