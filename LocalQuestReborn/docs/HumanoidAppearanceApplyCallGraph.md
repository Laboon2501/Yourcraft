# Humanoid GameNpc 外观应用接口取证

## 结论

LocalQuestReborn 当前已经能把 ENpc/BNpc 解析为两类外观：

- Monster / ModelChara：核心数据是 `ModelCharaId`。
- Humanoid / ENpc：核心数据是 `Customize + Equipment`，`ModelCharaId=0` 不是失败。

本轮禁止 native 直接写装备、customize 或 ModelCharaId。最小可用应用路径选择 Brio 的 `ActorAppearanceCapability.SetAppearance(...)`，因为 Brio 已经提供 `ActorAppearance.FromENpc(ENpcBase npc)`，可以直接把 ENpcBase 的人形字段转换为 Brio 内部 `ActorAppearance`。

## UI Button

```text
Actor实例 / 应用 NPC 外观
  -> LocalQuestReborn.Services.RealNpcSpawnService.ApplyNpcAppearance(runtimeId)
     LocalQuestReborn/Services/RealNpcSpawnService.cs:293
    -> LocalQuestReborn.Services.AppearanceApplyService.ApplyNpcAppearance(npc, actor)
       LocalQuestReborn/Services/AppearanceApplyService.cs:32
      -> ApplyGameNpc(...)
         LocalQuestReborn/Services/AppearanceApplyService.cs:96
        -> GameNpcAppearanceResolver.Resolve(appearance)
           LocalQuestReborn/Services/GameNpcAppearanceResolver.cs:20
        -> GameNpcAppearanceApplyService.TryApplyModelChara(actor, resolution, out reason)
           LocalQuestReborn/Services/GameNpcAppearanceApplyService.cs
          -> BrioHumanoidAppearanceApplyService.TryApplyHumanoid(actor, resolution, out reason)
             LocalQuestReborn/Services/BrioHumanoidAppearanceApplyService.cs
            -> Brio EntityManager.SetSelectedEntity(characterObject)
            -> Brio EntityManager.TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(out capability)
            -> Brio.Game.Actor.Appearance.ActorAppearance.FromENpc(ENpcBase npc)
            -> ActorAppearanceCapability.SetAppearance(ActorAppearance appearance, AppearanceImportOptions.All)
```

## Glamourer State IPC

AQuestReborn 不直接写 raw IPC 名称，而是引用 `Glamourer.Api.IpcSubscribers` wrapper。

取证位置：

- `.research/AQuestRebornFull/AQuestReborn/PenumbraAndGlamourerHelpers/PenumbraAndGlamourerIpcWrapper.cs:67`
  - `new SetCollectionForObject(dalamudPluginInterface)`
  - `new ApplyDesign(dalamudPluginInterface)`
  - `new SetItem(dalamudPluginInterface)`
  - `new GetStateBase64(dalamudPluginInterface)`
  - `new ApplyState(dalamudPluginInterface)`
  - `new RevertState(dalamudPluginInterface)`

- `.research/AQuestRebornFull/AQuestReborn/PenumbraAndGlamourerHelpers/PenumbraAndGlamourerHelperFunctions.cs:44`
  - `SetItem.Invoke(objectIndex, FullEquipTypeToApiEquipSlot(equipItem), itemId, new List<byte>())`

- `.research/AQuestRebornFull/AQuestReborn/PenumbraAndGlamourerHelpers/PenumbraAndGlamourerHelperFunctions.cs:175`
  - `ApplyState.Invoke(characterCustomization.ToBase64(), character.ObjectIndex, 0, ApplyFlag.Customization)`

- `.research/AQuestRebornFull/MCDF-Loader/MCDFLoader/Ipc/IpcCallerGlamourer.cs`
  - `ApplyState.Invoke(customization, chara.ObjectIndex, LockCode/0, ApplyFlag.Customization | ApplyFlag.Equipment)`

真实 wrapper 调用签名：

```csharp
Glamourer.Api.IpcSubscribers.ApplyState.Invoke(
    string stateBase64,
    int objectIndex,
    uint key,
    Glamourer.Api.Enums.ApplyFlag flags)
```

用途：

- `stateBase64`：Glamourer state，AQuestReborn 由 `CharacterCustomization.ToBase64()` 生成。
- `objectIndex`：目标 actor 的 object index。
- `key`：锁定/事务 key，AQuestReborn 普通调用使用 `0`。
- `flags`：`ApplyFlag.Customization`、`ApplyFlag.Equipment` 或二者组合。

LocalQuestReborn 本轮新增 `GlamourerStateApplyService` 只做 wrapper 探测和签名显示。原因是现有 `Customize + Equipment` 还不是 Glamourer state base64；强行拼 base64 会变成猜 schema。

## Brio ActorAppearanceCapability

取证位置：

- `.research/AQuestRebornFull/Brio/Brio/Game/Actor/Appearance/ActorAppearance.cs:130`
  - `ActorAppearance.FromENpc(ENpcBase npc)`
  - 从 `Race/Gender/Tribe/Face/HairStyle/SkinColor/EyeColor/...` 映射 customize。
  - 从 `ModelMainHand/ModelOffHand/ModelHead/ModelBody/...` 映射 equipment。

- `.research/AQuestRebornFull/Brio/Brio/Capabilities/Actor/ActorAppearanceCapability.cs:92`
  - `public async Task SetAppearance(ActorAppearance appearance, AppearanceImportOptions options)`

