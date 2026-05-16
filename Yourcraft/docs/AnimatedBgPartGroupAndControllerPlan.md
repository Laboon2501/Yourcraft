# Animated BgPart Group / Controller Plan

## Current Split

Dynamic BgPart failures now split into two common classes instead of path-specific hacks.

1. SharedGroup multi-BgPart visible animation
   - Meddle-style layout scanning can expose SharedGroup child BgParts.
   - Screen/ad style objects may be several same-position BgParts whose `visible` state cycles.
   - The probe samples all children of one SharedGroup parent for 60 frames and records active child sequences.

2. Controller-driven transform animation
   - Aetheryte/floating/rotating objects can show per-frame transform changes on their original layout instance.
   - A recreated local instance may be overwritten by a runtime controller or layout update.
   - Local instances now record immediate, +1, +5, and +30 frame transform readbacks after each write.

## Creation Failure Diagnostics

LocalLayoutObjectInstance records:

- source kind: LoadedLayout or SharedGroup
- parent SharedGroup path/address and child index when applicable
- whether a transform write was skipped
- applied target position
- immediate readback
- readback after 1, 5, and 30 frames
- whether the transform appears controlled/overwritten by runtime

If `UnsafeComplexModel` or invalid render state blocks transform writes, the instance now keeps that reason in UI state rather than silently remaining at the source position.

## SharedGroup Visibility Cycling Probe

The probe does not write any native state. It:

- resolves the selected SharedGroup parent, or the parent of a selected SharedGroup child
- lists all child BgParts
- records child resource path, address, visible state, transform, GraphicsObject, and material pointer candidates
- samples 60 frames of visible children
- marks the group as a `VisibilityCycling` candidate when multiple active-child sequences appear

## Local Generation Design

If a SharedGroup is confirmed as `VisibilityCycling`, the intended local implementation is:

1. Create a `LocalAnimatedGroupInstance`.
2. Occupy N available BgPart slots.
3. Recreate each occupied slot to one child `resourcePath`.
4. Apply the same VisualOnly transform to every child.
5. Playback the sampled visible sequence in the plugin by toggling visibility per child.

The first version should be VisualOnly only. FullLayout collision for animated groups should be disabled or assigned only to an explicit primary child until collision behavior is proven.

## Controller-Driven Transform Playback Design

For transform-driven objects, the safe path is not to copy controller/listener pointers.

The probe should sample source transform sequences for 60/300 frames and classify:

- pure rotation
- circular orbit
- bobbing
- transform curve

Playback should use:

`local transform(t) = local base transform + (source transform(t) - source base transform)`

If the local instance is overwritten after a write, it should be marked `ControlledByRuntime=true`; a later playback mode can deliberately write VisualOnly transform every frame with throttling.

## Safety Boundaries

- Do not copy controller/listener pointers.
- Do not write SharedGroup containers.
- Do not modify the original group.
- Do not call CleanupRender.
- Do not batch unknown native writes.
- Do not hardcode model paths; classify by parent relationship, child count, visible cycling, and transform/material changes.
