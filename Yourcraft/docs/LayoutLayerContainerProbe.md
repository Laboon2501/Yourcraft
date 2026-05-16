# Layout Layer Container Probe

## 取证结论

本轮目标是解释为什么 `LayoutInstance` 复制不能直接做，以及下一步应该怎样安全继续。

当前 FFXIVClientStructs 暴露的 `LayerManager` 只提供 `Instances` 可枚举容器。Meddle 的 Layout 页也是通过：

```csharp
foreach (var (_, instancePtr) in layerManager->Instances)
```

读取现有 instance。没有发现公开的 `AddInstance`、`CreateInstance`、`Insert` 或 `Allocate` 入口。

`ILayoutInstance` 自身暴露了一些实例级方法：

- `Init(void*, byte*)`
- `Deinit`
- `SetProperties(FileLayerGroupInstance*)`
- `SetLayer(LayerManager*)`
- `GetSizeOf()`
- `CreatePrimary(Transform*, void*)`
- `DestroyPrimary()`
- `SetTransform(Transform*)`

这些方法说明单个 instance 有初始化、绑定 layer、创建 primary graphics 的流程，但缺少“由 layer/layout resource 分配并插入容器”的安全入口。

## 当前 layer instance 容器结构

UI 中新增的 `LayerDumpService` 会对选中 source instance 的 `LayerAddress` 做只读 dump：

- `layerAddress`
- `layerId`
- `instanceCount`
- first / last instance pointer
- 当前 source instance 在枚举结果中的 index
- 前后相邻 instance 地址
- 当前 UI 已知的同 layer instance 列表

容器底层的 first/last/capacity 没有通过 FFXIVClientStructs 公开。当前只能确认它是 `LayerManager.Instances` 暴露的 native collection wrapper，可枚举，但不能安全插入。

## 现有 instance 是怎么被 layer 持有的

从 Meddle 和 FFXIVClientStructs 可见路径看：

1. `LayoutWorld.Instance()` 拿到当前世界布局。
2. `LayoutWorld.LoadedLayouts` / `GlobalLayout` 提供 `LayoutManager*`。
3. `LayoutManager.Layers` 提供 `LayerManager*`。
4. `LayerManager.Instances` 提供 `ILayoutInstance*` 列表。
5. 具体类型如 `BgPartsLayoutInstance` 再通过 `GetPrimaryPath()` / `GraphicsObject` 读资源和可见状态。

这是一条加载完成后的只读/编辑路径，不是创建路径。

## 是否能安全插入新 instance

目前不能。

原因：

- 没有公开 Add/Insert/Allocate。
- `ILayoutInstance.Init` 需要的参数不是一个完整的安全构造 API。
- `SetProperties(FileLayerGroupInstance*)` 需要原始 layout resource 内的 instance 数据，不是单独的 mdl path。
- `CreatePrimary` 可能只创建 graphics primary，不负责把 instance 注册回 layer 容器。
- 手写容器 first/last/capacity 或 memcpy instance 都属于高风险 native 操作，本轮明确禁止。

## 需要调用哪个 init/refresh

还没有找到足够安全的完整链路。可能的链路需要继续逆向：

1. layout resource 解析出 `FileLayerGroupInstance`
2. 分配具体 `BgPartsLayoutInstance`
3. `Init`
4. `SetLayer`
5. `SetProperties`
6. 插入 `LayerManager.Instances`
7. `CreatePrimary`
8. layer/layout update 或 resource callback

目前只确认 `SetTransform` 对已有 instance 生效。

## 备用路线：复用已有 instance

新增 UI 实验：

- 查找附近候选可复用 BgPart
- 将候选 BgPart 改成选中 BgPart 的资源/transform
- 恢复候选原始数据

安全边界：

- 会保存候选原始 transform。
- 如果候选和源的 resourcePath 相同，可以把候选移动到玩家当前位置来验证“占用已有 slot”的效果。
- 如果 resourcePath 不同，目前不会写 resourcePath，因为没有安全公开入口。
- 不写 layer 容器指针，不 memcpy，不批量操作。

下一步如果要真正替换资源，需要找到 `SetProperties(FileLayerGroupInstance*)` 所需数据来源，或找到 LayoutResource 内部创建/加载 instance 的真实函数。
