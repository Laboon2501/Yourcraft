# Standalone BgObject scene attach / render registration investigation

Date: 2026-05-14

This document covers v11.2. The stable slot-backed BgPart copy flow is unchanged. This pass only adds read-only scene attach comparison and two Debug-only, double-confirmed native experiments for standalone `BgObject` objects.

## Current observed state

`BgObject.Create(modelPath, poolName, null)` can return a valid `Graphics.Scene.BgObject*`:

- object address is non-zero
- vtable is in `ffxiv_dx11.exe`
- `ModelResourceHandle` is non-null
- `LoadState == 7`
- `IsVisible == true`
- `Graphics.Scene.Object.Position` can be written and read back
- `NotifyTransformChanged`, `UpdateTransforms`, `UpdateMaterials`, `UpdateRender`, and bounds-first `UpdateCulling` do not make it visible

This strongly suggests the missing step is scene graph / world registration, not model resource loading or transform storage.

## New read-only comparison

The Debug UI now has:

- `Standalone vs 真实 BgPart scene attach 对比`

It compares:

- object address
- vtable
- object / draw / outline flags
- visible / transform-changed
- position / rotation / scale
- model resource handle and path
- cached matrices / cached transform / stain buffer / animation data
- parent / child / previous sibling / next sibling
- parent chain and root candidate
- whether `prev->next == this`
- whether `next->prev == this`
- whether `parent->child` circular list can actually traverse to `this`
- whether standalone and reference BgPart appear to share the same root
- sphere bounds center/radius when `ModelResourceHandle.LoadState >= 7`

The important change is that parent/prev/next are no longer treated as meaningful just because they are non-zero. The probe checks circular sibling list integrity.

## Function body evidence

Local source:

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/Object.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/BgObject.cs`
- `D:\ffxiv\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe`
- matched symbol data: `.research/FFXIVClientStructs/ida/old/data_2026.04.21.0000.0000.yml`

Relevant addresses from the matched data:

- `Graphics.Scene.Object.AddChild = 0x14041E7C0`
- `Graphics.Scene.Object.OnAddedToWorld = 0x14041E8B0`
- `Graphics.Scene.BgObject.Create = 0x14079A830`

### `Object.AddChild(parent, child)`

The function body shows it is not a passive setter. It edits circular sibling links.

Key behavior:

- reads `child->ParentObject`
- if the old parent child head is `child`, rewrites old parent `ChildObject`
- unlinks child from old sibling ring through `child->PreviousSiblingObject` and `child->NextSiblingObject`
- resets child `prev/next` to itself
- clears child parent
- if new parent has no child, sets `parent->ChildObject = child`
- otherwise inserts child into the new parent's circular child list
- sets `child->ParentObject = parent`

Implication:

Calling `AddChild` on a malformed or already-owned standalone object can rewrite scene graph links. The UI therefore exposes this only as a single-object, double-confirmed experiment.

### `Object.OnAddedToWorld(this)`

The function body starts by checking `ObjectFlags & 1`.

Observed behavior:

- if bit 0 is not set, it returns immediately
- reads a global pointer via `rip + 0x24D88B3`
- reads/writes a linked list pointer at global object `+0x40`
- masks `ObjectFlags` down to low bits, then ORs in the global pointer
- writes `this + 0x40` and rewires the previous list head
- clears low bit 0 from `ObjectFlags`

Implication:

`OnAddedToWorld` looks like it registers an object into a world/global object list only when a pending flag is set. If a standalone object is created without the expected flag, calling it may be a no-op. If the flag is set incorrectly, it may mutate a global list. This remains Debug-only.

### `BgObject.Create`

The confirmed chain includes:

```text
if existingAllocation == null:
  allocate 0xE0 bytes
  initialize BgObject
