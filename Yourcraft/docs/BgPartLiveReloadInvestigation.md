# BgPart Live Reload / Reinitialize Investigation

## Current Baseline

The current single-instance mdl experiment has converged:

- `BgObject.SetModel(...)` can return `true`.
- `ModelResourceHandle.FileName` changes to the target `.mdl`.
- The visible mesh does not change.
- `UpdateRender`, `UpdateMaterials`, `UpdateTransforms`, `NotifyTransformChanged`, `ComputeSphereBounds`, and bounds-first `UpdateCulling` are not enough to rebuild the visible mesh.
- `CleanupRender` can make the model disappear and make later transform writes unsafe, so it is disabled.

This matches the observed Penumbra furniture/resource behavior: actor redraw works because a character redraw has a known draw-object rebuild path, while layout/BgPart resources appear to bind their visible mesh during layout load or object creation.

## Source Findings

### `BgPartsLayoutInstance`

Source: `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Layer/BgPartsLayoutInstance.cs`

Relevant fields:

- `GraphicsObject` at `0x30`: `Client::Graphics::Scene::BgObject*`
- `Collider` at `0x38`
- collision CRC/material fields
- `CollisionUpdateListener` at `0x60`

No public safe method is exposed on `BgPartsLayoutInstance` itself for "reload model" or "recreate graphics object".

### `ILayoutInstance`

Source: `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/ILayoutInstance.cs`

Potential but risky virtual functions:

- `Init(void* creator, byte* primaryPath)`
- `Deinit()`
- `SetProperties(FileLayerGroupInstance* data)`
- `SetLayer(LayerManager* layer)`
- `GetPrimaryPath()`
- `HavePrimary()`
- `GetGraphics()`
- `SetGraphics(Graphics.Scene.Object* obj, Transform* transform)`
- `CreatePrimary(Transform* transform, void* pathOrType)`
- `DestroyPrimary()`
- `SetActive(bool active)`

These are promising for a true reload path, but they are not safe to call blindly. They may affect collision, layer membership, object lifetime, or internal layout maps.

### `LayoutManager` / `LayerManager`

Sources:

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/LayoutManager.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Layer/LayerManager.cs`

Relevant fields:

- `LayoutManager.InitState`: `7` means fully loaded.
- `LayoutManager.Layers`
- `LayoutManager.InstancesByType`
- `LayerManager.Instances`
- `LayoutManager.LvbResourceHandle`
- `LayerGroupResourceHandles`

No public per-layer reload method is exposed. Reloading whole layers or maps would be out of scope and unsafe for this plugin.

### `BgObject`

Source: `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/BgObject.cs`

Relevant fields/methods:

- `ModelResourceHandle` at `0x90`
- `CachedTransformMatrices` at `0xA0`
- `StainBuffer` at `0xA8`
- `CachedTransform` at `0xB0`
- `LoadedAnimationData` at `0xB8`
- `LoadAnimationData(modelResourcePath)`
- `ResetFlags()`
- `SetModel(ResourceCategory*, modelResourcePath)`
- static `Create(modelGamePath, poolName, existingAllocation = null)`

`SetModel` updates the model resource handle path but does not prove that the existing render mesh is rebuilt.

### `DrawObject`

Source: `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/DrawObject.cs`

Relevant virtual functions:

- `UpdateCulling`
- `UpdateTransforms`
- `UpdateMaterials`
- `ComputeSphereBounds`
- `ComputeAxisAlignedBounds`
- `ComputeOrientedBounds`
- `GetAttachBoneWorldTransform`
- `SetTransparency`

These are render update/bounds/material helpers, not confirmed mesh recreation APIs.

IDA metadata also shows `Client::Graphics::Render::RenderManager_CreateModel` / `CreateModel` and `SetupModelAttributes`, but no safe managed binding from a live `BgObject` to recreate its render model in place.

## Why Map Reload Likely Works

Map or housing reload likely reruns the layout load pipeline:

1. layer data is read,
2. `BgPartsLayoutInstance` creates/initializes its primary graphics object,
3. `BgObject.Create` / model resource loading happens,
4. render model/culling/material structures are attached,
5. collision is initialized separately.

If `SetModel` only changes `ModelResourceHandle`, the existing visible mesh may still point at already-created render data. That would explain why leaving and re-entering the map applies the changed resource while live refresh does not.

## Candidate Experiments

These are recorded in the Debug UI as buttons, but they only update plugin status and do not call native methods yet:

1. `SetModel -> mark layout object dirty`
2. `SetModel -> hide/show BgObject`
3. `SetModel -> disable/enable instance`
4. `SetModel -> remove from render list -> re-add`
5. `SetModel -> reload single BgPart render object`

## Risk Notes

High-risk entries:

- `ILayoutInstance.Deinit`
- `ILayoutInstance.Init`
- `ILayoutInstance.DestroyPrimary`
- `ILayoutInstance.CreatePrimary`
- `ILayoutInstance.SetGraphics`
- layer `Instances` map mutation
- `LayoutManager.InstancesByType` mutation

These can invalidate collision, layer membership, and transform state. They should not be called until a known first-party call sequence is identified.

## Next Minimal Research Step

Do not continue SetModel refresh chains. The next useful work is:

1. Find xrefs/call sequence for `BgPartsLayoutInstance.CreatePrimary`.
2. Find xrefs/call sequence for `BgPartsLayoutInstance.DestroyPrimary`.
3. Determine whether `CreatePrimary` accepts the primary model path or a typed internal descriptor.
4. Determine whether `SetGraphics` is used only during creation or can safely replace a live `GraphicsObject`.
5. Identify whether `BgObject.Create(..., existingAllocation)` is safe to use with a live `BgObject` allocation, or only as a constructor-time helper.

Until then, per-instance mdl replacement remains paused and must not enter create/delete/restore/batch flows.
