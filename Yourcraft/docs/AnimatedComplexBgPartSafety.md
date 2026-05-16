# Animated / Complex BgPart Safety Notes

## Current Working Path

Static `bg/...mdl` and `bgcommon/...mdl` replacement uses:

1. `DestroyPrimary()`
2. `CreatePrimary(original-or-current transform, target mdl path)`
3. Re-read the new `GraphicsObject`
4. Apply transform through `Graphics.Scene.Object Position / Rotation / Scale`

For VisualOnly mode, transform is now delayed by several framework frames after recreate. The plugin re-reads the new `GraphicsObject` before writing transform and refuses to write if the object is not stable.

## Safety Checks Added

Before VisualOnly transform writes after recreate:

- `GraphicsObjectAddress != 0`
- New `GraphicsObject` is re-read after `CreatePrimary`; old pointers are not reused
- `ModelResourceHandle != null`
- `LoadState == 7`
- `IsVisible == true`
- `Position / Rotation / Scale` are readable
- Transform values are finite and within a sane coordinate range
- High-risk dynamic paths are blocked from automatic transform writes

If any check fails, the instance is marked with `UnsafeAfterRecreate` or `UnsafeComplexModel`, transform writes are disabled, and the UI asks the user to restore or change map.

## Risk Path Rules

Soft high-risk paths:

- `/vfx/`
- `/light/`
- `/shared/`
- `/evt/`
- `/twn/` with file names containing `scr`, `screen`, `monitor`, or `ad`

These are treated as `UnsafeComplexModel`; recreate may update the model handle, but automatic transform writes are blocked.

`/aet/` paths are treated as `AnimatedStaticOnly`: the visual model may appear, but its animation/material controller is not currently recreated.

## Animation Evidence

The plugin now dumps:

- `ModelResourceHandle` path/category/load state
- visibility
- `Graphics.Scene.Object` transform
- `cachedMatrices` at `BgObject + 0xA0`
- `stainOrBgChangeData` at `BgObject + 0xA8`
- `cachedTransform` at `BgObject + 0xB0`
- `animationData` at `BgObject + 0xB8`

These are shown for the selected original BgPart and the local replaced instance so animated source objects can be compared against recreated local instances.

## Current Animation Conclusion

Animated or dynamic material models can show as static after recreate because their motion appears to depend on original layout/shared group/event update context, not only the `BgObject` model path and transform. Yourcraft does not yet recreate that controller chain.

UI wording should remain explicit:

> 自带动画/动态材质模型可能只显示静态外观；动画需要原 layout controller/shared group/event update 支持，暂未支持。

