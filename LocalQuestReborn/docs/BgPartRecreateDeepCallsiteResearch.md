# BgPart recreate 三入口深度取证

日期：2026-05-13

本轮继续只读取证，目标是收敛 `CreatePrimary`、`BgObject.Create`、`SetGraphics` 三个入口的参数来源和可能调用顺序。没有新增 UI 写入按钮，没有调用 `Init`、`Deinit`、`CreatePrimary`、`DestroyPrimary`、`SetGraphics`、`BgObject.Create` 或 `CleanupRender`。

## 资料来源

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/ILayoutInstance.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/FileFormat.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/LayoutManager.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/LayoutWorld.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Layer/BgPartsLayoutInstance.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/Object.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/DrawObject.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/BgObject.cs`
- `.research/FFXIVClientStructs/ida/data.yml`
- `.research/FFXIVClientStructs/ida/ffxiv_structs.yml`

限制：当前本地资料没有函数体反编译或完整 xref 图，只能基于生成绑定、vtable、结构字段、IDA 名称和已有实验结果判断。下文会区分“已确认”和“推断”。

## 入口一：BgPartsLayoutInstance.CreatePrimary

### 已确认签名

FFXIVClientStructs 暴露在 `ILayoutInstance`：

```csharp
// arg can be either byte** path or int* type
CreatePrimary(Transform* transform, void* pathOrType) // vfunc 27
```

`ffxiv_structs.yml` 中 `BgPartsLayoutInstance` 对应 vfunc：

```text
CreatePrimary(
  Client::LayoutEngine::Layer::BgPartsLayoutInstance* this,
  Client::LayoutEngine::Transform* transform,
  __int64 pathOrType
) -> void
```

邻近 vfunc：

```text
GetGraphics   offset 184
GetGraphics2  offset 192
SetGraphics   offset 200
CreatePrimary offset 216
DestroyPrimary offset 224
CreateSecondary offset 256
DestroySecondary offset 264
```

这说明 `CreatePrimary` 位于 layout instance 的 primary graphics 生命周期区域。

### pathOrType 是 byte** path 还是 int* type？

已确认：接口注释写明第二参数可能是 `byte** path` 或 `int* type`，但本地资料没有函数体能证明 `BgPartsLayoutInstance` 分支具体是哪一个。

强推断：对 BgPart 来说，`pathOrType` 更可能走 `byte** path`，并指向 mdl path。

依据：

- `FileLayerGroupInstanceBgPart` 有：
  - `OffsetPathMdl`
  - `OffsetPathPcb`
  - `PathMdl`
  - `PathPcb`
- BgPart 是静态模型实例，primary graphics 对应 mdl，secondary/collider 对应 pcb 或 analytic collider。
- `CreatePrimary` 的 path/type 双态参数在 BgPart 场景下如果不用 path，就无法解释 mdl 路径如何进入 `BgObject.Create(modelGamePath, poolName, ...)`。

仍未确认：

- `pathOrType` 是 `byte**` 指向 path 指针，还是直接传 `byte*` 后由实现 reinterpret。
- `pathOrType` 是否也可能是 `GetPrimaryPath()` 返回值的地址。
- `CreatePrimary` 是否会在缺省情况下自己调用 `GetPrimaryPath()`，只有特定类型才使用 `int* type`。

### primaryPath 来源

已确认结构：

```csharp
ILayoutInstance.Init(void* creator, byte* primaryPath)
ILayoutInstance.GetPrimaryPath() -> CStringPointer
```

`FileLayerGroupInstanceBgPart` 的文件数据来源：

```csharp
FileLayerGroupInstanceBgPart.OffsetPathMdl
FileLayerGroupInstanceBgPart.PathMdl
```

推断链：

```text
LVB / layer group 文件
  -> FileLayerGroupInstanceBgPart.PathMdl
  -> ILayoutInstance.Init(creator, primaryPath)
  -> ILayoutInstance.GetPrimaryPath()
  -> CreatePrimary(transform, pathOrType)
