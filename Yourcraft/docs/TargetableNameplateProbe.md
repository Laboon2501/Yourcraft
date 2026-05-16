# Targetable / NamePlate Probe

## 源码取证摘要

### Brio actor 为什么不像普通 NPC

Brio `ActorSpawnService.CreateCharacter` 使用 `ClientObjectManager.CreateBattleCharacter` 创建本地 BattleCharacter，再从本地玩家复制 CharacterSetup。当前 Yourcraft 在 Brio 3.0 环境实际命中 3 参数签名：

`CreateCharacter(out ICharacter outCharacter, SpawnFlags flags, bool disableSpawnCompanion)`

该签名没有 `customName`，因此无法在创建阶段传入 NPC 名称。Brio 6 参数签名才会传 `customName`，并在 `CreateEmptyCharacter` 中调用 `GameObject.SetName(customName)`。

当前实测 Brio actor 字段：

- `ObjectKind = Pc`
- `SubKind = 4`
- `DataId = 0`
- `IsTargetable = False`
- `Name` 读回为玩家名

这说明它更像本地玩家 clone，而不是服务器下发的 ENpc/BNpc/EventNpc。

### AQuestReborn 如何处理 nameplate

AQuestReborn 注册 `NamePlateGui.OnNamePlateUpdate`，通过 `handler.GameObject.Address` 匹配自己保存的 generated NPC，然后改：

`handler.NameParts.Text = npcName`

它还在 framework update 中对 generated character 写：

`characterStruct->NamePlateIconId = 71201`

这更像 nameplate 显示层覆盖，而不是把 Brio actor 真正变成服务器 NPC。

### Target / 可选中相关字段

可能相关字段：

- `ObjectKind`
- `SubKind`
- `DataId`
- `EntityId`
- `GameObjectId`
- `IsTargetable`
- `HitboxRadius`
- `EventHandler`
- `NameId`
- `NamePlateIconId`

其中 `ObjectKind/SubKind/EventHandler/DataId/GameObjectId` 都可能影响 hover、鼠标选中、交互光标，但这些字段属于原生对象布局，未确认前不应直接写。

## 当前实现

新增 Target Probe 面板，支持保存并对比：

- 当前 `TargetManager.Target` 作为参考 NPC 快照
- 当前选中的 Brio RuntimeActor 作为生成 Actor 快照

快照字段包括：

- `Name`
- `ObjectIndex`
- `Address`
- `ObjectKind`
- `SubKind`
- `DataId`
- `EntityId`
- `IsTargetable`
- `Position`
- `HitboxRadius`
- `EventHandler`
- `NameId`
- `GameObjectId`
- `OwnerId`
- targetable/flags 相关可读字段

## 最小安全实现路径

1. 先用 Target Probe 对比真实 NPC 与 Brio actor 字段差异。
2. 不写未知字段，尤其是 `ObjectKind/SubKind/DataId/EventHandler/Targetable flag`。
3. `设为当前目标` 优先使用 Dalamud `ITargetManager.Target` setter 或可发现的公开方法。
4. NamePlate 更安全的方向是接入 `NamePlateGui.OnNamePlateUpdate`，按 actor address 覆盖显示文字，而不是强写 native name buffer。
5. 真正鼠标 hover/交互光标需要进一步确认 EventHandler、targetable flag 和 object kind 组合。
