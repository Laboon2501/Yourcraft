# Single Instance SetModel Experiment

> Status: paused. Direct `BgObject.SetModel` calls caused native crashes, so this experiment is no longer callable from the UI. See `docs/BgObjectSetModelCrashAnalysis.md`.

## Scope

This experiment is intentionally limited to one selected `LocalLayoutObjectInstance`.

It is not used by:

- instance creation
- batch copy
- delete
- restore all
- resourcePath string memory writes
- Penumbra global replacement

## Call Signature

Current FFXIVClientStructs exposes:

```csharp
BgObject.SetModel(ResourceCategory* modelResourceCategory, string modelResourcePath)
```

The plugin currently uses:

```csharp
var category = default(ResourceCategory);
bgObject->SetModel(&category, modelPath);
```

This is treated as an experimental default category until a safer known category value is confirmed.

## Safety Preconditions

The UI disables the SetModel button unless:

- Unsafe/native writes are enabled.
- One `LocalLayoutObjectInstance` is selected.
- `graphicsObjectAddress` is present.
- `custom mdl path` is non-empty.
- `custom mdl path` ends with `.mdl`.
- The user checks the explicit SetModel crash-risk confirmation box.
- The selected instance is not restored, invalid, or duplicate.

## Update Chain

After `SetModel`, the experiment calls:

```csharp
NotifyTransformChanged();
IsTransformChanged = true;
UpdateMaterials();
UpdateRender();
UpdateTransforms(true);
ComputeSphereBounds();
```

Each call is for the selected instance only.

## Readback

The UI records:

- before model path
- target model path
- after model path
- modelResourceHandle address
- SetModel return value
- visibility readback
- Position / Rotation / Scale readback
- last exception
- manual confirmation result

## Current Status

This is not promoted to the normal copy pipeline.

Manual validation still needs to confirm:

- whether the selected instance changes to the target `.mdl`
- whether other same-resource BgParts remain unchanged
- whether transform still works after model replacement
- whether restoring the original model is stable
- whether the game remains stable after repeated tests