```

是否完全按这条链传递还未由函数体确认，但 `Init(primaryPath)`、`GetPrimaryPath()`、`FileLayerGroupInstanceBgPart.PathMdl` 三者共同支持这个方向。

### transform 来源

已确认文件结构：

```csharp
FileLayerGroupInstance.Transform
FileLayerGroupTransform {
  Vector3 Translation;
  Vector3 Rotation; // euler angles
  Vector3 Scale;
}
```

运行时结构：

```csharp
ILayoutInstance.Transform {
  Vector3 Translation;
  Quaternion Rotation;
  Vector3 Scale;
}
```

推断链：

```text
FileLayerGroupInstance.Transform
  -> SetProperties(FileLayerGroupInstance* data)
  -> runtime ILayoutInstance Transform
  -> CreatePrimary(transform, pathOrType)
  -> SetGraphics(obj, transform)
```

这也符合当前 FullLayoutWithCollision 实验：写 LayoutInstance transform 会同时影响可见模型和碰撞。

### 是否调用 BgObject.Create？

没有函数体 xref 直接确认。

强推断：`BgPartsLayoutInstance.CreatePrimary` 很可能调用 `BgObject.Create`。

依据：

- `BgPartsLayoutInstance.GraphicsObject` 类型是 `BgObject*`。
- `BgObject.Create(modelGamePath, poolName, existingAllocation)` 是唯一暴露的静态 BgObject 创建入口。
- `CreatePrimary` 的职责是创建 primary graphics，BgPart primary graphics 正是 `BgObject`。

但不能确定：

- 是否直接调用 `BgObject.Create`，还是通过 layout creator / object pool / factory 间接调用。
- 是否传入 `existingAllocation`。
- 是否立即调用 `SetGraphics`，还是由 `BgObject.Create` 内部关联。

## 入口二：BgObject.Create

### 已确认签名

FFXIVClientStructs：

```csharp
public static partial BgObject* Create(
  CStringPointer modelGamePath,
  CStringPointer poolName,
  BgObject* existingAllocation = null);
```

`ffxiv_structs.yml`：

```text
Create(
  char* modelGamePath,
  char* poolName,
  Client::Graphics::Scene::BgObject* existingAllocation
) -> Client::Graphics::Scene::BgObject*
```

IDA 名称：

```text
Client::Graphics::Scene::BGObject:
  ctor
  Create
  LoadAnimationData
  ResetFlags
  SetModel
