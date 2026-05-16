# ResourceCategory / SetModel Investigation

## Scope

This pass is read-only. It does not call `BgObject.SetModel`, does not update render state, and does not add any write button.

## Local FFXIVClientStructs Search

Searched locations:

- Local project `obj/Debug` and `obj/x64/Debug`
- Local project source
- Local XIVLauncher dev XML: `%AppData%\XIVLauncher\addon\Hooks\dev\FFXIVClientStructs.xml`

Findings:

- The project `obj` folders do not contain generated FFXIVClientStructs source files.
- The local XML exposes `BgObject.SetModel` as:

```csharp
BgObject.SetModel(ResourceCategory* modelResourceCategory, CStringPointer modelResourcePath) -> bool
```

- XML docs describe `modelResourceCategory` only as "the resource category that contains the model resource".
- A direct lookup for `T:FFXIVClientStructs.FFXIV.Client.System.Resource.ResourceCategory` and `F:...ResourceCategory...` did not return named enum/member values in the XML.

Current conclusion:

- The `ResourceCategory` type exists in the generated binding because the project compiles against it, but named values were not found in the exposed XML/source searched here.
- `default(ResourceCategory)` remains untrusted.

## Local Plugin / Dependency SetModel Search

Searched:

- Yourcraft source and docs
- Local `.research/MeddleRetry/Meddle`
- Workspace files under `C:\Users\kiomo\Documents\New project`

Findings:

- No safe third-party call site for `BgObject.SetModel` was found.
- Meddle reads BgPart model resources, but does not use `SetModel`.
- Yourcraft now contains only paused methods and documentation references for `SetModel`.

## Meddle Read Pattern

Meddle's layout read path casts `BgPartsLayoutInstance.GraphicsObject` to a custom `BgObject` struct and reads the model handle:

```csharp
BgObject* graphics = (BgObject*)bgPart->GraphicsObject;
if (graphics == null || graphics->ModelResourceHandle == null)
    return null;

if (graphics->ModelResourceHandle->LoadState < 7)
    return null;

var modelPtr = (nint)graphics->ModelResourceHandle;
```

Meddle's custom layout:

```csharp
[FieldOffset(0x00)] public FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject Base;
[FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
[FieldOffset(0xA8)] public BgObjectAdditionalData* BGChangeData;
```

This supports Yourcraft's read-only model dump path.

## Runtime Read-Only Dump Added

For the selected `LocalLayoutObjectInstance`, the UI now shows:

- `ModelResourceHandle` address
- `ModelResourceHandle` vtable pointer
- `FileName`
- `Type`
- `FileType`
- `LoadState`
- `Id`
- transform readback from `BgObject.Position/Rotation/Scale`
- `SetModel` signature string
- inferred ResourceCategory
- confidence level

The dump does not read or write a confirmed `ResourceCategory` field because no such field was found on `ModelResourceHandle` in the public binding.

## Category Inference

Current inference is path-prefix based only:

- `bg/` or `bgcommon/`: likely a background/common scene resource category, but the exact `ResourceCategory` value is not known.
- `chara/`: likely a character resource category, not suitable for BgPart replacement.
- `vfx/`: likely a VFX resource category.

Trust level displayed in UI:

```text
低：当前只能由路径前缀推测，尚未读到 ResourceCategory 实例字段。
```

## Call Preconditions Still Unknown

Before `SetModel` can be safely retried, these must be known:

1. Exact `ResourceCategory` value for BgPart `.mdl` resources.
2. Whether the category must be copied from an existing resource manager/category object rather than default constructed.
3. Whether the binding overload requires `CStringPointer`, `ReadOnlySpan<byte>`, or managed string in this Dalamud/FFXIVClientStructs version.
4. Required object state before call:
   - `BgObject*` is valid
   - `ModelResourceHandle != null`
   - `ModelResourceHandle.LoadState >= 7`
   - target path belongs to the same category and resource system
5. Whether `SetModel` needs additional lifecycle calls, or whether those calls must be avoided until the new handle is loaded.

## Current Product State

- No write button is provided.
- `BgObjectModelOverrideService.ApplyModel()` is paused and immediately returns failure status.
- `RestoreModel()` is paused.
- `Refresh()` remains read-only and only updates UI fields.

Next useful step is to find generated interop source or upstream FFXIVClientStructs source for `ResourceCategory` and any first-party call path that constructs or passes it to `BgObject.SetModel`.