- `.research/AQuestRebornFull/Brio/Brio/UI/Windows/Specialized/ActorAppearanceWindow.cs:139`
  - `_ = capability.SetAppearance(currentAppearance, AppearanceImportOptions.All)`

- `.research/AQuestRebornFull/Brio/Brio/UI/Controls/Editors/AppearanceEditorCommon.cs:61`
  - `_ = capability.SetAppearance(_globalNpcSelector.Selected.Appearance, options)`

真实 Brio 方法：

```csharp
ActorAppearance appearance = ActorAppearance.FromENpc(ENpcBase npc);
await ActorAppearanceCapability.SetAppearance(
    appearance,
    AppearanceImportOptions.All);
```

LocalQuestReborn v1.7 通过反射实现这条路径：

- 读取 Brio assembly。
- 获取 `EntityManager`。
- `SetSelectedEntity(characterObject)`。
- `TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(out capability)`。
- 读取 `ENpcBase RowId`。
- 调用 `ActorAppearance.FromENpc(ENpcBase)`。
- 调用 `SetAppearance(..., AppearanceImportOptions.All)`。

## AQuestReborn 如何应用外观到 generated actor

生成 actor 之后，AQuestReborn 主要有三条外观路径：

```text
Glamourer Design
  -> PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex)
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1514
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1640
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:2764

MCDF / appearance file
  -> AQuestReborn.LoadAppearance(appearanceData, swapType, character)
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1764
    -> BrioAccessUtils.EntityManager.TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(out appearance)
       .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1833
    -> appearance.ImportAppearance(appearanceDataItem, AppearanceImportOptions.All)
       .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1836
    -> fallback AppearanceAccessUtils.AppearanceManager?.LoadAppearance(...)
       .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1849

Penumbra collection
  -> SetCollectionForObject.Invoke(character.ObjectIndex, collectionGuid, true, true)
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1491
  -> RedrawObject.Invoke(character.ObjectIndex, RedrawType.Redraw)
     .research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1504
```

MCDF-Loader 对外 IPC：

```csharp
McdfStandalone.LoadMcdf(string path, IGameObject target, int appearanceSwap) -> bool
McdfStandalone.LoadMcdfAsync(string path, IGameObject target) -> Task<bool>
McdfStandalone.GetHandledAddresses() -> List<nint>
```

取证位置：

- `.research/AQuestRebornFull/MCDF-Loader/MCDFLoader/Interop/Ipc/IpcProvider.cs:88`
- `.research/AQuestRebornFull/MCDF-Loader/MCDFLoader/Interop/Ipc/IpcProvider.cs:94`

## LocalQuestReborn 字段映射

LocalQuestReborn 的 `GameNpcResolvedAppearance.Customize` 对应 Brio `ActorAppearance.FromENpc` 中的 ENpcBase 字段：

- `race` -> `Race`
- `gender` -> `Gender`
- `tribe` -> `Tribe`
- `bodyType` -> `BodyType`
- `height` -> `Height`
- `face` -> `Face`
- `hairStyle` -> `HairStyle`
- `hairHighlight` -> `HairHighlight`
- `skinColor` -> `SkinColor`
- `eyeHeterochromia` -> `EyeHeterochromia`
- `hairColor` -> `HairColor`
- `hairHighlightColor` -> `HairHighlightColor`
- `facialFeature` -> `FacialFeature`
- `facialFeatureColor` -> `FacialFeatureColor`
- `eyebrows` -> `Eyebrows`
- `eyeColor` -> `EyeColor`
- `eyeShape` -> `EyeShape`
- `nose` -> `Nose`
- `jaw` -> `Jaw`
- `mouth` -> `Mouth`
- `lipColor` -> `LipColor`
- `bustOrTone1` -> `BustOrTone1`
- `extraFeature1` -> `ExtraFeature1`
- `extraFeature2OrBust` -> `ExtraFeature2OrBust`
- `facePaint` -> `FacePaint`
- `facePaintColor` -> `FacePaintColor`

`GameNpcResolvedAppearance.Equipment` 对应：

- `mainHand` -> `ModelMainHand`
- `offHand` -> `ModelOffHand`
- `head` -> `ModelHead`
- `body` -> `ModelBody`
- `hands` -> `ModelHands`
- `legs` -> `ModelLegs`
- `feet` -> `ModelFeet`
- `ears` -> `ModelEars`
- `neck` -> `ModelNeck`
- `wrists` -> `ModelWrists`
- `leftRing` -> `ModelLeftRing`
- `rightRing` -> `ModelRightRing`

本轮实现没有手工重建这些字段，而是把原始 `ENpcBase` 行交给 Brio 的 `ActorAppearance.FromENpc`，让 Brio 使用它自己的映射。

## 已实现与缺口

已实现：

- `GlamourerStateApplyService`：探测 `Glamourer.Api.IpcSubscribers.ApplyState` wrapper 和真实 `Invoke` 签名。
- `BrioHumanoidAppearanceApplyService`：通过 Brio `ActorAppearanceCapability.SetAppearance` 应用 ENpcBase 人形外观。
- Actor 实例页显示 Humanoid 是否解析、三条可用路径、当前路径、最后结果和最后异常。

尚未实现：

- 从 LocalQuestReborn 自己的 `Customize + Equipment` 构造 Glamourer state base64。
- 通过 Glamourer `SetItem` 逐件设置装备。
- 通过 MCDF 生成临时 MCDF 文件再应用。
- Monster / ModelChara 的非 native 安全应用路径。