```

### poolName 来源

未确认。

可见线索：

- `BgObject.Create` 需要 `poolName`。
- `LayoutManager` 注释中有：
  - `0x320: instance pools`
  - `0xB90: gfx bg object pool ptr`
- `CreatePrimary` 第一个参数之外没有显式 poolName 参数，因此 poolName 可能来自：
  - layout manager / creator 参数
  - instance pool 名称
  - scene/object factory 内部常量
  - `BgObject.Create` callsite 附近的固定字符串

当前本地资料没有 callsite 字符串或函数体，因此不能确定真实 poolName。后续需要 IDA xref 到 `BgObject.Create` 的 callsite 查字符串或寄存器来源。

### existingAllocation 用途

已确认：签名提供 `existingAllocation = null`。

可判断：

- 如果为 null，函数会分配/创建新的 `BgObject`。
- 如果非 null，可能在既有分配上构造或重置对象。

未确认：

- 是否允许对 live layout-owned `GraphicsObject` 使用。
- 是否要求先调用 `DestroyPrimary` 或其它析构路径。
- 是否会释放旧 render model、cached transform、stain buffer、animation data。

风险：当前 `CleanupRender` 已验证会让对象进入不安全状态，说明直接触碰 render 生命周期很容易破坏后续 VisualOnly transform。`existingAllocation` 即使存在，也不能直接等价为“安全原地重建”。

### 是否调用 RenderManager.CreateModel？

没有函数体 xref 直接确认。

推断：`BgObject.Create` 或其内部 `SetModel` / render 初始化链最终应调用 `RenderManager.CreateModel` 或等价 render model factory。

依据：

- IDA 中存在 `Client::Graphics::Render::RenderManager.CreateModel`。
- `BgObject` 最终必须拥有可见 render mesh。
- `SetModel` 实验只更新 `ModelResourceHandle/FileName`，没有刷新可见 mesh，说明 render mesh 创建发生在更深或更早的创建链。

但当前不能确认 `BgObject.Create` 是直接调用 `RenderManager.CreateModel`，还是通过 model resource load callback / scene graph 初始化间接创建。

### 是否注册 culling/render scene？

未确认。

`DrawObject` 暴露：

- `UpdateCulling`
- `UpdateTransforms`
- `UpdateMaterials`
- `ComputeSphereBounds`
- `UpdateRender`
- `NotifyTransformChanged`

`Object` 暴露：

- `AddChild`
- `OnAddedToWorld`
- `CleanupRender`

这些说明 render tree / culling 注册是独立于资源 handle 的生命周期步骤。当前没有证据说明 `BgObject.Create` 是否完整执行这些步骤，或者必须在 `SetGraphics` / `OnAddedToWorld` 后才完成。

## 入口三：SetGraphics

### 已确认签名

```text
SetGraphics(
  Client::LayoutEngine::Layer::BgPartsLayoutInstance* this,
  Client::Graphics::Scene::Object* obj,
  Client::LayoutEngine::Transform* transform
) -> void
```

`BgPartsLayoutInstance` 有明确字段：

```csharp
GraphicsObject // offset 0x30, BgObject*
Collider       // offset 0x38
```

### 是否仅写 GraphicsObject？

不能确认。

最小可能行为：

```text
this->GraphicsObject = (BgObject*)obj
apply transform to obj
```

但从签名看它也可能做更多：

- 同步 `Graphics.Scene.Object.Position/Rotation/Scale`
- 更新 layout instance active/primary 状态
- 关联 layer/layout manager
- 触发 bounds/culling 更新
- 将 graphics object 接入 parent/child scene object 链

因为 `SetGraphics` 接收 transform，而不是只接收 object pointer，所以它很可能不只是简单赋值。

### 是否同步 transform？

高度可能。

依据：

- 参数包含 `Transform* transform`。
- `Graphics.Scene.Object` 本身有 Position/Rotation/Scale。
- `CreatePrimary` 同样接收 `Transform*`。

推断：`SetGraphics` 至少会把 layout transform 应用给 graphics object，或者保证 graphics object 与 layout instance transform 同步。

### 是否注册 render/culling？

未确认。

`SetGraphics` 的位置在 primary lifecycle vfunc 区域，可能承担“绑定”职责，但没有函数体证据说明它会注册 render/culling。注册也可能发生在：

- `BgObject.Create`
- `Object.OnAddedToWorld`
- `DrawObject.UpdateRender`
- layout manager/layer manager 后续 update 阶段

### 是否只在初始化阶段调用？

强推断是初始化/生命周期阶段用。

依据：

- 位于 `GetGraphics`、`CreatePrimary`、`DestroyPrimary` 附近。
- 它是 vfunc，不是普通公开的热更新 API。
- 名称是 `SetGraphics`，不是 `ReloadGraphics` 或 `RefreshGraphics`。
- 当前 Layout object 创建链里 primary/secondary 的概念与初始化/销毁匹配。

结论：它不应作为 live hot-swap 按钮调用，除非先确认 `DestroyPrimary/CreatePrimary/SetGraphics` 的完整顺序、引用所有权和恢复条件。

## 真实 layout load callsite：当前可确认与缺口

### 可确认的数据来源

文件实例：

```text
FileLayerGroupInstanceBgPart
  Type
  Key
  Name
  Transform
  OffsetPathMdl / PathMdl
  OffsetPathPcb / PathPcb
  ColliderType
