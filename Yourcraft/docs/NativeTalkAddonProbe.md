# Native Talk Addon Probe

## 目标

研究玩家右键/确认键点击真实 EventNpc 时，客户端侧哪些目标对象和 UI addon 状态会变化，为后续 Yourcraft 接管本地多段对话做准备。

## 本轮实现

新增 `NativeTalkProbeService`：

- 记录当前 `TargetManager.Target`。
- 提供 `Confirm/Interact Probe` 手动按钮。
- 记录 target name、objectIndex、dataId。

由于当前工程还未注入 `IAddonLifecycle`，本轮先以手动 probe 形式保存事件日志。后续可扩展为监听：

- `SelectString`
- `Talk`
- `TalkSubtitle`
- `SelectIconString`
- `JournalDetail`
- `ContentsInfo`
- `EventItem`

每次 addon 打开时应记录：

- addon name
- target npc dataId
- text node 内容
- option 数量
- event id 如可读

## 多段对话路线

不要把多段对话写入原 NPC 数据。

本地多段对话应由 Yourcraft 自己维护：

1. 原生 NPC 只作为 host / 交互入口。
2. 玩家 target host NPC 后：
   - `/godmode talk` 直接打开 Yourcraft `DialogueWindow`。
   - 后续可监听原生 interact，再选择关闭/覆盖原生 Talk addon。
3. 对话文本和状态仍保存在 `quests.json` 和插件配置中。

## 已接入的最小 host 路线

`CustomNpc` 新增：

- `nativeHostMode`
- `hostDataId`
- `hostObjectIndex`
- `hostTerritoryType`
- `hostName`
- `overrideNativeName`
- `interceptNativeTalk`
- `useLocalDialogueOnInteract`

NPC 管理页提供“从当前选中 NPC 设置为 Host”。

当当前 target 匹配 host DataId/ObjectIndex，执行 `/godmode talk` 会走 Yourcraft 本地对话。
