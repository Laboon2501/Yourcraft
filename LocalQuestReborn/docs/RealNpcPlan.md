# LocalQuestReborn v0.5 真实本地 NPC 模型方案调研

本轮目标是判断 LocalQuestReborn 后续是否可以从“ImGui 本地 NPC 站牌”升级到“游戏世界内可见的本地 NPC 模型”。结论先放前面：可以做，但不适合在 v0.5 直接接入 Brio 或 AQuestReborn 的完整实现。它们的 actor、外观、姿势和资源链路都比较重，应该先保留 LocalQuestReborn 自己的任务运行时，只新增一层实验接口，后续再按能力逐步接入。

## 调研来源

- Brio：<https://github.com/Etheirys/Brio>
- Brio actor 管理：<https://github.com/Etheirys/Brio/blob/main/Brio/Entities/EntityActorManager.cs>
- Brio actor 外观能力：<https://github.com/Etheirys/Brio/blob/main/Brio/Capabilities/Actor/ActorAppearanceCapability.cs>
- Brio MCDF 文件入口：<https://github.com/Etheirys/Brio/blob/main/Brio/Files/MareCharacterDataFile.cs>
- AQuestReborn：<https://github.com/Sebane1/AQuestReborn>
- AQuestReborn 自定义 NPC 数据：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/CustomNpc/CustomNpcCharacter.cs>
- AQuestReborn 交互 NPC：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/InteractiveNpc.cs>
- AQuestReborn 自定义 NPC UI：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/CustomNpc/CustomNpcWindow.cs>
- AQuestReborn 项目依赖：<https://github.com/Sebane1/AQuestReborn/blob/master/AQuestReborn/AQuestReborn.csproj>

## Brio 能不能创建本地可见 actor

可以。Brio 内部有 actor 实体、actor spawn 管线、object table 监听、外观能力、pose 能力和资源能力。`EntityActorManager` 会从 `IObjectTable` 附加角色对象，并通过 `ActorSpawnService` 处理 Brio 生成 actor 的标记；`ActorEntity` 再挂接外观、pose、隐藏显示等能力。

但它不是一个轻量的“给我一个坐标和模型 ID，直接生成 NPC”的公共小接口。真实可用链路会涉及 Brio 自己的服务容器、实体系统、spawn flags、object monitor、posing、外观 redraw，以及对 Dalamud/FFXIVClientStructs 的较深依赖。

## AQuestReborn 的 custom NPC 是怎么做的

AQuestReborn 更接近 LocalQuestReborn 想要的功能形态：它有自定义 NPC 数据、编辑窗口、召唤/解除 NPC、跟随/移动、外观应用和对话/任务层集成。

从代码结构看，它的自定义 NPC 大致由三层组成：

1. 数据层：`CustomNpcCharacter` 保存 NPC 名称、外观配置、位置、是否跟随玩家、MCDF/Glamourer/Penumbra 相关字段。
2. 运行层：`InteractiveNpc` 持有实际 `ICharacter`，通过 Brio 的 EntityManager/PosingCapability/ActorAppearanceCapability 操作模型位置、旋转、缩放、动画和外观。
3. UI 层：`CustomNpcWindow` 提供 NPC 编辑、外观来源选择、召唤/解除、位置调试等按钮，并调用运行层重应用外观或生成/移除 NPC。

也就是说，AQuestReborn 的 custom NPC 不是单靠 Dalamud 原生 API 完成的，而是建立在 Brio、Glamourer、Penumbra、MCDF-Loader、AnamCore 等一组能力之上。

## 是否依赖 GPose

Brio 明显以 GPose 工作流为核心之一。`EntityActorManager` 在附加已有 actor 时检查 `IsGPose()`，Brio 的 actor 外观能力也监听 GPose 状态变化，并接入 pose、摄影、actor 控制等功能。

AQuestReborn 的 NPC 运行逻辑看起来不只是 GPose 内使用；它有跟随玩家、位置更新、战斗/闲置行为等普通状态需求。但因为它依赖 Brio 的 actor/pose 能力，实际能否稳定在普通游戏状态显示，取决于 Brio spawn 出来的本地 actor 是否在当前 Dalamud/游戏版本下支持普通状态，以及它绕过 GPose 限制的具体实现。这个部分不能只靠复制 UI 层代码确认，需要单独做最小实验。

