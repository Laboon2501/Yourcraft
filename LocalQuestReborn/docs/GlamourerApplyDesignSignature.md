# Glamourer ApplyDesign Signature

## 取证结论

AQuestReborn 没有直接调用 raw IPC 名称字符串，而是通过 `Glamourer.Api.IpcSubscribers.ApplyDesign` wrapper 调用 Glamourer。

关键代码：

- `.research/AQuestRebornFull/AQuestReborn/PenumbraAndGlamourerHelpers/PenumbraAndGlamourerIpcWrapper.cs:25`
  - `public ApplyDesign ApplyDesign { get => _applyDesign; set => _applyDesign = value; }`
- `.research/AQuestRebornFull/AQuestReborn/PenumbraAndGlamourerHelpers/PenumbraAndGlamourerIpcWrapper.cs:82`
  - `_applyDesign = new ApplyDesign(dalamudPluginInterface);`
- `.research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1514`
  - `PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex);`
- `.research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:1640`
  - `PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex);`
- `.research/AQuestRebornFull/AQuestReborn/AQuestReborn.cs:2764`
  - `PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(design.Key, customNpc.Character.ObjectIndex);`

## 真实调用入口

```csharp
new Glamourer.Api.IpcSubscribers.ApplyDesign(pluginInterface)
    .Invoke(Guid designId, int objectIndex)
```

参数：

- `Guid designId`：Glamourer design GUID。
- `int objectIndex`：目标 actor 的 ObjectIndex。

返回类型：

- 由当前 Glamourer.Api assembly 决定，通常是 error code enum 或 bool。
- LocalQuestReborn 通过反射读取 wrapper 的 `Invoke` 返回类型，并在 UI 的“Glamourer IPC 详情”中显示。

## LocalQuestReborn 修改

`GlamourerIpcBridgeService` 现在优先探测并使用：

```text
Glamourer.Api.IpcSubscribers.ApplyDesign.Invoke(Guid, int)
```

只有 wrapper 不存在时，才保留旧的 raw IPC 名称探测结果作为 fallback。这样不再优先猜 `Glamourer.ApplyDesign` 的 2/4 参数 raw call gate。

失败时 UI/Actor 结果会显示：

- 绑定签名
- 调用参数
- 返回结果
- 异常信息
