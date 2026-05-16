# TargetSystem Candidate Conditions

本轮结论：`ObjectKind / SubKind / TargetableStatus / ObjectTable` 都对齐以后，`GetIsTargetable()` 仍然是 `false`，说明 Brio actor 没进入 TargetSystem 的可选中候选链，或者缺失了更深的 identity / handler / target filter 条件。

## 源码取证

参考：

- FFXIVClientStructs `TargetSystem.cs`
  - `TargetSystem.Target`
  - `SoftTarget`
  - `MouseOverTarget`
  - `MouseOverNameplateTarget`
  - `TargetableObjectsOnScreen`
  - `ObjectFilterArray1/2/3`
  - `GetMouseOverObject(...)`
  - `SetHardTarget(...)`
  - `InteractWithObject(...)`
- FFXIVClientStructs `GameObject.cs`
  - `ObjectKind`
  - `SubKind`
  - `TargetableStatus`
  - `EntityId`
  - `BaseId`
  - `ObjectIndex`
  - `DrawObject`
  - `EventHandler`
  - `RenderFlags`
  - `GetGameObjectId()`
  - `GetIsTargetable()`
  - `GetNamePlateColorType()`
  - `IntersectsRay(...)`

## 1. 鼠标选中候选对象来自哪里？

`TargetSystem` 不只是直接扫 Dalamud `IObjectTable`。源码里存在 `TargetableObjectsOnScreen` 和多个 `GameObjectArray` filter array。`GetMouseOverObject(int x, int y)` 默认使用 `ObjectFilterArray1` 加 camera 做 raycast/过滤。

这意味着一个对象“在 ObjectTable 里”只是最低条件。它还必须进入 TargetSystem 当前 filter array / on-screen targetable array，才有机会被鼠标 hover/选中。

## 2. 只在 ObjectTable 里是否足够？

不够。当前实验已经确认：

- ObjectTable 可以按 index 找到 Brio actor。
- ObjectTable address 可匹配。
- DrawObject 非空。
- `IsReadyToDraw=True`。

但 `GetIsTargetable=False` 且鼠标不可选中，说明 ObjectTable 可见不等于 TargetSystem 候选可见。

## 3. GameObjectId / EntityId 是否必须满足某种格式？

`GameObjectId` 在 `GameObject.cs` 中是派生值，不是独立字段。源码注释显示大致规则：

- `EntityId == 0xE0000000` 有特殊处理。
- `BaseId == 0` 或 `ObjectIndex` 在特定范围时，可能以 `ObjectIndex` 作为 ObjectId。
- `BaseId != 0` 时，可能以 `BaseId` 作为 ObjectId。
- 否则使用 `EntityId`。

所以“写 GameObjectId=参考 NPC”不能直接写一个字段。当前 UI 实现会提示并走 `BaseId` route，然后立即 readback `GetGameObjectId()`。

`EntityId` 属于对象身份字段，可能影响 NameCache、TargetSystem identity、网络对象/本地对象识别。它是中高风险字段，本轮放在二次确认后。

## 4. EventNpc 是否必须有 EventHandler？

很可能需要，至少对于原生 EventNpc 的交互和部分 nameplate/icon 行为。`GameObject` 有 `EventHandler*` 字段，多个 EventHandler/Director 类型有 `GetNameplateIconForObject(GameObject*)`。这说明 EventNpc 的名字牌/交互上下文不只是 `ObjectKind=EventNpc`。

但复制真实 NPC 的 `EventHandler` 指针很危险：

- 可能把 Brio actor 指向不属于它的 event context。
- 可能让原 NPC 和 clone 共享 handler 状态。
- 可能在 handler 生命周期变化后悬空。

所以 UI 只在二次确认后显示“复制 EventHandler=参考 NPC”。

## 5. GetIsTargetable 是否依赖 EventHandler？

源码没有展开 `GetIsTargetable()` 的 C# 条件，它是 native virtual function。不能确认直接依赖 EventHandler。

但当前现象说明它至少不只看：

