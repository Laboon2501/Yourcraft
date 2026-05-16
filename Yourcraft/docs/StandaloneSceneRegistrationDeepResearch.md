# Standalone BgObject scene registration deep research

Date: 2026-05-14

This note records v11.4. The slot-backed BgPart copy system is unchanged. Standalone `BgObject` is still Debug-only and still does not touch Layout/Layer containers.

## Current facts

`BgObject.Create(modelPath, poolName, null)` can create a readable object:

- `objectAddress` is non-zero.
- vtable is in the main module.
- `ModelResourceHandle != null`.
- `LoadState == 7`.
- `IsVisible == true`.
- `Graphics.Scene.Object.Position` can be written and read back.
- `ComputeSphereBounds()` can produce a bounds center at the target position.

However the object is still not visible in the game scene.

The important comparison result is:

- Standalone and a real BgPart can report the same root candidate.
- Standalone `ParentObject`, `PreviousSiblingObject`, and `NextSiblingObject` are non-null.
- The parent's child-ring scan does not find the standalone object.
- The same scan does find the real BgPart.
- After the AddChild experiment, `parentContainsThis` still stayed false and `objectFlags` became abnormal.

Because of this, all scene attach write experiments are now paused.

## UI / safety change

Disabled or hidden:

- `Standalone AddChild 到真实 BgPart parent`
- `Standalone 调用 OnAddedToWorld`
- `Standalone OnAddedToWorld -> Update chain`
- any button that writes parent/child/prev/next scene links

Still available:

- CreateOnly
- Dump
- Validate
- Position single-field write
- Bounds / culling readback steps
- Standalone vs real BgPart comparison
- Raw `+0x00` to `+0xA0` layout dump / offset comparison

The service layer also hard-blocks `ExecuteSceneAttachStep`, so stale UI or accidental calls cannot invoke AddChild / OnAddedToWorld.

## Field layout confirmation

Local FFXIVClientStructs sources:

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/Object.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/DrawObject.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/BgObject.cs`

Relevant offsets:

```text
0x00  vtable
+0x18 Object.ParentObject
+0x20 Object.PreviousSiblingObject
+0x28 Object.NextSiblingObject
+0x30 Object.ChildObject
+0x38 Object.ObjectFlags
+0x50 Object.Position
+0x60 Object.Rotation
+0x70 Object.Scale
+0x80 sizeof(Object)
+0x88 DrawObject.Flags
+0x89 DrawObject.OutlineFlags
+0x90 BgObject.ModelResourceHandle
+0xA0 BgObject.CachedTransformMatrices
+0xA8 BgObject.StainBuffer
+0xB0 BgObject.CachedTransform
+0xB8 BgObject.LoadedAnimationData
+0xE0 sizeof(BgObject)
```

So `ObjectFlags` at `+0x38` is the correct generated offset. A huge value is not evidence that the offset is wrong by itself, because `OnAddedToWorld` appears to store pointer-like state in the high bits of `ObjectFlags`.

v11.4 adds a raw layout dump that prints:

- raw bytes from `+0x00` through `+0xBF`
- `ObjectFlags@+0x38`
- link fields at `+0x18/+0x20/+0x28/+0x30`
- draw flags at `+0x88/+0x89`
- model handle at `+0x90`
- cached/render-related pointers at `+0xA0/+0xA8/+0xB0/+0xB8`
- byte diff against the selected real BgPart

This is only readback. It does not execute AddChild or OnAddedToWorld.

## Function-body evidence from previous research

Matched symbol file:

```text
.research/FFXIVClientStructs/ida/old/data_2026.04.21.0000.0000.yml
```

Known addresses:

```text
BgPartsLayoutInstance.CreatePrimary = 0x14073FC00
BgPartsLayoutInstance.DestroyPrimary = 0x14073FCD0
BgObject.Create = 0x14079A830
Object.AddChild = 0x14041E7C0
Object.OnAddedToWorld = 0x14041E8B0
```

### BgObject.Create helpers

Earlier unknown helpers in `BgObject.Create` are now identified as:

- `0x14041E7C0` = `Graphics.Scene.Object.AddChild`
- `0x14041E8B0` = `Graphics.Scene.Object.OnAddedToWorld`

`BgObject.Create` therefore already calls scene-link related helpers during creation. The standalone object still being invisible means the problem is not simply "call AddChild once."

### Object.AddChild

Function-body notes indicate `AddChild` is a member function on the parent object and rewires circular sibling links. It:

- reads the child's old parent
- unlinks from the old sibling ring
- resets child sibling pointers
- inserts into the new parent's child ring
- writes `child->ParentObject = parent`

This is not a harmless setter. Calling it when the standalone object is already partially linked, or when the inferred parent is not the expected render scene parent, can corrupt link state. The v11.3 experiment suggests the current call path is unsafe: parent scan still did not contain the standalone, while `objectFlags` became abnormal.

### Object.OnAddedToWorld

Function-body notes indicate `OnAddedToWorld` is guarded by `ObjectFlags & 1`. It appears to interact with a global/world object list and store pointer-like data in the high bits of `ObjectFlags`.

This makes direct external calls unsafe without proving the expected flag state and world-list ownership. It may be a virtual lifecycle callback that is only valid when called from the engine's normal object creation path.

### BgPartsLayoutInstance.CreatePrimary

Confirmed chain:

```text
CreatePrimary(this, transform, pathOrType)
  -> modelGamePath = *(char**)pathOrType
  -> poolName = "Client.LayoutEngine.Layer.BgPartsLayoutInstance"
  -> existingAllocation =
       if IndexInPool != -1:
         LayoutManager.BgObjectPool + IndexInPool * 0xE0
       else:
         null
  -> BgObject.Create(modelGamePath, poolName, existingAllocation)
  -> this->GraphicsObject = result
  -> SetTransformImpl(transform)
  -> conditionally SetActive(...)
