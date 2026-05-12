# NamePlate / Target 实验计划

## 当前结论

LocalQuestReborn 现在停止把 Brio 生成的 `Pc` clone 当作任务 NPC 使用。后续目标收窄为：生成可见 Actor，读取并对比它和真实 NPC 的原生字段，然后用单字段实验验证是否能进入游戏原生 NamePlate / Target 体系。

## 原生绿色名字可能由哪些字段决定

从本机 Dalamud / FFXIVClientStructs 文档可确认，`GameObject` / `Character` / `BattleChara` 继承链上存在这些 NamePlate 相关字段和方法：

- `ObjectKind`
- `SubKind`
- `BattleNpcSubKind`
- `DataId`
- `EntityId`
- `NameId`
- `NamePlateIconId`
- `NameplateOffset`
- `NameplateOffsetScaleMultiplier`
- `NameplateOffsetTarget`
- `EventHandler`
- `GetIsTargetable`
- `GetObjectKind`
- `GetNameId`
- `GetNamePlateColorType`
- `GetNamePlateColors`
- `GetNamePlateWorldPosition`

绿色 NPC 名字大概率不是单独的颜色字段，而是由对象类型、可目标性、名字来源、Event/Battle NPC 分类和 NamePlate 系统共同决定。优先怀疑链路是：

1. 对象必须能被 ObjectTable / TargetManager 视为可目标对象。
2. `ObjectKind` 需要是 `EventNpc` 或 `BattleNpc` 这类 NPC 类型，而不是 `Pc`。
3. `IsTargetable` / `GetIsTargetable` 必须返回 true。
4. `NameId` / `DataId` / `EventHandler` 可能决定名字来源和交互/悬停状态。
5. `GetNamePlateColorType` / `GetNamePlateColors` 的返回结果决定最终颜色类型。

## Pc clone 与 EventNpc 的差异

已知 Brio 3.0 通过 `ActorSpawnService.CreateCharacter(out ICharacter, SpawnFlags, bool)` 生成的对象表现为玩家 clone。之前取证显示这类 Actor 常见状态是：

- `ObjectKind = Pc`
- `SubKind = 4`
- `DataId = 0`
- `IsTargetable = false`
- 名字读回仍是玩家名

真实 EventNpc 则通常具备：

- `ObjectKind = EventNpc`
- 有稳定 `DataId`
- 有原生名字来源
- 可被鼠标悬停/选中
- 有原生绿色 NamePlate
- 可能拥有 `EventHandler` 或相关事件入口

这说明仅改显示名或刷新 NamePlate 不够。Brio Actor 未进入 NPC 分类与可目标对象路径时，NamePlateGui 很可能不会按 NPC 规则绘制它。

## Brio Actor 能不能注册进 NamePlateGui

目前没有发现 Dalamud 对外提供“把任意 GameObject 注册进 NamePlateGui”的高级 API。Dalamud 文档暴露了 NamePlate 配置项和 TargetManager 的 `MouseOverNameplateTarget`，但没有直接注册对象的接口。

因此当前最小路线不是“手动注册 NamePlateGui”，而是：

1. 读取真实 NPC 与 Brio Actor 的字段快照。
2. 对比 `ObjectKind / SubKind / DataId / NameId / IsTargetable / NamePlateIconId / EventHandler`。
3. 在 UnsafeMode 下只允许单字段写入实验。
4. 每次写入后立即 readback，再观察 NamePlate / Target 行为。

如果后续 Brio 或游戏内部有可调用的 NamePlate refresh/registration 方法，再封装成单独服务，不能写在 UI 里。

## 最小可试验步骤

1. 选中真实 EventNpc，点击“读取附近原生 NPC NamePlate 数据”。
2. 生成一个 Brio Actor，选中 Actor 实例，点击“读取 Brio Actor NamePlate 数据”。
3. 点击“字段对比”，重点看：
   - `ObjectKind`
   - `SubKind`
   - `DataId`
   - `EntityId`
   - `NameId`
   - `NamePlateIconId`
   - `EventHandler`
   - `IsTargetable`
4. 先尝试只读：
   - 读取原生名称
   - 读取 Targetable 状态
   - 刷新 Target 匹配
5. UnsafeMode=true 后，才允许做单字段写入：
   - 写 `ObjectKind=EventNpc`
   - 写 `SubKind=0`
   - 写 `IsTargetable=true`
   - 写 `DataId=参考 NPC`
   - 写 `Hitbox=参考 NPC`
6. 每一步观察：
   - readback 是否成功
   - actor 是否仍 valid
   - TargetManager 是否能选中
   - NamePlate 是否出现
   - 名字颜色是否变化

## 风险点

- 直接写 `ObjectKind / IsTargetable / DataId / EventHandler` 属于高风险 native 写入，必须保持 UnsafeMode=false 为默认。
- `EventHandler` 可能不是纯显示字段，乱写可能导致交互事件、客户端状态或内存访问异常。
- `DataId` 和 `NameId` 可能需要与对象类型、Excel 数据、事件处理器一致；只改其中一个字段可能无效。
- Brio Actor 可能缺少原生 NPC 初始化流程，即使字段 readback 成功，也未必被 NamePlateGui 纳入绘制。
- GPose / 切图会清理 Brio Actor，实验前应确认 RuntimeActorInstance 仍 valid。

## 当前工程对应 UI

新增页签“原生 NamePlate / Target 实验”承载这条路线：

- Target Probe
- EventNpc 快照
- Brio Actor 快照
- 字段对比
- 读取/刷新原生名称
- 读取 Targetable 状态
- 尝试设置显示名
- 尝试刷新 NamePlate
- UnsafeMode 下的单字段 EventNpc-like 实验

本轮不再做任务、不再做对话、不再拦截原生 Talk addon。
