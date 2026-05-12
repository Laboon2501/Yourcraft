# Actor Nameplate / Targetability Plan

## 取证结论

### Brio actor 默认行为

- Brio `ActorSpawnService.CreateCharacter` 通过 `ClientObjectManager.CreateBattleCharacter` 创建本地 actor，然后复制本地玩家外观。
- Brio 新版 6 参数签名支持 `customName`，最终在 `CreateEmptyCharacter` 中调用 `GameObject.SetName(customName)`。
- 当前 LocalQuestReborn 实机走到的是 Brio 3 参数 `CreateCharacter(out ICharacter, SpawnFlags, bool)`，这个签名没有 `customName`，因此默认名字通常仍是 Brio 内部生成名或玩家 clone 相关名字。
- Brio actor 是本地创建的 client object，不等同于服务器下发的 ENpc/BNpc，因此普通 NPC 的 nameplate、hover、交互光标、事件处理不一定自动存在。

### AQuestReborn 的做法

- AQuestReborn 挂了 `NamePlateGui.OnNamePlateUpdate`，按 `handler.GameObject.Address` 匹配自己保存的生成 NPC，再把 `handler.NameParts.Text` 改成自定义名字。
- AQuestReborn 还在 framework update 中对 native `Character.NamePlateIconId` 写入 `71201`，尝试强制显示友好 NPC 图标。
- AQuestReborn 的交互/对话并不依赖服务器原生 EventNpc 交互，而是自己维护 generated actor 与自定义 NPC 的映射。

### 可能控制字段

- 显示名字：
  - Brio 6 参数 `customName`
  - `GameObject.SetName`
  - Dalamud `NamePlateGui.OnNamePlateUpdate`
  - native `NamePlateIconId`
- 可被选中：
  - `ObjectIndex`
  - `TargetManager.Target`
  - `GameObjectId`
  - `EntityId`
  - `ObjectKind`
  - `SubKind`
  - 可能存在的 `IsTargetable` / targetable flag
- 鼠标 hover / 交互光标：
  - 通常依赖 ObjectKind、targetable flag、event handler、客户端 object hit test。
  - 本轮不强行实现完整 hover，只验证 TargetManager 可识别和插件可设为当前目标。

### 安全边界

- 安全读：
  - `ICharacter` / `IGameObject` 公开属性
  - `ObjectIndex`、`Address`、`Name`、`ObjectKind`、`SubKind`、`DataId`、`EntityId`
- 相对可控写：
  - Brio 6 参数 `customName`
  - 公开属性 setter
  - `TargetManager.Target` setter 如 Dalamud 当前版本提供
- 危险写：
  - native `GameObject.Name`
  - native `NamePlateIconId`
  - native `ObjectKind` / `SubKind` / targetable flag
  - EventHandler / DataId / GameObjectId

## 最小实现方案

1. `RuntimeActorInstance` 记录 nameplate/targetability readback。
2. `ActorNameplateService`：
   - 尝试读取公开 `Name`。
   - 尝试公开 setter。
   - UnsafeMode=true 时才尝试写 native `GameObject.Name`。
3. `ActorTargetabilityService`：
   - 读取 ObjectKind/SubKind/DataId/EntityId/IsTargetable。
   - 通过 `TargetManager.Target.ObjectIndex` 匹配 RuntimeActor。
   - 提供“设为当前目标”按钮，优先用 `TargetManager.Target` setter。
4. 暂不盲写 ObjectKind/SubKind/EventHandler，避免 AccessViolation 或污染游戏对象状态。
