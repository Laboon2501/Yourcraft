# Brio 真实 Prop 加载链取证

## 当前实验结论

Yourcraft 已验证：

```text
SpawnFlags.Prop / SpawnFlags.IsProp / ActorSpawnService.SpawnNewProp
  -> objectType = Dalamud.Game.ClientState.Objects.SubKinds.Npc
  -> isCharacterClone = true
  -> isBrioProp = false
```

因此这条路线不能作为 Yourcraft 的“场景物体生成”成功路径。它本质仍然依赖 `ActorSpawnService.CreateCharacter`，只会生成角色 clone。

## 1. Brio UI 里 “Load Prop” 按钮调用哪个方法

在当前取证到的 Brio 源码中，没有发现一个公开的、独立的 “Load Prop by mdl path” 按钮。

相关 UI/入口分成两类：

1. `ActorContainerWidget`
   - 按钮 `Spawn`
     - `ActorContainerCapability.CreateCharacter(false, true, forceSpawnActorWithoutCompanion: true)`
   - 按钮 `Spawn with Companion slot`
     - `ActorContainerCapability.CreateCharacter(true, true)`
   - 当前取证版本里没有单独的 `Load Prop` 按钮。

2. Scene import 路线
   - `SceneService.LoadScene(SceneFile sceneFile, bool destroyAll = false)`
   - 当 `actorFile.IsProp == true` 时进入 Prop 分支：

```text
SceneService.LoadScene
  -> ActorContainerCapability.CreateProp(false)
    -> ActorSpawnService.SpawnNewProp(out ICharacter?)
      -> ActorSpawnService.CreateCharacter(... SpawnFlags.IsProp | SpawnFlags.CopyPosition ...)
  -> private SceneService.LoadProp(actorId, actorFile)
```

也就是说 Brio 的 Prop 加载链是“场景文件恢复 Prop actor”，不是“从 mdl path 直接创建地图物件”。

## 2. SceneService.LoadProp 真实签名

`SceneService.LoadProp` 是私有方法：

```csharp
private async Task LoadProp(EntityId actorId, ActorFile actorFile)
```

调用位置在：

```csharp
if(actorFile.IsProp)
{
    var (actorId, actor) = actorCapability.CreateProp(false);

    _framework.RunUntilSatisfied(
        () => actor.Native()->IsReadyToDraw(),
        (__) =>
        {
            _framework.RunOnTick(() =>
            {
                _ = LoadProp(actorId, actorFile);
            });
        },
        100,
        dontStartFor: 2
    );
}
```

`LoadProp` 内部做三件事：

```text
attachedActor.GetCapability<ModelPosingCapability>()
attachedActor.GetCapability<ActorAppearanceCapability>()
modelCapability.Transform += actorFile.PropData.PropTransformDifference
appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Weapon)
appearanceCapability.AttachWeapon()
```

## 3. PropData 需要哪些字段

`PropData` 定义在 `Brio.Files.ActorFile`：

```csharp
public class PropData
{
    public Transform PropTransformDifference { get; set; }
    public Transform PropTransformAbsolute { get; set; }
}
```

字段含义：

- `PropTransformDifference`
  - 相对原始模型 transform 的差值。
  - `SceneService.LoadProp` 实际使用这个字段。
- `PropTransformAbsolute`
  - 保存导出时的绝对 transform。
  - 当前 `LoadProp` 没直接使用它。

## 4. PropData 是否接受 bg/.../xxx.mdl 路径

不接受。

`PropData` 只有 Transform，不包含：

- `.mdl path`
- `ModelId`
- `WeaponModelId`
- `ResourceHandle`
- `PropsFileEntry`

所以 `bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl` 不能直接写入 `PropData`。

## 5. 如果不接受 mdl path，它接受什么

Brio 的 Prop 显示不是靠 `PropData` 选模型，而是靠 `ActorFile.AnamnesisCharaFile` 里的 weapon appearance。

关键链路：

```text
ActorFile.IsProp = true
ActorFile.PropData = Transform 数据
ActorFile.AnamnesisCharaFile = 外观/武器数据
SceneService.LoadProp
  -> ActorAppearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Weapon)
  -> ActorAppearanceCapability.AttachWeapon()
```

