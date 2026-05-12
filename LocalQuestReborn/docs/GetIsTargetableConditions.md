# GetIsTargetable Conditions

本轮追踪目标：解释为什么 `ObjectKind=EventNpc` + `TargetableStatus=0` 后，`GameObject.GetIsTargetable()` 仍然返回 `false`。

## 源码取证

读取 FFXIVClientStructs 当前源码后，`GameObject.GetIsTargetable()` 不是 C# 里展开的条件判断，而是 virtual/native 调用：

- 类型：`FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject`
- 方法：`GetIsTargetable()`
- 形态：native virtual function wrapper
- 相关字段：`TargetableStatus`，类型为 `[Flags] ObjectTargetableFlags`

`ObjectTargetableFlags` 的关键位：

- `IsTargetable = 1 << 1`，数值 `2`
- `ReadyToDraw = 1 << 6`，数值 `64`

因此此前实验写 `TargetableStatus=0` 的含义不是“可选中”，而是清空这些标志位。下一轮最小实验应该先写：

```text
TargetableStatus = IsTargetable | ReadyToDraw = 66
```

源码还显示 `GetGameObjectId()` 是派生方法，不是一个简单独立字段。对普通 object，它会根据 `ObjectKind` 从 `EntityId` 或 `BaseId` 等 native 字段组合出 `GameObjectId`。所以 UI 中的“写 GameObjectId=参考 NPC”当前实现为“按 BaseId route 写入并立即读回 GameObjectId”，不是写一个不存在的独立 `GameObjectId` 字段。

## GetIsTargetable 可能检查的条件

由于 `GetIsTargetable()` 是 native virtual function，真实逻辑需要通过字段对比和单字段实验反推。当前最可能参与判断的条件：

1. `TargetableStatus`
   - 必须至少包含 `IsTargetable` 位。
   - 很可能还需要 `ReadyToDraw` 位。

2. `ObjectKind / SubKind`
   - 鼠标选择过滤会区分 `Pc / EventNpc / BattleNpc / EventObj`。
   - Brio actor 默认是 `Pc` clone，真实 Host NPC 是 `EventNpc`。

3. `DrawObject`
   - `DrawObject` 非空说明模型存在。
   - 如果 native selection/raycast 依赖 draw object 或 render object，Brio actor 的 draw object 状态必须对齐。

4. `RenderFlags`
   - 可能参与可见性/渲染状态判断。
   - UI 已增加“写 RenderFlags=参考 NPC”单字段实验。

5. `IsReadyToDraw()`
   - 与 `ReadyToDraw` 位和 draw object 状态相关。
   - 条件表会显示真实 NPC 与 Brio actor 的 readback。

6. `HitboxRadius`
   - 鼠标 hover/raycast 可能需要非零 hitbox。
   - 已保留“写 HitboxRadius=参考 NPC”。

7. `BaseId/DataId`
   - 对 `EventNpc` 体系，`BaseId` 通常对应 Dalamud public `DataId`。
   - `DataId=0` 可能导致没有有效 NPC 身份。

8. `EntityId / GameObjectId`
   - `GameObjectId` 是派生值。
   - `EntityId` 写入很危险，可能影响 object identity，只在二次确认后显示按钮。

9. `EventHandler`
   - EventNpc 的交互/名字牌可能依赖 handler/director context。
   - 复制真实 NPC 的 `EventHandler` 极危险，本轮只做二次确认后的单字段实验。

10. ObjectTable 可达性
   - 即使 native 字段被写回，如果 ObjectTable 无法按 `ObjectIndex` 找到该 actor，TargetSystem/NamePlate 仍可能忽略它。

## 当前 Brio actor 可能不满足的条件

基于此前结果和新增条件表，重点看这些差异：

- `ObjectKind` 默认是 `Pc`
- `SubKind` 默认是 PC 子类型，不是 EventNpc 子类型
- `TargetableStatus` 可能缺少 `IsTargetable` / `ReadyToDraw`
- `GetIsTargetable()` 为 false
- `DataId/BaseId` 可能为 0
- `GameObjectId` 可能不是 EventNpc 体系期望的派生值
- `EventHandler` 可能为空或不是 EventNpc handler
- `HitboxRadius` 可能和真实 NPC 不同
- ObjectTable 地址/索引可能无法与 native address 匹配

## 新增 UI 条件表

“EventNpc Native Probe / 原生 EventNpc 取证” 中新增 `GetIsTargetable 条件表`，对比真实 EventNpc 和 Brio actor：

- `ObjectKind`
- `SubKind`
- `TargetableStatus`
- `RenderFlags`
- `GameObjectId`
- `EntityId`
- `DataId`
- `BaseId`
- `HitboxRadius`
- `DrawObject 是否非空`
- `EventHandler 是否非空`
- `Position 是否有效`
- `ObjectTableIndex`
- `当前 ObjectTable 是否能按 ObjectIndex 找到它`
- `ObjectTable address 是否匹配`
- `GetIsTargetable()`
- public `IsTargetable`

## 新增单字段实验顺序

建议按这个顺序做，不要跳到 EventHandler：

1. 保存真实 EventNpc native dump。
2. 保存 Brio actor native dump。
3. 写 `TargetableStatus=IsTargetable|ReadyToDraw`，观察 `GetIsTargetable()`。
4. 写 `SubKind=参考 NPC`，观察 `GetIsTargetable()`。
5. 写 `HitboxRadius=参考 NPC`，确认鼠标命中半径不是问题。
6. 写 `BaseId/DataId=参考 NPC`，观察 `GameObjectId` 派生值变化。
7. 写 `RenderFlags=参考 NPC`，观察 `IsReadyToDraw()` 和 `GetIsTargetable()`。
8. 写 `GameObjectId=参考 NPC`。当前实现会说明没有独立字段，并走 BaseId route。
9. 二次确认后才写 `EntityId=参考 NPC`。
10. 最后才考虑“极危险：复制 EventHandler=参考 NPC”。

每一步写完后插件会自动对 actor 做一次 native dump，UI 可直接看 `GetIsTargetable()`、public `IsTargetable`、ObjectTable 匹配和 TargetManager readback。

## 当前判断

`TargetableStatus=0` 读回成功但 `GetIsTargetable=false` 并不意外，因为 `0` 很可能代表没有 `IsTargetable` 标志。下一步最有价值的是写 `66` 或参考 NPC 的 `TargetableStatus`，再看 native virtual `GetIsTargetable()` 是否变化。
