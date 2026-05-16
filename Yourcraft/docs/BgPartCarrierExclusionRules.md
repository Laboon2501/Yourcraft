# BgPart Carrier Exclusion Rules

## Goal

Local scene objects use slot-backed copy. The selected BgPart is only a template/source. A different BgPart slot is allocated as the carrier, recreated to the target mdl, transformed, and later restored from its own original snapshot.

The default policy is fixed as `PreferredListThenAnyValid`; the UI no longer exposes multiple carrier strategies.

1. Prefer free slots with the same `resourcePath` as the template.
2. If same-model slots are not enough, use slots/resource paths from the Preferred Modify list.
3. If that is still not enough, use any valid bg/bgcommon BgPart.

The Protected list always wins over the Preferred Modify list. Protected BgParts are never allocated as carriers and are not modified.

## Allocation Order

Same-model stage:

- `slot.resourcePath == template.resourcePath`
- `slotAddress != templateSlotAddress`
- not occupied
- not reserved
- not protected
- valid BgPart pointer
- valid GraphicsObject
- valid ModelResourceHandle

Sorting:

- distance descending
- address as stable tie-breaker

Preferred Modify stage:

- only used after the same-model stage
- matches a preferred slot before a preferred resource path
- not occupied, reserved, protected, or the template slot
- valid BgPart pointer, GraphicsObject, and ModelResourceHandle

Sorting:

- distance descending
- preferred slot before preferred resource path
- address as stable tie-breaker

AnyValidBgPart stage:

- only used after same-model and preferred modify stages
- accepts any valid bg/bgcommon BgPart that is not occupied, reserved, protected, or the template slot
- floor/wall/terrain/structure/SharedGroup/dynamic classifications are warnings, not hard rejects

Distance is always recomputed as `Vector3.Distance(playerPosition, slot.WorldPosition)` when a player position is available. The allocator must not use the current UI row order or nearest-first ordering for official allocation.

## Warning Patterns

Short tokens such as `flo`, `flr`, and `wal` are matched by path component. This avoids rejecting normal names such as `flow1`.

These patterns no longer block allocation by default. They are surfaced as `warningReason`; use the Protected list for objects that must never be modified.

FloorLike:

- `floor`
- `flo`
- `flr`
- `yuka`

WallLike:

- `wall`
- `wal`
- `kabe`

TerrainLike / scene foundation:

- `ceil`
- `ceiling`
- `tenjo`
- `roof`
- `ground`
- `gnd`
- `terrain`
- `land`
- `road`
- `base`
- `bgbase`
- `map`
- `sea`
- `water`
- `sky`
- `cliff`
- `rock_large`
- `foundation`
- `field_base`

StructureLike / architecture:

- `building`
- `bld`
- `house_base`
- `room`
- `room_base`
- `pillar_large`
- `arch_large`
- `bgcommon/hou/common/general/*/bgparts/com_b*_m*.mdl`
- `*/hou/*/bgparts/*_wall*.mdl`
- `*/hou/*/bgparts/*_floor*.mdl`
- `*/hou/*/bgparts/*_roof*.mdl`
- `*/hou/*/bgparts/*_ceil*.mdl`
- `*/hou/*/bgparts/*_base*.mdl`
- `*/hou/*/bgparts/*_m0*.mdl`

TooLarge:

- any scale component has absolute value greater than `20`

TooCloseImportantGeometry:

- distance to player is less than `8y`
- and the object appears structural or large

## Always Excluded

- `TemplateSlot`
- `Protected`
- `Occupied`
- `Reserved`
- `InvalidGraphicsObject`
- `InvalidModelHandle`
- `SharedGroupChild`
- `DynamicControlled`
- `UnsafeComplex`
- paths outside `bg/...mdl` or `bgcommon/...mdl`

## User Overrides

The UI exposes carrier blacklist and whitelist pattern fields.

- blacklist adds extra fallback rejects
- whitelist allows fallback carriers that were only rejected by structural rules
- whitelist does not override occupied, reserved, template slot, invalid graphics, SharedGroup child, or dynamic/controller-driven rejects

Patterns are simple substring matches. Separate multiple patterns with comma, semicolon, or newline.

## Protected BgParts

The protection list is persisted in plugin configuration:

- `ProtectedBgPartSlots`
- `ProtectedBgPartResourcePaths`

Protected entries cannot be used as carriers and cannot be modified by mdl recreate, transform writes, or collision writes. They may still be used as read-only templates or read-only collision sources.

Slot protection is matched by territory, resourcePath, source type, approximate original position, SharedGroup path/child index when present, and stable key/address when available. ResourcePath protection can be scoped to the current territory.

## Restore Requirement

Restore must use only `instance.OriginalSlotSnapshot.OriginalResourcePath` as the target. It must not use the template path, custom mdl, a global fallback path, or a default model.

Both VisualOnly and FullLayoutWithCollision restore:

- original mdl
- original layout transform
- original graphics transform
- original collision source / secondary state
- original visible state

## Current Limitations

Dynamic BgPart, SharedGroup child, and controller-driven objects are not supported as official copy targets or carriers. When selected, the UI should report that the object is likely controller/SharedGroup/dynamic-material driven and ask the user to choose a static BgPart or manually enter an mdl path.

Standalone BgObject / true spawn remains paused and is not part of the v11.8 official flow.
