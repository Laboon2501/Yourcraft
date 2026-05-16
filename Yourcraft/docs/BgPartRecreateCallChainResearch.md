# BgPart recreate 调用链取证

日期：2026-05-13

本轮目标是只读取证，寻找 BgPart 图形对象创建/重建链路。没有新增按钮，没有调用 `Init`、`Deinit`、`CreatePrimary`、`DestroyPrimary`、`SetGraphics` 或其它 native recreate 入口。

## 资料来源

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/ILayoutInstance.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Layer/BgPartsLayoutInstance.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/Object.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/DrawObject.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/BgObject.cs`
- `.research/FFXIVClientStructs/ida/data.yml`
- `.research/FFXIVClientStructs/ida/ffxiv_structs.yml`

## 已确认结构

`BgPartsLayoutInstance` 是 `ILayoutInstance` 的派生类型，FFXIVClientStructs 注释为 simple static model with optional collider。

关键字段：

- `BgPartsLayoutInstance.GraphicsObject`：offset `0x30`，类型 `Client::Graphics::Scene::BgObject*`
- `BgPartsLayoutInstance.Collider`：offset `0x38`
- `BgObject.ModelResourceHandle`：offset `0x90`
- `BgObject.CachedTransformMatrices`：offset `0xA0`
- `BgObject.CachedTransform`：offset `0xB0`
- `Graphics.Scene.Object.Position`：offset `0x50`
- `Graphics.Scene.Object.Rotation`：offset `0x60`
- `Graphics.Scene.Object.Scale`：offset `0x70`

这解释了当前已跑通的 VisualOnly transform：直接写 `Graphics.Scene.Object` 的 Position/Rotation/Scale 可以改变视觉对象，而不触碰 layout collision。

## ILayoutInstance primary 生命周期入口

FFXIVClientStructs 暴露的 vfunc：

```csharp
SetGraphics(Graphics.Scene.Object* obj, Transform* transform) // vfunc 25
CreatePrimary(Transform* transform, void* pathOrType)         // vfunc 27
DestroyPrimary()                                              // vfunc 28
```

`ILayoutInstance.CreatePrimary` 的源码注释写明：`arg can be either byte** path or int* type`。IDA 结构数据中，`BgPartsLayoutInstance` 对应签名为：

```text
SetGraphics(this, Client::Graphics::Scene::Object* obj, Client::LayoutEngine::Transform* transform)
CreatePrimary(this, Client::LayoutEngine::Transform* transform, __int64 pathOrType)
DestroyPrimary(this)
```

参数含义推断：

- `transform`：创建 primary graphics 时使用的 layout transform。它不是 render-only transform，走这条链路很可能会影响 layout primary 对象生命周期。
- `pathOrType`：FFXIVClientStructs 注释说明它可能是 `byte** path` 或 `int* type`。对 BgPart 来说，最可能是 primary resource path 指针，或者 layout instance 类型参数。当前没有安全证据说明它能直接传 `ModelResourceHandle.FileName`。
- `SetGraphics.obj`：把一个已创建的 `Graphics.Scene.Object*` 绑定回 layout instance。
- `SetGraphics.transform`：绑定图形对象时同步/应用的 layout transform。

## BgObject.Create

FFXIVClientStructs 暴露：

```csharp
public static partial BgObject* Create(
    CStringPointer modelGamePath,
    CStringPointer poolName,
    BgObject* existingAllocation = null);