```

`SetGraphics` is not called by `CreatePrimary`; it only writes `this+0x30`, copies transform into `Graphics.Scene.Object`, and ORs `ObjectFlags |= 2`. It does not register render/culling.

## Answers required by v11.4

### 1. Why did AddChild not make parentContainsThis true?

Most likely because the inferred parent or call preconditions are wrong. `BgObject.Create` had already called AddChild/OnAddedToWorld internally, but the standalone still was not in the reference parent child-ring. Calling `AddChild` again against a real BgPart parent did not repair membership, which suggests one of:

- the selected real BgPart parent is not the correct owner/list for standalone objects;
- the standalone's existing sibling ring was already inconsistent;
- the generated `AddChild` wrapper signature is correct as a member function, but this usage violates required object state;
- AddChild only handles a scene object child list, not the render submit/culling list that makes objects visible.

### 2. Is the objectFlags abnormal value caused by a bad call?

Likely yes. The `+0x38` offset is correct. `OnAddedToWorld` appears to use `ObjectFlags` as a bitfield plus pointer-like registration state. A huge value after AddChild/OnAddedToWorld means the object may have been linked or unlinked through a wrong lifecycle path, not that `ObjectFlags` was read from the wrong offset.

### 3. What are helper 0x14041E7C0 / 0x14041E8B0?

They match:

- `0x14041E7C0`: `Object.AddChild`
- `0x14041E8B0`: `Object.OnAddedToWorld`

They are not arbitrary render-mesh submit helpers.

### 4. Does real BgPart visibility depend on LayoutInstance.SetActive?

Very likely. The real `CreatePrimary` path continues after `BgObject.Create`:

```text
this->GraphicsObject = result
SetTransformImpl(transform)
SetActive(...)
```

Standalone creation has no `ILayoutInstance`, so it has no equivalent `SetTransformImpl` / `SetActive` owner state. This is the strongest current explanation for "valid object, valid resource, valid bounds, but no visible mesh."

### 5. What is Standalone missing without LayoutInstance?

Likely missing one or more of:

- active state owned by `ILayoutInstance.SetActive`;
- culling/render submit registration that is normally triggered from layout update;
- correct layout pool / object manager ownership;
- a valid parent child-ring under the same list the active scene scans;
- lifecycle state that `OnAddedToWorld` expects to be set before it runs.

### 6. Next minimum safe experiment

Do not call AddChild or OnAddedToWorld again.

Next research should be function-body only:

1. Disassemble `ILayoutInstance.SetActive` / `BgPartsLayoutInstance.SetActive` call target from the matched vtable.
2. Disassemble `BgPartsLayoutInstance.SetTransformImpl`.
3. Identify whether `SetActive` registers `GraphicsObject` with a render/culling manager, or only toggles existing state.
4. Find whether the registration target can be read-only dumped from a real BgPart.

Only after that, design a single-object experiment that does not write scene parent/sibling links directly.

## Current prohibition

Until the function-body evidence is complete, do not call:

- AddChild
- OnAddedToWorld
- CleanupRender
- Dtor
- DestroyPrimary / CreatePrimary for standalone
- SetGraphics
- LayoutManager / LayerManager writes
- memcpy
- batch standalone creation
- collision / CreateSecondary

## v11.5: deeper scene registration conclusion

Current hard conclusion:

`BgObject.Create(modelPath, poolName, null)` creates a resource-complete and field-complete `BgObject`, but that alone is not enough to enter the visible render submit chain.

Observed proof:

- `ModelResourceHandle` is valid.
- `LoadState == 7`.
- `visible == true`.
- `Position` can be written and read back.
- `ComputeSphereBounds` can calculate a bounds center at the target position.
- The object is still invisible.
- A real BgPart is reachable from its parent's child list, but the standalone object is not.

### UI state in v11.5

Write-like standalone scene registration experiments are not exposed:

- AddChild to real BgPart parent: hidden.
- OnAddedToWorld: hidden.
- AddChild -> OnAddedToWorld -> update chain: hidden.
- Parent/child/prev/next write buttons: hidden.
- Render/update activation writes such as `NotifyTransformChanged`, `UpdateTransforms`, `UpdateRender`, and `UpdateCulling`: hidden.

The service also blocks stale calls:

- `ExecuteSceneAttachStep` always fails with the paused-reason message.
- `ExecuteActivationStep` only allows `ComputeSphereBounds`; other activation steps are rejected.

Remaining standalone tools:

- CreateOnly.
- Dump.
- Validate.
- Position single-field write.
- ComputeSphereBounds bounds dump.
- Standalone vs real BgPart comparison.
- Raw `+0x00..+0xBF` offset dump.
- Full parent child chain scan only by explicit button and bounded by count/time.

### AddChild signature and call conditions

FFXIVClientStructs generated signature:

```csharp
public partial void AddChild(Object* child);
```

Function address from matched symbols:

```text
Object.AddChild = 0x14041E7C0
```

The generated member function means:

```text
rcx = parent / this
rdx = child
r8/r9 = not part of the public generated signature
```

Function-body behavior from previous disassembly:

1. Reads the child object's current `ParentObject`.
2. If needed, removes the child from its old parent child ring.
3. Rewrites `PreviousSiblingObject` and `NextSiblingObject`.
4. Inserts the child into the new parent's circular child list.
5. Writes `child->ParentObject = parent`.

Fields touched or relied on:

```text
+0x18 ParentObject
+0x20 PreviousSiblingObject
+0x28 NextSiblingObject
+0x30 ChildObject
```

It is a real scene-object link mutator, not a render registration function. It should only be called when the child has a valid, expected detached or owned-link state. The standalone object created by `BgObject.Create(null)` already has non-null parent/prev/next, but it is not reachable from the real parent child ring. That is an inconsistent or at least non-obvious state.

Why the previous AddChild experiment did not work:

- The standalone was not actually detached; it already had sibling links.
- The selected real BgPart parent may not be the correct root/list for standalone objects.
- AddChild can repair `Object.ParentObject` style ownership but may not insert into the culling/render submit list.
- If the wrong parent/list was used, AddChild may rewrite links without making the object visible.
- The post-call `objectFlags` abnormality means this call path is not safe enough to continue.

Minimum safe AddChild condition, if ever revisited:

- Prove the target parent is the exact parent used by the engine for the newly created standalone object.
- Prove the child is detached or safely removable from its old list.
- Prove `parent->child` traversal will include the object after a dry-run-level analysis.
- Prove no render/global-list pointer bits in `ObjectFlags` are invalid.

Current decision: not suitable for standalone use.

### OnAddedToWorld signature and call conditions

FFXIVClientStructs generated signature:

```csharp
public partial void OnAddedToWorld();
```

Function address from matched symbols:

```text
Object.OnAddedToWorld = 0x14041E8B0
```

Generated member function means:

```text
rcx = this
rdx/r8/r9 = not part of the public generated signature
```

Function-body observations from previous research:

- It first checks `ObjectFlags & 1`.
- If the pending bit is not set, it can return without doing useful work.
- It reads a global/world list pointer.
- It stores pointer-like data into the high bits of `ObjectFlags`.
- It rewires a global object/world list.
- It clears the low pending bit after registration.

This looks like an engine lifecycle callback, not a general external "make visible" command. It likely expects the object to be in a specific pre-registration state set by the creator or scene manager.

Current answer:

- It is technically a no-argument member function.
- It should not be externally called on arbitrary standalone objects.
- It does not by itself prove render/culling submit registration.
- It may mutate global object-list state if preconditions are wrong.

### BgObject.Create helpers

Matched helper addresses inside `BgObject.Create`:

```text
0x14041E7C0 = Object.AddChild
0x14041E8B0 = Object.OnAddedToWorld
```

This matters because standalone creation already runs these helpers. Re-running them manually is not the missing link.

`BgObject.Create` high-level behavior:

```text
if existingAllocation == null:
  allocate 0xE0 bytes
  initialize BgObject
