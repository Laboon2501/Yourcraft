# Yourcraft v1.1 AQuestReborn 真实 NPC 集成路线分析

当前结论：Brio 在本机只暴露了 `ApiVersion` 类 IPC，没有公开可用的 Spawn/Create/Delete Actor IPC。因此 Yourcraft 不能只靠 Brio IPC 生成真实 NPC。AQuestReborn 的做法是直接引用 Brio assembly 和一组相关项目，在插件进程内取得 Brio 服务并调用 `ActorSpawnService.CreateCharacter`。

## 读取文件

- `AQuestReborn.csproj`
- `AQuestReborn/AQuestReborn.cs`
- `CustomNpc/CustomNpcCharacter.cs`
- `InteractiveNpc.cs`
- `CustomNpc/CustomNpcWindow.cs`
- `Windows/NPCEditorWindow.cs`

## 外部依赖

AQuestReborn 的项目文件显示：

- NuGet:
  - `Glamourer.Api`
  - `System.Drawing.Common`
- 本地项目引用:
  - `Brio`
  - `ECommons`
  - `LooseTextureCompilerCore`
  - `MCDF-Loader`
  - `RoleplayingQuestCore`
  - `RoleplayingVoiceCore`

源码中还直接使用：

- Brio: `ActorSpawnService`, `ActorAppearanceCapability`, `PosingCapability`, `Transform`, `PenumbraService`
- AnamCore: emote、武器、头部注视、语音等角色控制
- MCDF-Loader / `McdfDataImporter`: MCDF 外观创建与加载
- Glamourer/Penumbra wrapper: 设计应用、collection 设置、redraw、revert
- Dalamud / FFXIVClientStructs: `ICharacter`, native `Character`, object position/rotation/model id

## 如何创建 NPC/actor

AQuestReborn 初始化时等待 Brio services 可用，然后通过：

- `Brio.Brio.TryGetService<ActorSpawnService>(out _actorSpawnService)`

取得 Brio 的 actor spawn 服务。生成自定义 NPC 时，不走 Brio IPC，而是把请求放进队列：

- `_customNpcActorSpawnQueue`
- `_customNpcPositionSpawnQueue`

每帧/定时处理队列时调用：

- `_actorSpawnService.CreateCharacter(out ICharacter character, SpawnFlags.DefinePosition, true, position, rotation, customName: ...)`

成功后把 `ICharacter` 包装成 `InteractiveNpc`，并写入运行态字典。

## 如何保存 ICharacter / actor 句柄

AQuestReborn 保存多层运行态引用：

- `_customNpcCharacters: Dictionary<string, ICharacter>`
- `_customNpcDictionary: Dictionary<string, InteractiveNpc>`
- `_interactiveNpcDictionary: Dictionary<string, InteractiveNpc>`
- `_hiddenNpcPool: Dictionary<string, (InteractiveNpc Npc, ICharacter Character)>`
- 任务 NPC 另有 `_spawnedNpcsDictionary: Dictionary<string, Dictionary<string, ICharacter>>`

`InteractiveNpc` 内部保存：

- `_character: ICharacter`
- `_cachedObjectIndex`
- `SafeCharacterAddress`，通过 object table 根据缓存 index 重新取 fresh reference，避免直接碰 stale native pointer

这说明 Yourcraft 不能只保存 `npcId -> object index` 就结束。至少要有一个运行态对象负责验证 `ICharacter.IsValid()`、区域切换清理和 actor 生命周期。

## 如何设置 NPC 位置

AQuestReborn 有两层位置控制：

1. 生成时：
   - `ActorSpawnService.CreateCharacter(..., SpawnFlags.DefinePosition, ..., position, rotation)`
2. 运行时：
   - `InteractiveNpc.SetTransform(position, rotation, scale)`

`SetTransform` 会同时操作：

- native `GameObject.SetPosition`
- native `GameObject.SetRotation`
- Brio `PosingCapability.ModelPosing.Transform`

它还处理游泳状态、Y 轴修正、Brio pose capability 缺失时重新获取 capability 等细节。