ResetFlags
AddChild(globalSceneParent?, bgObject)
OnAddedToWorld(bgObject)
build model path helper
SetModel(modelPath)
return BgObject*
```

Earlier docs incorrectly treated `0x14041E7C0` / `0x14041E8B0` as unnamed helpers. Function-body evidence now identifies them as `Object.AddChild` and `Object.OnAddedToWorld`.

The fact that `BgObject.Create` already calls both helpers but the standalone object remains invisible means the issue may be:

- the parent used by `BgObject.Create` is not the active render scene root for the current territory,
- the object is added to a global object list but not submitted to the current visible/culling scene,
- bounds/culling are still invalid,
- the global list link stored in `ObjectFlags` differs from visible BgPart objects,
- a layout `SetActive` / `SetTransformImpl` / culling manager step is still missing.

## New high-risk experiments

Both buttons are default-disabled and require:

- `UnsafeMode == true`
- normal Standalone high-risk confirmation
- extra scene attach / AddChild confirmation
- selected standalone state is `ValidatedReadOnly` or `PositionWriteSucceeded`
- read-only validation passes immediately before the call

Buttons:

1. `Standalone 调用 OnAddedToWorld 实验`
2. `将 Standalone AddChild 到真实 BgPart parent`

The AddChild experiment:

- uses the currently selected real BgPart only as a parent source,
- reads `realBgPart.GraphicsObject->ParentObject`,
- verifies the parent and standalone pointers look like `Graphics.Scene.Object`,
- checks whether the parent child list already contains the standalone,
- calls `parent->AddChild(standalone)` once,
- dumps link integrity after the call.

No CleanupRender, Dtor, DestroyPrimary, CreatePrimary, SetGraphics, LayoutManager write, LayerManager write, collision, or batch action is used.

## Next decision points

If `OnAddedToWorld` or `AddChild` makes the object visible:

- record which poolName was used,
- compare parent/root/list integrity after success,
- identify whether only one helper was required or both.

If it is still invisible:

- focus on the parent selected by `BgObject.Create`,
- compare the objectFlags root candidate against a visible BgPart,
- look for culling/render scene submit lists beyond `Object.ParentObject`,
- investigate whether `SetActive` from `BgPartsLayoutInstance.CreatePrimary` is the missing step.

## v11.3: bounded scan and bounds/culling follow-up

Observed user result:

- Standalone and real BgPart can report the same scene root.
- Standalone parent/prev/next are non-null.
- Standalone `parent->child` scan did not find the standalone object.
- Real BgPart `parent->child` scan did find the real object.
- Standalone sphere bounds stayed near `(0,0,0)` while real BgPart bounds followed the world position.

### Debug stall fix

The scene attach probe previously risked doing too much work from UI-visible dump paths. v11.3 changes the behavior:

- automatic validation now writes only a lightweight dump;
- heavy scene attach comparison only runs from explicit button clicks;
- parent child scanning has both count and time limits;
- default scan uses at most 64 nodes and a small time budget;
- full scan is a separate button capped at 2048 nodes;
- scan output records hit, hit index, total scanned, truncated, end reason, and elapsed milliseconds;
- main module executable ranges are cached so pointer validation does not enumerate process modules for every scanned child.

### Attach state classification

Standalone objects now classify as:

- `LinkedAndContained`: parent, prev, and next are valid and parent child list contains this object.
- `LinkedButNotContained`: parent, prev, and next are valid but the parent child list does not contain this object.
- `Detached`: parent/prev/next are missing or invalid.
- `Invalid`: object pointer itself does not look like a `Graphics.Scene.Object`.

This is meant to distinguish "field is non-zero" from "actually attached to the parent's child ring."

### New guarded steps

New buttons:

- `Standalone bounds/culling rebuild`
  - sets transform changed,
  - calls `NotifyTransformChanged`,
  - calls `UpdateTransforms(true)`,
  - calls `ComputeSphereBounds`,
  - records distance from bounds center to position,
  - then calls `UpdateCulling` and `UpdateRender`.

- `Standalone OnAddedToWorld -> Update chain`
  - calls `OnAddedToWorld`,
  - then `UpdateTransforms(true)`,
  - `ComputeSphereBounds`,
  - bounds-first `UpdateCulling`,
  - `UpdateRender`.

- `Standalone AddChild 到真实 BgPart parent`
  - only shown behind Unsafe + normal Standalone confirmation + extra scene attach confirmation,
  - only allowed when attach state is `Detached` or `LinkedButNotContained`,
  - refuses repeat AddChild if the parent child list already contains the object,
  - calls AddChild, then OnAddedToWorld and update chain.

No CleanupRender, Dtor, DestroyPrimary, CreatePrimary, SetGraphics, LayoutManager/LayerManager writes, collision, or batch standalone operation was added.
