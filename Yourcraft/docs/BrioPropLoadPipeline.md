# Brio Prop 加载管线取证

## Brio UI / Scene 中加载 Prop 的按钮调用链

Brio 当前源码中的 Prop 路线不是“直接加载一个 bgparts `.mdl`”，而是：

```text
SceneService.LoadScene(SceneFile)
  -> ActorContainerCapability.CreateProp(false)
    -> ActorSpawnService.SpawnNewProp(out ICharacter?)
      -> ActorSpawnService.CreateCharacter(... SpawnFlags.IsProp | SpawnFlags.CopyPosition, true)
      -> EntityManager 将对象识别为 ActorEntity
      -> ActorEntity.ActorType 根据 SpawnFlags.IsProp 返回 ActorType.Prop
  -> SceneService.LoadProp(actorId, ActorFile)
    -> ActorEntity.GetCapability<ModelPosingCapability>()
    -> ActorEntity.GetCapability<ActorAppearanceCapability>()
    -> modelCapability.Transform += actorFile.PropData.PropTransformDifference
    -> appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Weapon)
    -> appearanceCapability.AttachWeapon()
```

结论：Brio 的“Prop”仍然从 `ICharacter` 出发，但 Brio 内部把它包装成 `ActorType.Prop`，并通过主手/Prop weapon appearance 显示为道具。

## PropData 结构

`Brio.Files.ActorFile` 中：

```csharp
public class PropData
{
    public Transform PropTransformDifference { get; set; }
    public Transform PropTransformAbsolute { get; set; }
}
```

`PropData` 只保存 transform 差异与绝对 transform，不保存 `.mdl` 路径、ModelId 或 ResourceHandle。

## PropsFileEntry / ModelDatabase

`Brio.Resources.Extra.ModelDatabase` 读取内置 `Data.Props.json`：

```text
PropsFileEntry.Id
PropsFileEntry.Name
PropsFileEntry.Description
PropsFileEntry.Slot
```

`Id` 是逗号分隔的数字：

- 3 段时构造成 `WeaponModelId { Id, Type, Variant }`
- 2 段时构造成 `EquipmentModelId { Id, Variant }`

然后生成 `ModelInfo(..., ActorEquipSlot.Prop, ...)`。

这说明 Brio 的 Prop 选择器需要的是 `WeaponModelId` / `EquipmentModelId`，不是 `bg/.../xxx.mdl`。

## SceneService.LoadProp / AddProp 签名

当前取证到的公开入口：

```csharp
public unsafe void LoadScene(SceneFile sceneFile, bool destroyAll = false)
```

内部私有方法：

```csharp
private async Task LoadProp(EntityId actorId, ActorFile actorFile)
```

本轮没有发现 public `AddProp` / `CreateProp` / `LoadProp(modelPath)` 这种直接方法。Brio 的 UI/Scene 通过 `ActorContainerCapability.CreateProp(bool)` 生成 Prop actor，再通过 `ActorAppearanceCapability.SetAppearance(... Weapon)` / `SetProp(WeaponModelId)` 应用具体道具模型。

## 最终对象类型

底层游戏对象仍是 `ICharacter` / character object table 对象。Brio 层通过 `ActorEntity.IsProp == true` 区分 Prop：

```text
ActorEntity.IsProp -> ActorType == ActorType.Prop
ActorType -> SpawnFlag.HasFlag(SpawnFlags.IsProp)
```

因此验收时不能只看 Dalamud managed type；需要同时看：

- `characterObjectType`
- `ActorEntity.IsProp`
- `SpawnMethod`
- `PropData/Appearance` 是否走 Brio Prop capability

如果只调用 `CreateCharacter` 并得到玩家 clone，而 Brio `ActorEntity.IsProp=false`，就不是 Prop。

## 是否支持 mdl path

当前 Brio PropData 不支持 `.mdl path` 字段。示例：

`bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl`

不能直接写进 `PropData` 或 `SceneService.LoadProp`。要支持该路径，需要后续走以下路线之一：

1. Penumbra temporary resource replacement：把某个已知 Prop/weapon model 的 mdl 替换成目标 bgparts mdl。
2. Brio `ActorAppearanceCapability.SetProp(WeaponModelId)`：先从 Brio `Props.json`/ModelDatabase 选择一个 `WeaponModelId`。
3. 研究 FFXIVClientStructs `DrawObject` / `ResourceHandle` / model loader，但这属于高风险 native 路线，必须 UnsafeMode 且单字段实验。

## 是否只能在 GPose / Brio scene 中使用

Brio 的 entity/capability 和 actor container 主要面向 Brio 场景与 GPose 管理。`ActorContainerCapability.CanControlCharacters => _gPoseService.IsGPosing`，但 `CreateProp` 自身没有在方法内直接拒绝非 GPose。实际对象生命周期仍受 Brio/GPose/切图清理影响。

## Yourcraft v3.3 实现策略

- 新增 `BrioPropBridgeService`。
- 推荐路线优先反射调用 `ActorContainerCapability.CreateProp(false)`。
- fallback 只调用 Brio 真实 Prop 入口 `ActorSpawnService.SpawnNewProp(out ICharacter)`，不再直接调用 `CreateCharacter`。
- 生成后通过 Brio `EntityManager` 尝试读取 `ActorEntity.IsProp`。
- 如果返回对象仍然只是 Character clone，UI 明确显示：

  `这不是 Prop，只是 Character clone。`

- Raw mdl path 模式保留为实验入口，但在没有安全 API 前不会假装支持。
