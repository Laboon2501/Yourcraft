# Yourcraft v0.9 最小真实 NPC 生成实验

本轮新增 `RealNpcSpawnService`，但没有真正生成 3D actor。原因是 Brio / AQuestReborn 的真实 actor 生成链路不是一个稳定、独立、低依赖的小接口；直接接入会把 Yourcraft 从“本地任务运行时”变成“依赖 Brio 内部服务图的 actor 控制插件”。当前版本先把接口、UI、生命周期清理和日志打通。

## 参考来源

- Brio：<https://github.com/Etheirys/Brio>
- Brio actor 管理：<https://github.com/Etheirys/Brio/blob/main/Brio/Entities/EntityActorManager.cs>
- Brio actor 外观能力：<https://github.com/Etheirys/Brio/blob/main/Brio/Capabilities/Actor/ActorAppearanceCapability.cs>
- AQuestReborn：<https://github.com/Sebane1/AQuestReborn>
- AQuestReborn custom NPC 数据：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/CustomNpc/CustomNpcCharacter.cs>
- AQuestReborn interactive NPC 运行层：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/InteractiveNpc.cs>
- AQuestReborn 项目依赖：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/AQuestReborn.csproj>

## Brio actor spawn 思路

Brio 的 actor 表现不是单纯调用一个 `SpawnNpc(modelId, position)`。它围绕 actor entity、spawn flags、object table、pose capability、appearance capability 和外部插件 IPC 形成一套运行时。`EntityActorManager` 负责把游戏对象附加成 Brio actor entity；`ActorAppearanceCapability` 再处理外观、MCDF、Glamourer、Penumbra、CustomizePlus 等能力。

这说明 Yourcraft 后续可以借鉴分层：

- `RealNpcSpawnService` 管生命周期和运行态句柄。
- `NpcAppearanceService` 管 GameNpc / Glamourer / Penumbra / MCDF 外观来源。
- `NpcTransformService` 管坐标、朝向、区域切换后的安全清理。

但不应直接复制 Brio 内部类。Brio 是 GPL 项目，并且代码与自身服务容器高度耦合。

## AQuestReborn custom NPC 思路

AQuestReborn 的 custom NPC 更像一个完整样例：它有 NPC 数据、外观来源、召唤/解除按钮、运行态 `InteractiveNpc`、位置更新、跟随、动画和外观重应用。项目文件显示它依赖 Brio、MCDF-Loader、Glamourer.Api、ECommons、RoleplayingQuestCore 等多个项目或包。

可以借鉴的部分：

- NPC 数据与运行态对象分离。
- 每个生成出来的 NPC 都有可销毁的运行态记录。
- 区域切换、插件卸载、目标消失时必须清理。
- 外观重应用应通过服务层，而不是写死在 UI。

不适合直接复制的部分：

- `InteractiveNpc` 体量很大，混合了移动、跟随、战斗、动画、外观和对话上下文。
- 依赖链太重，不适合直接进入 Yourcraft 的核心插件。
- AGPL 代码不能复制进本项目；本轮只参考接口边界和生命周期思路。

## 当前阻塞点

1. Brio 没有在 Yourcraft 中可直接调用的稳定轻量 spawn API。
2. Brio actor spawn 涉及内部服务容器、object table 索引、spawn flags 和 native actor 生命周期。
3. AQuestReborn 的真实 NPC 依赖 Brio、Glamourer、Penumbra/MCDF 生态，不符合 v0.9 “不破坏现有虚拟 NPC 系统”的低风险目标。
4. 真实 actor 在普通游戏状态、区域切换、战斗状态、GPose 状态下的稳定性需要单独做最小复现。

## v0.9 实现范围

本轮已新增：

- `RealNpcSpawnService`
- 实验 NPC 页签按钮：
  - 生成选中 NPC
  - 删除选中 NPC
  - 删除全部
  - 重新应用外观
- 每个按钮都通过 `try/catch` 保护，失败只写日志。
- `Framework.Update` 检测区域切换并调用 `DespawnAll()`。
- 插件 `Dispose()` 调用 `DespawnAll()`。

当前 `RealNpcSpawnService` 不生成模型，只打印 NPC id、坐标、外观来源，以及 GameNpc / Glamourer / MCDF / Penumbra 的关键字段。

## 下一步建议

1. 先尝试通过 Brio IPC 或公开服务接口做最小 actor spawn，不把 Brio 内部类复制进项目。
2. 如果 Brio 没有合适 IPC，新增 `IRealNpcBackend` 接口，让 Yourcraft 支持“无后端 / Brio 后端 / 未来其他后端”。
3. 真实 spawn 成功后，第一版只支持 `GameNpc` 或固定 `ModelChara`，暂不接 Glamourer/MCDF。
4. 通过区域切换、插件卸载、重复生成/删除做稳定性测试后，再接外观重应用。
