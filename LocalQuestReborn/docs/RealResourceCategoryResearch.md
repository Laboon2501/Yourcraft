# Real ResourceCategory Research

## Scope

This is read-only research for `BgObject.SetModel`. The plugin still does not expose any `SetModel` write button.

## ResourceCategory Definition

Source file:

`C:\Users\kiomo\Documents\New project\.research\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\System\Resource\ResourceManager.cs`

Definition:

```csharp
public enum ResourceCategory {
    Common = 0,
    BgCommon = 1,
    Bg = 2,
    Cut = 3,
    Chara = 4,
    Shader = 5,
    Ui = 6,
    Sound = 7,
    Vfx = 8,
    UiScript = 9,
    Exd = 10,
    GameScript = 11,
    Music = 12,
    SqpackTest = 18,
    Debug = 19,
    MaxCount = 20
}
```

Important related type:

`C:\Users\kiomo\Documents\New project\.research\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\System\Resource\Handle\ResourceHandle.cs`

```csharp
[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct ResourceHandleType {
    [FieldOffset(0x0), CExporterIgnore] public uint Value;
    [FieldOffset(0x0)] public HandleCategory Category;
    [FieldOffset(0x2)] private byte Unknown0A;
    [FieldOffset(0x3)] public byte Expansion;

    public enum HandleCategory : ushort {
        Common = 0,
        BgCommon = 1,
        Bg = 2,
        Cut = 3,
        Chara = 4,
        Shader = 5,
        Ui = 6,
        Sound = 7,
        Vfx = 8,
        UiScript = 9,
        Exd = 10,
        GameScript = 11,
        Music = 12,
        SqpackTest = 18,
        Debug = 19,
        MaxCount = 20
    }
}
```

`ResourceHandle.Type.Category` is the most useful runtime readback source. For a selected BgPart, LocalQuestReborn now reads `ModelResourceHandle->Type.Category` directly and shows it in the UI.

## BgPart Category

For paths like:

```text
bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl
```

the expected category is:

```csharp
ResourceCategory.Bg = 2
```

This is no longer only a path guess. It should be verified per selected instance by reading:

```csharp
bgObject->ModelResourceHandle->Type.Category
```

If the selected BgPart's handle reports `Bg (2)`, that is the real category for that loaded resource handle.

`bgcommon/` resources may report `BgCommon (1)`. The UI should prefer the handle readback over any path-prefix inference.

## ResourceManager Path

Source file:

`C:\Users\kiomo\Documents\New project\.research\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\System\Resource\ResourceManager.cs`

Relevant methods:

```csharp
public ResourceHandle* FindResourceHandle(ResourceCategory* category, uint* type, uint* hash)
    => ResourceGraph->FindResourceHandle(category, type, hash);

public partial ResourceHandle* GetResourceSync(
    ResourceCategory* category,
    uint* type,
    uint* hash,
    CStringPointer path,
    void* unknown,
    void* unkDebugPtr,
    uint unkDebugInt);

public partial ResourceHandle* GetResourceAsync(
    ResourceCategory* category,
    uint* type,
    uint* hash,
    CStringPointer path,
    void* unknown,
    bool isUnknown,
    void* unkDebugPtr,
    uint unkDebugInt);
```

This shows that `ResourceCategory*` is also the category key for resource manager lookup. `BgObject.SetModel` taking `ResourceCategory*` is consistent with this resource loading path.

## BgObject.SetModel Signature

Source file:

`C:\Users\kiomo\Documents\New project\.research\FFXIVClientStructs\FFXIVClientStructs\FFXIV\Client\Graphics\Scene\BgObject.cs`

Definition:

```csharp
[MemberFunction("48 89 5C 24 ?? 57 48 83 EC 30 48 8B C2 C7 44 24")]
[GenerateStringOverloads]
public partial bool SetModel(ResourceCategory* modelResourceCategory, CStringPointer modelResourcePath);
```

Summary from source:

- Loads the model resource at the given path.
- `modelResourceCategory`: the resource category that contains the model resource.
- `modelResourcePath`: the path of the model resource.
- Returns success/failure.

## SetModel Call Source Search

Searched:

- FFXIVClientStructs upstream source
- LocalQuestReborn
- Meddle
- Ktisis
- Anamnesis

Result:

- No third-party plugin call site for `BgObject.SetModel(...)` was found.
- Meddle reads `BgPartsLayoutInstance.GraphicsObject -> ModelResourceHandle`; it does not replace BgPart models.
- Ktisis did not show a `BgObject.SetModel` use in the searched source.
- Anamnesis has unrelated equipment model setters, not `BgObject.SetModel`.
- FFXIVClientStructs exposes the member function signature, but does not include an internal game call graph/xref.

So the safe call conditions are not proven by an existing plugin example.

## CStringPointer / String Overload

FFXIVClientStructs README explains `[GenerateStringOverloads]`:

- Native methods use C strings, meaning UTF-8, null-terminated `byte*`.
- The generated `string` overload converts to UTF-8 bytes and appends a null terminator.
- The generated `ReadOnlySpan<byte>` overload exists for UTF-8 byte literals.

Practical implication:

```csharp
bgObject->SetModel(&category, "bg/ffxiv/.../file.mdl");
```

should compile through the generated string overload, but the native method still receives a `CStringPointer`.

For manual low-level use, the path must be UTF-8 and null-terminated. Do not use `fixed(char*)`, because C# `char` is UTF-16.

## Current UI / Runtime Readback

LocalQuestReborn now displays for the selected instance:

- `ModelResourceHandle` address
- vtable pointer
- `ResourceHandleType.Value`
- `ResourceHandleType.Category`
- `ResourceHandleType.Expansion`
- `FileType`
- decoded fourcc-like `FileType`
- `LoadState`
- `Id`
- SetModel signature
- category confidence

The confidence is high only when it comes from `ModelResourceHandle->Type.Category`.

## Safe Call Conditions Found

Confirmed:

- The real enum values are known.
- BgPart resources should be `Bg (2)` or sometimes `BgCommon (1)`, depending on the resource handle's actual category.
- The currently loaded `ModelResourceHandle->Type.Category` is the authoritative per-instance source.
- The generated string overload creates a UTF-8 null-terminated C string.

Not confirmed:

- Whether `SetModel` is safe to call on an already-live layout BgObject after arbitrary transform edits.
- Whether the method expects additional state beyond `ResourceCategory` and path.
- Whether replacing with a different `.mdl` requires material/stain/animation/cache cleanup.
- Whether the update/render chain should run immediately, wait for async load, or avoid some calls.

## Recommendation

Keep `SetModel` paused until a single-instance retry is designed around:

1. Read `category = (ResourceCategory)bgObject->ModelResourceHandle->Type.Category`.
2. Require `ModelResourceHandle != null`.
3. Require `LoadState >= 7`.
4. Require target path category compatibility with the read category.
5. Use generated string or UTF-8 null-terminated bytes, never UTF-16.
6. Do not call in create/delete/restore/batch flows.

Even with the real category known, this does not yet prove `SetModel` is safe.