```

运行时管理：

```text
LayoutManager.Layers
LayoutManager.InstancesByType
LayoutManager.ResourcePaths
LayerManager.Instances
ILayoutInstance.Init(creator, primaryPath)
ILayoutInstance.SetProperties(FileLayerGroupInstance* data)
```

### 推断调用顺序

```text
Layer/LVB load
  -> 解析 FileLayerGroupInstanceBgPart
    -> 分配 BgPartsLayoutInstance
      -> Init(creator, primaryPath = PathMdl?)
      -> SetLayer(layer)
      -> SetProperties(FileLayerGroupInstanceBgPart*)
        -> 写入/转换 Transform
        -> 保存 collider/material 信息
      -> CreatePrimary(runtimeTransform, pathOrType = &PathMdl? 或 &primaryPath?)
        -> BgObject.Create(modelGamePath = PathMdl, poolName = layout/scene pool, existingAllocation = null?)
          -> SetModel/LoadAnimationData/ResetFlags/RenderModel create
        -> SetGraphics(bgObject, runtimeTransform)
      -> 如果 ColliderType != None:
        -> CreateSecondary()
```

### 未找到的关键 callsite

当前本地资料没有找到：

- `BgPartsLayoutInstance.CreatePrimary` 函数体。
- `BgObject.Create` 的 caller xref。
- `SetGraphics` 函数体。
- layout load 中实际传给 `CreatePrimary` 的 `pathOrType`。
- layout load 中实际传给 `BgObject.Create` 的 `poolName`。
- `BgObject.Create` 是否直接调用 `RenderManager.CreateModel`。

需要 IDA/Ghidra 函数体或更完整 xref 数据才能继续确认。

## 对当前 SetModel 问题的解释

当前 SetModel 实验：

- `SetModel` 返回 true。
- `ModelResourceHandle.FileName` 变成 target mdl。
- `LoadState=7`。
- visible mesh 不变。
- `UpdateMaterials`、`UpdateRender`、`UpdateTransforms(true)`、`NotifyTransformChanged` 不足以改变 mesh。
- `CleanupRender` 会使模型消失并破坏 transform 写入安全。

结合本轮三入口推断：

`SetModel` 很可能只负责 resource handle，而可见 mesh 的创建/绑定在 `CreatePrimary -> BgObject.Create -> render model create -> SetGraphics` 这一初始化链中完成。热替换 handle 后，旧 render mesh 仍在当前 BgObject 的 render/cached data 中，所以画面不变。

## 当前安全判断

### CreatePrimary

不能调用。

原因：

- `pathOrType` 对 BgPart 的真实 ABI 未确认。
- 不知道是否需要先 `DestroyPrimary`。
- 不知道是否会重建 collider 或影响 `CollisionUpdateListener`。
- 不知道是否会改变 `LayerManager.Instances` / `LayoutManager.InstancesByType` 中的状态。

### BgObject.Create

不能调用。

原因：

- `poolName` 未确认。
- `existingAllocation` 是否可用于 live object 未确认。
- 创建后如何绑定回 layout instance 未确认。
- render/culling scene 注册顺序未确认。

### SetGraphics

不能调用。

原因：

- 可能是初始化阶段绑定入口。
- 是否只赋值还是做 render/culling 注册未确认。
- 若传入未正确创建或未注册的 object，可能破坏 layout/render ownership。

## 后续最小取证路线

只读继续：

1. 用 IDA/Ghidra 打开 `BgPartsLayoutInstance.CreatePrimary` vfunc 目标，确认：
   - `pathOrType` 解引用方式。
   - 是否调用 `BgObject.Create`。
   - 是否调用 `SetGraphics`。
2. 对 `BgObject.Create` 做 xref：
   - 找到来自 `BgPartsLayoutInstance.CreatePrimary` 的 callsite。
   - 记录 `poolName` 来源。
   - 记录 `existingAllocation` 传 null 还是 this->GraphicsObject。
3. 对 `SetGraphics` 函数体做字段写入扫描：
   - 是否写 offset `0x30 GraphicsObject`。
   - 是否写 `Graphics.Scene.Object.Position/Rotation/Scale`。
   - 是否调用 `OnAddedToWorld` / `UpdateRender` / culling 相关入口。
4. 找 layout load 中 `Init(primaryPath)` 的 caller：
   - 确认 primaryPath 是否来自 `FileLayerGroupInstanceBgPart.PathMdl`。
   - 确认 `SetProperties` 与 `CreatePrimary` 的顺序。

在以上完成前，LocalQuestReborn 不应新增 recreate 写入按钮，也不应恢复 CleanupRender。
