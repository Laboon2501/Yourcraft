# LocalQuestReborn v1.0 Brio IPC 调研结果

本轮目标是让 `RealNpcSpawnService` 尝试通过 Brio IPC 生成本地可见 actor，同时不破坏现有虚拟 NPC overlay 和任务流程。

## 本机检查

开发环境中从 `%AppData%\XIVLauncher` 和 `%AppData%\XIVLauncherCN` 递归查找 Brio 相关配置/文件时没有得到可用结果。由于 Dalamud 插件是否安装、是否加载最好以游戏运行时的 `IDalamudPluginInterface.InstalledPlugins` 为准，LocalQuestReborn 现在在运行时通过 `BrioNpcBridgeService.StatusText` 显示：

- Brio 未安装
- Brio 已安装但未启用或未加载
- Brio 已加载但 IPC 不可用
- Brio IPC 可用及其版本

## Brio IPC 是否存在

Brio 仓库提供了公开 API 文件 `BrioAPI_V2.cs`。该文件确认 Brio 暴露 Dalamud IPC，并且 API 版本为 `(2, 0)`。

本轮使用的最小 IPC：

- `Brio.ApiVersion`
- `Brio.Actor.SpawnExAsync`
- `Brio.Actor.Despawn`
- `Brio.Actor.SetModelTransform`

这些 IPC 能完成最小 actor 生成、删除和设置模型 transform。外观应用仍未做，因为 GameNpc / Glamourer / Penumbra / MCDF 分别需要更多 Brio 外观接口或额外 IPC，不适合在第一个 spawn 实验里混进去。

## v1.0.1 版本兼容修正

实测环境检测到 Brio IPC 当前版本为 `3.0`。原实现只接受完全等于 `2.0`，导致还没尝试 spawn 就失败。现在兼容逻辑改为：

- 最低版本：`2.0`
- 当前版本 `>= 2.0` 就允许继续尝试 spawn
- UI 显示 Brio IPC 是否可用、当前 IPC 版本、是否兼容、最后一次 Spawn 错误信息

当前绑定的 IPC 名称和签名仍然是 Brio API v2 文件中公开的最小接口：

- `Brio.ApiVersion` -> `(int Major, int Minor)`
- `Brio.Actor.SpawnExAsync` -> `bool, bool, bool` 返回 `Task<IGameObject?>`
- `Brio.Actor.Despawn` -> `IGameObject` 返回 `bool`
- `Brio.Actor.SetModelTransform` -> `IGameObject, Vector3?, Quaternion?, Vector3?, bool` 返回 `bool`

如果 Brio IPC 3.0 改动了这些签名，LocalQuestReborn 会捕获异常，写入 Dalamud log，并在实验 NPC 页签显示最后一次 Spawn 错误信息。当前还没有游戏内异常堆栈可记录；下一步需要在启用 Brio 3.0 的环境中点击“生成选中 NPC”确认具体失败点。

## 已实现

- 新增并接线 `BrioNpcBridgeService`
- `RealNpcSpawnService.SpawnSelectedNpc` 调用 `Brio.Actor.SpawnExAsync`
- Spawn 成功后用 `Brio.Actor.SetModelTransform` 尝试把 actor 放到 `CustomNpc.position`
- `TryDespawn` 调用 `Brio.Actor.Despawn`
- `TryDespawnAll` 清理本插件记录的 Brio actor
- `TryApplyAppearance` 先返回明确失败原因，不假装已支持外观
- 实验 NPC 页签显示 Brio 状态和最后一次成功/失败原因
- 区域切换、插件 `Dispose()` 时继续调用 `DespawnAll`

## 已知限制

1. `SpawnExAsync` 默认生成 Brio actor，但不等价于“按 GameNpc id 生成某个原生 NPC 模型”。
2. `GameNpc` 外观来源暂未套用到 actor。后续需要确认 Brio 是否有稳定外观 IPC，或者接 Glamourer/Penumbra/MCDF 的独立 IPC。
3. `SetModelTransform` 是 Brio 的模型 transform，不一定等价于游戏世界原生坐标移动；是否能在普通游戏状态稳定显示，需要进游戏实测。
4. 只记录本插件本次运行生成的 actor。插件重载后无法管理旧实例，必须依赖 Brio 自己的清理能力或后续建立更稳定的 actor 标记方案。

## 没有 IPC 时的替代路线

如果用户当前安装的 Brio 版本没有上述 IPC，LocalQuestReborn 不会复制 Brio 源码。替代路线有三种：

1. 依赖 Brio assembly：把 Brio 作为运行时插件依赖，通过公开 API 或服务接口调用。优点是能力完整；缺点是版本耦合强。
2. Fork Brio spawn 服务：维护一份独立后端实现。优点是可控；缺点是维护成本高，而且要严格处理 GPL/AGPL 许可证边界。
3. 参考 AQuestReborn 架构：LocalQuestReborn 保持任务系统和 NPC 数据，真实 actor 层单独做后端插件或可选模块。优点是核心任务系统不被重依赖污染。

当前实现选择 IPC 路线，并在失败时显示明确原因。
