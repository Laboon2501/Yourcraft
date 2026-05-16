# EventNpc Native Field Probe

## 目标

验证 Brio 生成的本地 actor 是否可以进入 FFXIV 原生 EventNpc 体系，或至少找出和真实 EventNpc 的关键字段差异。

## 已实现取证

新增 `NativeNpcProbeService`，可保存两类快照：

- 当前 `TargetManager.Target` 作为真实 EventNpc 快照。
- 当前选中 RuntimeActor 作为 Brio actor 快照。

每个字段记录值和来源：

- `Dalamud public property`
- `public field`
- `反射读取`
- `未确认`

当前读取字段包括：

- `Name`
- `ObjectIndex`
- `Address`
- `ObjectKind`
- `SubKind`
- `IsTargetable`
- `DataId`
- `EntityId`
- `GameObjectId`
- `NameId`
- `NamePlateIconId`
- `EventHandler`
- `EventId`
- `NpcId`
- `BaseId`
- `HitboxRadius`
- `TargetObjectId`
- `OwnerId`
- `Position`
- `DrawObject`
- `RenderFlags`

## 源码线索

Brio `ActorSpawnService.CreateCharacter` 走 `ClientObjectManager.CreateBattleCharacter`，本质创建的是本地 battle character / player clone。

AQuestReborn 曾做过：

- `characterStruct->NamePlateIconId = 71201`
- `NamePlateGui.OnNamePlateUpdate` 中按 `GameObject.Address` 覆盖 `handler.NameParts.Text`

AnamCore 中 `ActorBasicMemory` 标注：

- `DataId` offset `0x080`
- `ObjectKind` offset `0x08c`
- `ActorTypes.EventNpc = 0x03`

这些线索说明 ObjectKind/DataId/NamePlateIconId 很可能相关，但本轮不直接盲写 native offset。

## 实验策略

`ExperimentalEventNpcService` 只提供单字段按钮：

- 写 ObjectKind
- 写 SubKind
- 写 IsTargetable
- 写 DataId
- 写 Hitbox
- 刷新 nameplate

所有写入必须 `UnsafeMode=true`，且优先寻找公开/反射 setter。没有 setter 时不进行 native 盲写。

## 当前判断

Brio actor 当前表现为：

- `ObjectKind=Pc`
- `SubKind=4`
- `DataId=0`
- `IsTargetable=False`
- 名字读回为玩家名

真实 EventNpc 和 Brio actor 的差异需要通过 UI 中的 `EventNpc Native Probe / 原生 EventNpc 取证` 面板实机保存并对比。

## 最小安全路径

1. 先选中真实 EventNpc 保存快照。
2. 生成 Brio actor 后保存 actor 快照。
3. 对比字段差异。
4. 仅对确认有 setter 的字段做单字段实验。
5. 原生 hover/右键交互暂不强行写 EventHandler。
