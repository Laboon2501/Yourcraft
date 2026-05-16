# Native Targetable Fields

本轮目标是把 Brio 生成的 `Pc` clone 和真实 `EventNpc` 放到同一套 native `GameObject` 视角下对比。结论先保持克制：本轮只确认字段、只读 dump、单字段实验；不做“一键改成 NPC”。

## 取证来源

- 本机 Dalamud dev XML：`%AppData%\XIVLauncher\addon\Hooks\dev\FFXIVClientStructs.xml`
- 当前项目已编译通过的 FFXIVClientStructs 类型：`FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject`
- 现有 Dalamud/Brio managed 对象反射快照：`TargetProbeService` / `NativeNpcProbeService`

## GameObject 字段

`FFXIVClientStructs` 当前版本确认 `GameObject` 上存在这些 native 字段或方法：

| 字段/方法 | 当前项目使用方式 | 来源 |
|---|---|---|
| `ObjectKind` | native 字段，可读；UnsafeMode 下单字段写 `ObjectKind.EventNpc` | `GameObject.ObjectKind` |
| `SubKind` | native 字段，可读；UnsafeMode 下单字段写 `0` | `GameObject.SubKind` |
| `TargetableStatus` | native 字段，可读；UnsafeMode 下单字段写 `0` 作为候选实验 | `GameObject.TargetableStatus` |
| `GetIsTargetable()` | native 方法，只读 | `GameObject.GetIsTargetable()` |
| `BaseId` | native 字段；Dalamud public `DataId` 很可能映射到这里 | `GameObject.BaseId` |
| `HitboxRadius` | native 字段，可读；UnsafeMode 下可单字段写参考 NPC 半径 | `GameObject.HitboxRadius` |
| `NamePlateIconId` | native 字段，可读 | `GameObject.NamePlateIconId` |
| `RenderFlags` | native 字段，可读 | `GameObject.RenderFlags` |
| `EventHandler` | native 指针字段，可读；本轮不写 | `GameObject.EventHandler` |
| `GetGameObjectId()` | native 方法，只读 | `GameObject.GetGameObjectId()` |
| `GetNameId()` | native 方法，只读；当前版本不是直接字段 | `GameObject.GetNameId()` |
| `GetNamePlateColorType()` | native 方法，只读 | `GameObject.GetNamePlateColorType()` |

代码里通过 `Marshal.OffsetOf<GameObject>(fieldName)` 在 UI dump 中显示字段 offset；方法型数据会标注为 `method:*`，没有 offset。

## ObjectKind / SubKind

`ObjectKind` 和 `SubKind` 是 native `GameObject` 字段。真实 `EventNpc` 与 Brio actor 的核心差异预计在这里首先出现：

- Brio actor 当前读回：`ObjectKind=Pc`，`SubKind=4`
- 真实 EventNpc 需要通过 UI 的 “只读 Dump 当前 Target” 保存现场值
- 单字段实验按钮只在 `UnsafeMode=true` 时显示：
  - 写 `ObjectKind=EventNpc`
  - 写 `SubKind=0`

这两个字段单独变化不一定足以进入 TargetSystem，因为鼠标选择还可能依赖 ObjectTable 注册状态、raycast/selection filter、TargetableStatus、hitbox、EventHandler 和 nameplate 生成路径。

## IsTargetable

当前取证显示 `IsTargetable` 更像 public/read-only 包装或计算结果，而不是一个可直接写的 public property。native 层实际可见的是：

- `TargetableStatus` 字段
- `GetIsTargetable()` 方法

因此本轮实现的按钮不再写 `IsTargetable=true`，而是写候选字段 `TargetableStatus=0`，随后立即 readback：

- `TargetableStatus`
- `GetIsTargetable()`

如果 `TargetableStatus=0` 后 `GetIsTargetable()` 仍为 false，说明还有其他条件参与计算。

## TargetSystem 选择条件

XML 能确认 `Character.GetTargetId()` / `SetTargetId()` 的文档引用了 `TargetSystem`，但这并不等于鼠标 hover/点击能选中对象。鼠标选择通常还需要：

- 对象在客户端 ObjectTable/可选择列表中
- `GameObject.GetIsTargetable()` 返回 true
- hitbox / collision / distance 能被选择逻辑命中
- ObjectKind/SubKind 被 selection filter 接受
- 对象可能需要有效的 `GameObjectId` / `EntityId`

本轮没有写 `EventHandler`、`GameObjectId`、`EntityId`，因为这些字段更容易影响客户端状态机和网络对象生命周期。

## NamePlate

`GameObject` / `Character` / `BattleChara` 链路上确认存在：

- `NamePlateIconId`
- `NameplateOffset`
- `NameplateOffsetScaleMultiplier`
- `NameplateOffsetTarget`
- `GetNamePlateColorType()`
- `GetNamePlateColors()`
- `GetNamePlateWorldPosition()`

多个 `EventHandler`/Director 类型也暴露 `GetNameplateIconForObject(GameObject*)`，说明原生 nameplate 不只是一个简单字符串，还可能经由 object kind、event handler/director 和 nameplate GUI 更新路径共同决定。

当前不能确认 NamePlateGui 是否只扫描 ObjectTable，还是维护单独列表；但 Brio actor 作为 `Pc` clone 且 `GetIsTargetable=false`，很可能不满足原生 NPC nameplate/hover 的过滤条件。

## 为什么 Brio actor 进不了鼠标选择

当前证据指向这些原因：

1. Brio actor 是 `Pc` clone，不是原生 `EventNpc`。
2. `IsTargetable=False`，native 侧需要继续看 `TargetableStatus` 和 `GetIsTargetable()`。
3. `DataId/BaseId` 为 0 时，无法关联真实 NPC Excel/handler 身份。
4. 可能没有 EventNpc 需要的 `EventHandler` / director context。
5. 原生 nameplate 颜色、名字、hover 不是单一字段决定，可能由 `GetNamePlateColorType()`、nameplate icon、object kind 和 EventHandler 共同决定。

## 新增实验入口

UI 的 “EventNpc Native Probe / 原生 EventNpc 取证” 中新增：

- 只读 Dump 当前 Target
- 只读 Dump 选中 Actor
- 对比 Native GameObject Dump

`UnsafeMode=true` 后才显示单字段写入按钮：

- 写 native `ObjectKind=EventNpc`
- 写 native `SubKind=0`
- 写 native `TargetableStatus=0`
- 写 native `BaseId/DataId=参考 NPC`
- 写 native `HitboxRadius=参考 NPC`

每个按钮只写一个字段，写后立即 readback。默认 Safe Mode 不显示这些写入按钮。

## 下一步判断标准

建议实际游戏里按这个顺序取证：

1. 选中真实 EventNpc，点 “只读 Dump 当前 Target”。
2. 选中/选定 Brio actor，点 “只读 Dump 选中 Actor”。
3. 点 “对比 Native GameObject Dump”。
4. 在 UnsafeMode 下逐个测试 `TargetableStatus`、`ObjectKind`、`SubKind`、`BaseId/DataId`、`HitboxRadius`。
5. 每一步观察 readback、`GetIsTargetable()`、TargetManager 是否能匹配。

如果所有字段 readback 都变化但仍不可选中，说明 Brio actor 缺的是 TargetSystem/ObjectTable/nameplate 注册路径，而不是单个 `GameObject` 字段。
