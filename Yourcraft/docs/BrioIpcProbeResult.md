# Yourcraft Brio IPC 探测结果

本轮加入 `BrioIpcProbeService`，目标是停止猜测 `Brio.Actor.SpawnExAsync`，改为先探测 Brio 当前实际注册了哪些 IPC。探测器不会执行 Spawn，只会创建 subscriber 并检查是否已注册；所有探测都有 `try/catch`，失败只写日志和 UI。

## 探测列表

当前探测以下 IPC 名称：

- `Brio.ApiVersion`
- `Brio.Version`
- `Brio.Actor.Spawn`
- `Brio.Actor.SpawnAsync`
- `Brio.Actor.Create`
- `Brio.Actor.CreateAsync`
- `Brio.Actor.Delete`
- `Brio.Actor.Despawn`
- `Brio.Actor.ApplyAppearance`
- `Brio.Actor.SetAppearance`
- `Brio.Scene.SpawnActor`
- `Brio.Scene.DeleteActor`
- `Brio.GPose.SpawnActor`

## UI 行为

实验 NPC 页签新增“探测 Brio IPC”按钮，并显示：

- IPC 名称
- 尝试绑定的签名
- 是否已注册
- 调用/绑定错误信息

如果没有探测到可用 Spawn IPC，生成按钮会显示：

> Brio 当前未暴露可用 Spawn IPC，需要走插件依赖/引用 Brio assembly 或参考 AQuestReborn 内部实现。

## Spawn 调用策略

`BrioNpcBridgeService` 不再持有硬编码的 `Brio.Actor.SpawnExAsync` subscriber。现在只会在 `BrioIpcProbeService.SelectedSpawnIpc` 存在时调用。

当前可调用的 Spawn 形态只包括：

- 无参数同步：`IGameObject?`
- 无参数异步：`Task<IGameObject?>`

带参数或签名未知的 IPC 只做探测显示，不会盲目调用。

## 当前结论

之前的错误：

```text
IPC method Brio.Actor.SpawnExAsync was not registered yet
```

说明当前 Brio 版本没有注册旧版公开 API 文件里的 `Brio.Actor.SpawnExAsync`。因此 Yourcraft 现在不再直接调用它。需要在游戏内点击“探测 Brio IPC”，把 UI 中已注册的 IPC 列表作为下一步依据。