LocalQuestReborn 的结论：v0.5 不声明已支持普通状态真实模型，只定义实验接口和日志按钮。

## 是否依赖 MCDF

Brio 本身支持 MCDF，但 MCDF 不是“创建 actor”的必要条件，它是外观导入/导出的一种来源。`ActorAppearanceCapability` 可以加载 MCDF，并通过 MCDF 服务把外观应用到角色。

AQuestReborn 则把 MCDF 当作自定义 NPC 外观来源之一，同时也支持怪物模型、Glamourer/Penumbra 外观方案。它的项目文件直接引用了 `MCDF-Loader`，UI 里也有从玩家外观创建 MCDF、选择 MCDF 文件、重新应用 MCDF 外观等流程。

LocalQuestReborn 的结论：后续可以把 MCDF 作为可选外观包格式，但不应该作为 v0.5 或最小 NPC spawn 的硬依赖。

## 是否能在普通游戏状态显示

Brio 有生成本地 actor 的能力，AQuestReborn 也在运行层维护 NPC 的当前位置、移动、跟随和外观，因此技术上有希望在普通游戏状态显示本地 NPC。

但当前调研不能把它视为 LocalQuestReborn 可直接复用的稳定能力，原因是：

- Brio 的一部分 actor 附加逻辑明确筛选 GPose actor。
- AQuestReborn 的实现与 Brio 内部能力、AnamCore、MCDF-Loader、Glamourer/Penumbra、RoleplayingQuestCore 等耦合很深。
- 普通状态下本地 actor 的生命周期、object table 索引、区域切换、战斗状态、性能和崩溃风险都需要单独验证。

LocalQuestReborn 后续应先做“最小可删除的本地 actor 实验”：只在测试按钮触发时生成一个无外观自定义或固定模型 actor，记录 object index，支持区域切换/插件卸载时安全清理。通过以后，再考虑外观、动画、跟随和任务事件绑定。

## 可以借鉴的代码/接口

- Brio 的 actor/entity 分层：把“任务 NPC 数据”和“实际游戏对象句柄”分开，不让 quest json 直接持有 native 指针。
- Brio 的 capability 思路：spawn、appearance、pose、transform 各自独立，LocalQuestReborn 后续也可以拆成 `ExperimentalNpcService`、`NpcAppearanceService`、`NpcTransformService`。
- AQuestReborn 的 `CustomNpcCharacter` 数据字段思路：NPC 名称、位置、外观来源、跟随状态、固定站位等字段可以作为未来 `CustomNpc` 扩展参考。
- AQuestReborn 的 `InteractiveNpc` 运行态对象：保存 `ICharacter`、缓存 object index、每帧更新位置、按玩家距离做裁剪或行为控制，这些都适合借鉴思路。
- AQuestReborn 的编辑 UI：召唤/解除、移动到玩家附近、保存 NPC 站位、重应用外观，适合映射到 LocalQuestReborn 的 NPC 管理页。

## 不适合直接复制的部分

- 不直接复制 Brio 的内部 EntityManager/ActorSpawnService 集成。它们是 Brio 插件自己的服务图，不是一个独立小模块。
- 不直接复制 AQuestReborn 的 `InteractiveNpc`。该类很大，混合了移动、战斗、动画、外观、跟随、头部朝向和调试逻辑，超出 LocalQuestReborn 当前需求。
- 不把 Glamourer、Penumbra、MCDF-Loader、AnamCore、RoleplayingQuestCore 直接加入 v0.5。这样会显著提高构建和运行风险，也会让 dev plugin 分发变复杂。
- 不在任务系统里直接保存 Brio/AnamCore 的 native 状态。LocalQuestReborn 的 json 应继续保存稳定、可分享的数据，运行态对象由服务层临时管理。

## LocalQuestReborn 后续建议路线

1. v0.5：只加入 `ExperimentalNpcService` 空壳和 UI 日志按钮。本轮已经采用这个方案。
2. v0.6：做最小真实 actor 实验，但放在单独服务里，并加总开关。失败时不影响任务、对话、tracker。
3. v0.7：如果最小 spawn 稳定，再加入基础模型选择和 Despawn/区域切换清理。
4. v0.8：再考虑 MCDF/Glamourer/Penumbra 外观导入，作为可选能力，不作为插件核心依赖。
5. 始终保留 ImGui 虚拟 NPC overlay 作为降级方案，确保没有真实模型时任务仍然可试玩。