## 如何应用外观

AQuestReborn 支持多种外观路线：

- Glamourer design:
  - 保存 `NpcGlamourerAppearanceString`
  - `PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex)`
- Penumbra collection:
  - 通过 `Brio.Brio.TryGetService<PenumbraService>` 取 collection
  - `SetCollectionForObject`
  - `RedrawObject`
- MCDF:
  - `AppearanceAccessUtils.AppearanceManager.CreateMCDF`
  - `AppearanceAccessUtils.AppearanceManager.LoadAppearance`
- Monster model:
  - native `ModelContainer.ModelCharaId`
  - Penumbra redraw
- `.chara`/Brio appearance:
  - `BrioAccessUtils.EntityManager.SetSelectedEntity`
  - `TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>`
  - `ImportAppearance`

外观应用不是 spawn 的一部分，而是 spawn 成功后根据配置分支执行，并且存在异步队列和重试逻辑。

## 是否必须依赖 Brio 源码/assembly

是。AQuestReborn 的真实 NPC 生成依赖 Brio assembly 内的 `ActorSpawnService`，不是 Brio IPC。它还使用 Brio 的 capability、transform、Penumbra service 和资源能力。

Yourcraft 如果要复刻真实 NPC，至少需要以下路线之一：

1. **插件依赖 Brio assembly**：把 Brio 作为本地项目/程序集引用，在运行时调用 `Brio.Brio.TryGetService<ActorSpawnService>`。
2. **抽象 Real NPC 后端**：定义 `IRealNpcBackend`，让 `BrioAssemblyNpcBackend` 作为可选实现。没有 Brio 时退回虚拟 NPC overlay。
3. **单独后端插件**：写一个小型 Brio companion plugin 暴露 Yourcraft 需要的 IPC，避免主插件直接依赖 Brio 内部类。

不建议 fork 或复制 AQuestReborn/Brio 的实现到 Yourcraft。AQuestReborn 是 AGPL，Brio 也有自身许可证和内部服务图，直接复制会带来许可证和维护风险。

## Yourcraft 最小复刻需要的类/接口

建议先定义自己的最小接口，不绑定 UI：

```csharp
public interface IRealNpcBackend
{
    bool IsAvailable { get; }
    string StatusText { get; }
    bool TrySpawn(CustomNpc npc, out RealNpcHandle handle, out string reason);
    bool TryDespawn(string npcId, out string reason);
    bool TryDespawnAll(out string reason);
    bool TrySetTransform(string npcId, Vector3 position, Vector3 rotation, Vector3 scale, out string reason);
    bool TryApplyAppearance(CustomNpc npc, out string reason);
}
```

最小运行态对象：

```csharp
public sealed class RealNpcHandle
{
    public string NpcId { get; init; }
    public ushort ObjectIndex { get; init; }
    public nint Address { get; init; }
    public string BackendName { get; init; }
}
```

最小 Brio assembly 后端需要：

- `ActorSpawnService`
- `ICharacter`
- actor 字典：`npcId -> ICharacter/handle`
- 区域切换清理
- `CreateCharacter` 队列和节流
- `DestroyObject` 或隐藏池
- 可选 `PosingCapability` transform
- 可选外观应用服务：
  - `GlamourerDesignAppearanceApplier`
  - `PenumbraCollectionAppearanceApplier`
  - `McdfAppearanceApplier`
  - `GameNpcModelAppearanceApplier`

## 推荐实施顺序

1. 保留当前虚拟 NPC overlay 和 Brio IPC 探测器。
2. 新增 `IRealNpcBackend` 和 `NoopRealNpcBackend`。
3. 单独实验 `BrioAssemblyNpcBackend`，只做 `CreateCharacter` 和 `DestroyObject`，不做外观。
4. 成功后加入 transform 和区域切换清理。
5. 最后才接 Glamourer / Penumbra / MCDF / GameNpc model 外观。

这样 Yourcraft 的任务运行时不会被 Brio/AQuestReborn 的重依赖直接污染，真实 NPC 能作为可选实验后端逐步推进。
