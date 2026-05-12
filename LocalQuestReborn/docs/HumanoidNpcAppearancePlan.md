# Humanoid NPC Appearance Plan

## 源码取证

AQuestReborn 的 `PenumbraAndGlamourerIpcWrapper` 初始化了 Glamourer / Penumbra 的 wrapper：

- `GetStateBase64`
- `ApplyState`
- `SetItem`
- `ApplyDesign`
- `RedrawObject`

AQuestReborn 的 `PenumbraAndGlamourerHelperFunctions.SetCustomization` 使用：

```csharp
PenumbraAndGlamourerIpcWrapper.Instance.ApplyState.Invoke(
    characterCustomization.ToBase64(),
    character.ObjectIndex,
    0,
    ApplyFlag.Customization);
```

装备应用使用：

```csharp
PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(
    objectIndex,
    ApiEquipSlot,
    itemId,
    new List<byte>());
```

Redraw 使用：

```csharp
PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(
    objectIndex,
    RedrawType.Redraw);
```

Brio 的 `ActorAppearanceCapability` 走更完整的 Brio 内部能力。它从 `ActorAppearance.FromENpc(ENpcBase npc)` 映射 ENpc 人形外观，然后调用 `SetAppearance(ActorAppearance appearance, AppearanceImportOptions options)`。这条路线更适合后续直接引用 Brio assembly 后反射调用 `ActorAppearanceCapability.SetAppearance`。

## 人形 ENpc 字段映射

`ENpcResident` 只用于名字，不作为模型来源。

`ENpcBase` 提供人形外观：

- `Race` -> customize.race
- `Gender` -> customize.gender
- `Tribe` -> customize.tribe
- `BodyType` -> customize.bodyType
- `Height` -> customize.height
- `Face` -> customize.face
- `HairStyle` -> customize.hairStyle
- `HairHighlight` -> customize.hairHighlight
- `SkinColor` -> customize.skinColor
- `EyeHeterochromia` -> customize.eyeHeterochromia
- `HairColor` -> customize.hairColor
- `HairHighlightColor` -> customize.hairHighlightColor
- `FacialFeature` -> customize.facialFeature
- `FacialFeatureColor` -> customize.facialFeatureColor
- `Eyebrows` -> customize.eyebrows
- `EyeColor` -> customize.eyeColor
- `EyeShape` -> customize.eyeShape
- `Nose` -> customize.nose
- `Jaw` -> customize.jaw
- `Mouth` -> customize.mouth
- `LipColor` -> customize.lipColor
- `BustOrTone1` -> customize.bustOrTone1
- `ExtraFeature1` -> customize.extraFeature1
- `ExtraFeature2OrBust` -> customize.extraFeature2OrBust
- `FacePaint` -> customize.facePaint
- `FacePaintColor` -> customize.facePaintColor

装备字段：

- `ModelMainHand` -> equipment.mainHand
- `ModelOffHand` -> equipment.offHand
- `ModelHead` -> equipment.head
- `ModelBody` -> equipment.body
- `ModelHands` -> equipment.hands
- `ModelLegs` -> equipment.legs
- `ModelFeet` -> equipment.feet
- `ModelEars` -> equipment.ears
- `ModelNeck` -> equipment.neck
- `ModelWrists` -> equipment.wrists
- `ModelLeftRing` -> equipment.leftRing
- `ModelRightRing` -> equipment.rightRing

## 应用路线

路线 A：Glamourer State API

- 需要构建 `CharacterCustomization` 兼容 JSON/base64。
- 调用 `ApplyState(base64, objectIndex, key, ApplyFlag.Customization)` 设置 customize。
- 调用 `SetItem(objectIndex, slot, itemId, stainList)` 设置装备。
- 调用 Penumbra `RedrawObject(objectIndex, RedrawType.Redraw)`。

路线 B：Brio `ActorAppearanceCapability`

- 反射取得 Brio `EntityManager`。
- `SetSelectedEntity(characterObject)`。
- `TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>`。
- 构造 Brio `ActorAppearance`，按 `ActorAppearance.FromENpc` 的映射填入 customize/equipment。
- 调用 `SetAppearance(appearance, AppearanceImportOptions.All)`。

路线 C：当前已实现

- `GameNpcAppearanceResolver` 已能识别人形 ENpc，并输出 `GameNpcResolvedAppearance.Kind=Humanoid`。
- 已解析 Customize + Equipment，并在 UI 显示“已解析人形 NPC 外观：Customize + Equipment”。
- 本轮不 native 写装备/customize。

## 还缺

- Glamourer `ApplyState` / `SetItem` 的本机 IPC 签名探测与调用封装。
- Brio `ActorAppearanceCapability.SetAppearance` 的反射构造与调用。
- 安全 redraw 链路的统一封装。
