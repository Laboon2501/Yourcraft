# BgObject.SetModel Crash Analysis

## Current Decision

`BgObject.SetModel` direct calls are paused.

Reason: the single-instance experiment still crashed inside `ApplyModel()`. This kind of native crash can bypass managed `try/catch`, so the plugin must not call `SetModel` from any normal flow.

The UI now keeps only:

- custom mdl path input
- model/resource readback fields
- read-only `modelResourceHandle` dump
- status/error records

The following flows must not call `BgObjectModelOverrideService.ApplyModel()`:

- instance creation
- batch copy
- delete
- restore all
- single-instance SetModel button
- single-instance restore-model button

## FFXIVClientStructs Evidence

Local FFXIVClientStructs XML exposes this `BgObject` method:

```csharp
BgObject.SetModel(ResourceCategory* modelResourceCategory, CStringPointer modelResourcePath)
```

The XML summary says it loads the model resource at the given path. The parameters are:

- `modelResourceCategory`: the resource category that contains the model resource
- `modelResourcePath`: the path of the model resource

The earlier experiment used:

```csharp
var category = default(ResourceCategory);
bgObject->SetModel(&category, modelPath);
```

That is not proven safe. The current crash strongly suggests `default(ResourceCategory)` or the call preconditions are wrong.

## ResourceCategory Status

`ResourceCategory` is referenced by the `SetModel` signature, but the local XML lookup did not expose named enum/member values for it.

Current conclusion:

- The valid category value for a BgPart `.mdl` is not confirmed.
- `default(ResourceCategory)` must be treated as unsafe.
- No code should guess category values and call `SetModel` in-game.

## Meddle Evidence

Meddle's layout pipeline reads BgPart models; it does not provide a safe write example for replacing a BgPart model.

Relevant Meddle read path:

```csharp
BgObject* graphics = (BgObject*)bgPart->GraphicsObject;
if (graphics == null || graphics->ModelResourceHandle == null)
    return null;

if (graphics->ModelResourceHandle->LoadState < 7)
    return null;

var modelPtr = (nint)graphics->ModelResourceHandle;
```

Meddle's custom `BgObject` struct records:

```csharp
[FieldOffset(0x00)] public FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject Base;
[FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
[FieldOffset(0xA8)] public BgObjectAdditionalData* BGChangeData;
```

This supports read-only model path/material inspection, not per-instance `SetModel` replacement.

## Current Safe Readback

`BgObjectModelOverrideService.Refresh()` is now read-only. It reads:

- `ModelResourceHandle` address
- `ModelResourceHandle.FileName`
- `BgObject.IsVisible`
- `BgObject.Position`
- `BgObject.Rotation`
- `BgObject.Scale`

It does not call:

- `SetModel`
- `NotifyTransformChanged`
- `UpdateMaterials`
- `UpdateRender`
- `UpdateTransforms`
- `ComputeSphereBounds`

## Paused UI Message

The UI displays:

```text
SetModel 直接调用已暂停：ResourceCategory / 调用签名未确认，会崩溃。
```

The SetModel and restore-model buttons are disabled.

## Next Safe Investigation

Before retrying any write path:

1. Find the real `ResourceCategory` values from FFXIVClientStructs source or generated interop output.
2. Find a first-party or proven third-party call site that passes a valid category to `BgObject.SetModel`.
3. Confirm whether the path must be a `CStringPointer`, `ReadOnlySpan<byte>`, or managed string overload in the current binding.
4. Confirm required object state:
   - `BgObject` is loaded
   - `ModelResourceHandle != null`
   - `LoadState >= 7`
   - resource path belongs to the matching category
5. Re-enable only a single-instance button after the above is known.

Until then, per-instance mdl replacement remains paused.
