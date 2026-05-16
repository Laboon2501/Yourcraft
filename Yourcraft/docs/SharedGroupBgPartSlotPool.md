# SharedGroup BgPart Slot Pool Expansion

## Goal

Meddle can show entries such as:

```text
SharedGroup -> BgPart[4] -> bg/.../x6t1_ad_scr00.mdl
```

Yourcraft previously only added the top-level `SharedGroupLayoutInstance` row and did not expose its children as selectable BgPart slots. That meant the BgPart Slot Pool could not find child-only models such as `x6t1_ad_scr00.mdl`.

## Implementation

`LayoutProbeService` now recursively expands:

```text
SharedGroupLayoutInstance.Instances.Instances
  -> ChildNodeInstance.Instance
    -> ILayoutInstance
```

Child instances are parsed through the same path as top-level layout instances. If the child is a `BgPartsLayoutInstance`, it is added as:

- `Type = BgPart`
- `SourceKind = SharedGroup`
- `SharedGroupPath = parent shared group primary path`
- `ParentAddress = parent SharedGroupLayoutInstance address`
- `ParentKey = parent key`
- `ChildIndex = child index`

Top-level BgParts remain:

- `SourceKind = LoadedLayout`

## UI

The BgPart selector now shows:

- source
- resource path
- address
- parent shared group path/address
- child index

The BgPart Slot Pool grouping shows how many slots are from:

- `LoadedLayout`
- `SharedGroup`

Search now includes:

- resourcePath
- type
- address
- source kind
- shared group path
- parent address/key
- debug info

## Collision Source Resolver

No special write path was added. SharedGroup child BgParts are normal `LayoutProbeInstance` rows with `Type=BgPart` and real child `BgPartsLayoutInstance` addresses, so the existing collision source resolver can use them as target collision sources.

## Safety

This is read-only expansion of existing SharedGroup children.

It does not:

- modify SharedGroup containers
- call SharedGroup recreate
- add or remove child nodes
- write resource paths