- `ObjectKind`
- `SubKind`
- `TargetableStatus`
- `ObjectTable`
- `DrawObject`
- `IsReadyToDraw`

后续需要通过只读条件表和单字段实验判断是否还依赖：

- `RenderFlags`
- `BaseId/DataId`
- `EntityId`
- `GameObjectId` 派生值
- `EventHandler`
- `TargetSystem` filter array 注册

## 6. NamePlate 是否依赖 NameId/DataId/EventHandler？

`GameObject` 有：

- `NamePlateIconId`
- `NameplateOffset`
- `GetNameId()`
- `GetNamePlateColorType()`
- `GetNamePlateColors()`
- `GetNamePlateWorldPosition()`

`GameObject.GetNamePlateColorType()` 注释里提到颜色表索引，其中 NPC 颜色在 `NamePlateEdgeNpc / NamePlateColorNpc` 对应的索引附近。这个返回值是方法，不是已确认可写字段。

因此 NamePlate 很可能依赖：

- `ObjectKind`
- `BaseId/DataId`
- `NameId`
- `EventHandler` / director
- `RenderFlags.Nameplate`
- TargetSystem / NamePlateGui 当前列表

本轮新增按钮：

- 写 `NamePlateIconId=参考 NPC`，可实际写字段。
- 写 `NamePlateColorType=参考 NPC`，当前只记录“未找到独立可写字段”，不做伪写入。

## 7. Brio actor clone 为什么即使改字段仍然不进入 TargetSystem？

最可能原因：

1. Brio actor 是后生成的 `Pc` clone，TargetSystem 的 `TargetableObjectsOnScreen` / filter arrays 可能没有把它纳入候选。
2. `GetIsTargetable()` 是 native virtual function，Brio clone 的 vtable/对象类型仍按 Character/Pc 路径运行，不会因为写 `ObjectKind` 就完整变成 EventNpc。
3. `GameObjectId` 派生值可能仍不像原生 EventNpc。
4. `EventHandler` 为空或不匹配，导致 EventNpc/nameplate/interaction 路径缺上下文。
5. nameplate 和 hover 可能还依赖 render/nameplate manager 的独立更新列表，不只依赖 ObjectTable。

## 本轮 UI 增强

Native Dump 增加只读字段：

- `GameObjectId`
- `EntityId`
- `EventHandler`
- `DrawObject`
- `NamePlateIconId`
- `NamePlateColorType`
- `TargetObjectId`
- `RenderFlags`
- `ObjectKind`
- `SubKind`
- `BaseId/DataId`
- `TargetableStatus`
- `GetIsTargetable`
- `IsReadyToDraw`
- `IsDead`
- `HitboxRadius`
- `ObjectTable index/address`
- `TargetSystem.Target`
- `TargetSystem.MouseOverTarget`
- `TargetSystem.MouseOverNameplateTarget`

## 实验顺序

不再建议继续改 `ObjectKind/SubKind/TargetableStatus` 作为主路径。下一步建议：

1. 安全优先：写 `HitboxRadius=参考 NPC`。
2. 安全优先：写 `RenderFlags=参考 NPC`。
3. 安全优先：写 `NamePlateIconId=参考 NPC`。
4. 安全优先：尝试 `NamePlateColorType=参考 NPC`，但当前不会写入，只记录无独立字段。
5. 中风险：写 `GameObjectId=参考 NPC`，当前实际走 `BaseId` route。
6. 中风险：写 `BaseId/DataId=参考 NPC`。
7. 高风险：二次确认后写 `EntityId=参考 NPC`。
8. 高风险：二次确认后复制 `EventHandler=参考 NPC`。

每次写完自动 dump Brio actor，并记录：

- old/new/readback
- `GetIsTargetable`
- 当前 `TargetManager.Target`

## 人工测试记录

UI 新增：

- “我确认鼠标能悬停”
- “我确认鼠标能选中”
- “我确认仍然不能选中”

点击后会把当前字段组合、`GetIsTargetable`、当前 Target 和人工结果写入 Dalamud log，方便对照哪一步真正改变了 TargetSystem 行为。