ResetFlags
AddChild(...)
OnAddedToWorld()
SetModel(modelPath)
return BgObject*
```

It sets up an object and resource handle, but the result still does not become visible. Therefore the missing registration is after or outside this helper pair.

### Real BgPart visibility chain

`BgPartsLayoutInstance.CreatePrimary` is confirmed to do more than `BgObject.Create`:

```text
CreatePrimary(this, transform, pathOrType)
  -> modelGamePath = *(char**)pathOrType
  -> poolName = "Client.LayoutEngine.Layer.BgPartsLayoutInstance"
  -> existingAllocation =
       if IndexInPool != -1:
         LayoutManager.BgObjectPool + IndexInPool * 0xE0
       else:
         null
  -> BgObject.Create(modelGamePath, poolName, existingAllocation)
  -> this->GraphicsObject = returned BgObject*
  -> SetTransformImpl(transform)
  -> conditionally SetActive(...)
```

The critical difference is ownership by a live `ILayoutInstance` plus later active-state handling. The standalone object has no `ILayoutInstance`, no `this->GraphicsObject` owner slot, and no normal `SetActive` call path.

Current judgment:

Real BgPart visibility is very likely dependent on the `LayoutInstance.SetActive` path, or on state prepared by `SetTransformImpl` plus `SetActive`.

### What to inspect next

Do not add write buttons. Next work should be pure function-body research:

1. Disassemble the concrete `BgPartsLayoutInstance.SetActive` target for the current game build.
2. Disassemble `BgPartsLayoutInstance.SetTransformImpl`.
3. Search callsites of `OnAddedToWorld`.
4. Search callsites of `UpdateCulling` and render/culling-manager insertion routines.
5. Search for lists that real BgPart objects appear in but standalone objects do not.
6. Dump only read-only candidates for:
   - active render object list,
   - culling object list,
   - layout active list,
   - world object list.

### Next minimal safe experiment

No direct experiment yet.

The next safe step is to identify a read-only difference between a real BgPart and standalone in one of the active/render/culling lists. Only after that should a single-object experiment be designed. It should not write parent/child/prev/next directly.