```

参数含义：

- `modelGamePath`：模型 game path，例如 `bg/.../*.mdl`。
- `poolName`：分配池名称。当前未找到 BgPart layout 初始化时传入的真实 poolName。
- `existingAllocation`：可选已有 `BgObject*` 分配地址。这个参数说明函数可能支持在指定内存上构造/重建 BgObject，但没有证据表明它能安全用于 live layout-owned BgObject。

风险判断：

- 如果对当前 live `GraphicsObject` 直接使用 `existingAllocation`，可能绕过 layout instance 的生命周期状态、父子图形链、culling/render 注册、stain/cached transform、collision listener 等绑定。
- 如果创建了新 `BgObject*`，还必须通过 `SetGraphics` 或等价路径挂回 `BgPartsLayoutInstance`。这个过程是否会更新 render scene/culling/listener 未确认。

结论：`BgObject.Create(existingAllocation)` 是值得继续取证的入口，但当前不能作为安全实验按钮。

## RenderManager CreateModel

IDA 数据中有：

```text
Client::Graphics::Render::RenderManager
  0x1402B8D20: CreateModel
```

旧版本 IDA 名称中也出现过 `Client::Graphics::Render::RenderManager_CreateModel`。这属于更底层 render model 创建入口。

当前没有 FFXIVClientStructs 直接托管绑定可说明它的完整参数列表。结合 `BgObject.SetModel` 实验结果看，`SetModel` 能更新 `ModelResourceHandle/FileName`，但当前可见 mesh 没有重建，说明 render model/cached mesh 绑定很可能不是由 `SetModel` 自动替换。

结论：`RenderManager.CreateModel` 可能在 `BgObject.Create` 或 `CreatePrimary` 内部被调用，但直接调用缺少安全参数、owner、scene/culling 注册上下文。

## SetupModelAttributes

IDA 数据中：

```text
#oldfail 0x1404A15D0: SetupModelAttributes  # wrist, fingers, tail all got inlined here
```

它位于 `Client::Graphics::Scene::Human` / `CharacterBase` 相关区域，附近还有：

- `SetupHelmetModelAttributes`
- `SetupTopModelAttributes`
- `SetupHandModelAttributes`
- `SetupLegModelAttributes`

判断：这是角色装备/人体模型属性链路，不是 BgPart 静态模型 recreate 链路。对 `BgObject` / `BgPartsLayoutInstance` 当前目标没有直接价值。

## SetModel 与 recreate 的关系

当前实验结论：

- `BgObject.SetModel(ResourceCategory*, path)` 返回 true。
- `ModelResourceHandle.FileName` 可变为目标 mdl。
- `LoadState=7`。
- 可见 mesh 不变化。
- `UpdateMaterials`、`UpdateRender`、`UpdateTransforms(true)`、`NotifyTransformChanged` 不足以刷新 visible mesh。
- `CleanupRender` 会导致模型消失，并使后续 transform/RestoreAll 不安全，已禁用。

因此推断：`SetModel` 只切换或加载 resource handle，不负责销毁旧 render mesh 并重建当前 BgObject 的可见 render data。真正刷新可能需要走 layout 初始化时的 `CreatePrimary` / `BgObject.Create` / render model create 链路。

## 问题判断

### CreatePrimary 是否读取当前 ModelResourceHandle？

没有证据支持。

`CreatePrimary` 的签名是：

```text
CreatePrimary(Transform* transform, void* pathOrType)
```

它没有 `ModelResourceHandle` 参数。FFXIVClientStructs 注释说明第二参数可能是 path 或 type，而不是当前 `BgObject.ModelResourceHandle`。更合理的判断是：它按 layout primary path / 传入 pathOrType 创建 primary graphics，而不是读取当前已经被 `SetModel` 改过的 handle。

### DestroyPrimary 后能否 CreatePrimary 恢复？

理论上可能重建 primary graphics，但当前不可认为安全。

原因：

- `DestroyPrimary` 会操作 layout instance 的 primary graphics 生命周期，可能释放或解除 `GraphicsObject`。
- 需要知道 `CreatePrimary` 的 `pathOrType` 应传 `byte** path` 还是 `int* type`，以及该指针生命周期。
- 需要知道 destroy 后 `Collider`、`CollisionUpdateListener`、layer maps、culling/render scene 是否仍保持一致。
- 已有 `CleanupRender` 经验说明 destroy/cleanup 类路径会让 live instance 进入 transform 写入不安全状态。

结论：只能继续做只读 xref/反汇编级取证，暂不调用。

### SetGraphics 是否只在初始化阶段用？

高度可能。

`SetGraphics(Graphics.Scene.Object* obj, Transform* transform)` 是 `ILayoutInstance` 的 primary graphics 绑定入口，vfunc 位置紧贴 `GetGraphics`、`CreatePrimary`、`DestroyPrimary`。它看起来负责把 graphics object 指针写回 layout instance，并同步 transform。

没有证据表明它能安全用于 live object 热替换。若传入未完整注册的 `BgObject*`，可能造成 render tree/culling/layer ownership 不一致。

### BgObject.Create(existingAllocation) 是否能原地重建？

不能确认。

签名确实提供 `existingAllocation = null`，所以函数设计上存在“在已有分配上创建”的可能。但要让它成为安全 reload 入口，还缺少至少这些条件：

- 真实 `poolName`。
- live `BgObject` 进入可重建状态的前置清理步骤。
- 旧 render/cached mesh 的释放方式。
- 创建后是否必须调用 `SetGraphics`。
- 创建后是否必须 `OnAddedToWorld`、`UpdateRender`、culling 注册、bounds 计算。

当前不能直接用于 Yourcraft 的单实例模型替换。

## 推测调用链

基于公开签名和结构关系，BgPart 初始加载可能类似：

```text
LayerManager / LayoutManager 读取 layout data
  -> 创建 BgPartsLayoutInstance
    -> Init(creator, primaryPath)
      -> SetProperties(FileLayerGroupInstance*)
      -> CreatePrimary(transform, pathOrType)
        -> BgObject.Create(modelGamePath, poolName, existingAllocation?)
          -> RenderManager.CreateModel(...)
          -> BgObject.ResetFlags()
          -> BgObject.LoadAnimationData(modelResourcePath)
        -> SetGraphics(bgObject, transform)
      -> CreateSecondary() / collider load, 如果存在 collision
```

这条链路是推测，不是已验证完整 xref。当前可确认的是这些函数/字段存在，且签名关系符合这个方向。

## 当前最小安全结论

1. VisualOnly transform 应继续使用 `Graphics.Scene.Object.Position/Rotation/Scale`，这是当前已验证稳定路径。
2. `SetModel` 不应进入正式流程，因为它只改 handle/path，不刷新 visible mesh。
3. `CleanupRender` 必须继续禁用。
4. `CreatePrimary` / `DestroyPrimary` / `SetGraphics` 是 recreate 方向的核心入口，但本轮没有足够证据允许调用。
5. `BgObject.Create(existingAllocation)` 可能是原地重建入口的一部分，但必须先找到真实 `poolName`、调用前置状态和后置注册流程。
6. `SetupModelAttributes` 属于角色模型属性链路，不是 BgPart recreate 的主要方向。

## 下一步建议

只读层面继续做：

- 在 IDA/xref 中追 `BgPartsLayoutInstance.CreatePrimary` 的实现体，确认它如何解析 `pathOrType`。
- 追 `BgObject.Create` 对 `poolName` 的使用和返回对象初始化过程。
- 追 `SetGraphics` 是否仅写 `GraphicsObject`，还是还做 render tree/culling 注册。
- 查 `DestroyPrimary` 是否会 null 掉 `GraphicsObject`、释放 render/collider/listener。
- 找到 layout load 时对 BgPart 的真实 callsite，记录 `primaryPath`、`pathOrType`、`poolName` 的来源。

在完成这些之前，不应新增 recreate 写入按钮。