模型来源在 `ModelDatabase`：

```text
ModelDatabase
  -> 读取 GameDataProvider.Instance.Items
  -> 读取 Brio 内置 Data.Props.json
  -> PropsFileEntry.Id
      3 段数字 -> WeaponModelId { Id, Type, Variant }
      2 段数字 -> EquipmentModelId { Id, Variant }
  -> ModelInfo(... ActorEquipSlot.Prop ...)
```

因此它接受的是：

- `WeaponModelId`
- `EquipmentModelId`
- 或由 `PropsFileEntry.Id` 转换出的模型 ID
- 最终进入 `ActorAppearance.Weapons.MainHand` / Prop weapon appearance

不是 `bg/.../xxx.mdl`。

## 6. 如何通过反射调用真实 LoadProp

理论路线：

```text
Brio.Brio.TryGetService<SceneService>(out sceneService)
构造 Brio.Files.SceneFile
构造 Brio.Files.ActorFile
  actorFile.IsProp = true
  actorFile.PropData = new PropData { ... }
  actorFile.AnamnesisCharaFile = 包含 WeaponModelId 的 chara appearance
  actorFile.PoseFile = 最小 PoseFile
调用 sceneService.LoadScene(sceneFile, destroyAll: false)
```

但这仍会经过：

```text
ActorContainerCapability.CreateProp
  -> ActorSpawnService.SpawnNewProp
    -> ActorSpawnService.CreateCharacter
```

当前实机结果说明这条对象创建层仍会返回 `Npc/Character clone`，并且 `ActorEntity.IsProp=false`。所以在 Yourcraft 里不能把它算作真实 Prop 成功。

若后续继续反射，必须先解决两个前置条件：

1. Brio 的 EntityManager 必须能把生成对象识别为 `ActorEntity.IsProp=true`。
2. 必须构造有效的 `ActorFile.AnamnesisCharaFile`，里面包含 Prop 的 `WeaponModelId`。

## 7. 生成后如何拿 runtime object / handle

SceneService 路线不会直接返回 runtime object。

可追踪点：

- `ActorContainerCapability.CreateProp(false)` 返回 `(EntityId, ICharacter)`，但这是内部调用。
- `ActorSpawnService.SpawnNewProp(out ICharacter?)` 返回 `ICharacter`。
- `EntityManager.GetEntity(new EntityId(character))` 可以尝试拿到 `ActorEntity`。
- `ActorEntity.IsProp` 是 Brio 层判断是否为 Prop 的关键 readback。

Yourcraft 当前必须记录：

- `runtimeId`
- `characterObjectType`
- `objectIndex`
- `address`
- `drawObjectAddress`
- `ActorEntity.IsProp`
- `是否 Character clone`

如果 `ActorEntity.IsProp=false`，就是失败。

## 8. 是否只在 GPose / Brio Scene 内可用

Brio 的 actor container 能力明显偏向 GPose/Brio scene：

```csharp
public bool CanControlCharacters => _gPoseService.IsGPosing;
```

UI widget 用 `CanControlCharacters` 禁用/启用 Spawn 控制。SceneService 也围绕 Brio SceneFile / ActorFile 恢复状态工作。

所以 Brio Prop 管线至少依赖 Brio scene/entity/capability 系统；它不是普通游戏状态下的原生地图物件创建 API。

## Yourcraft 当前处理

已禁用：

- PropData Prop 生成按钮
- Raw mdl path 生成按钮
- 所有基于 `CreateCharacter` / `SpawnFlags.Prop` / `SpawnNewProp` 的 Prop 生成路径

UI 明确显示：

```text
CreateCharacter 无法生成场景物体，只会生成角色 clone。
```

并提供清理按钮：

```text
清理 Character clone Prop 实例
```

下一步应研究：

1. Brio `ActorAppearanceCapability.SetProp(WeaponModelId)` 是否能对已存在 Brio actor 稳定显示道具模型。
2. `Data.Props.json` 的 `PropsFileEntry.Id` 如何转换成 `WeaponModelId` 并通过反射构造。
3. 若目标是任意 `bg/...mdl`，优先走 Penumbra temporary replacement，而不是 native 写 DrawObject。
