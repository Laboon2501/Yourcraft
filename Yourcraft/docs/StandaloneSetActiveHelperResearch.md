# Standalone SetActive Helper Research

版本：v11.7

目标：继续追查 Standalone `BgObject.Create(modelPath, poolName, null)` 为什么对象字段、资源、bounds 都正常但仍不可见。本轮只做函数体级取证和只读 list 对比，不新增任何写入按钮。

## 1. 当前结论

Standalone 不可见的最强解释仍然是：它没有真实 `BgPartsLayoutInstance` owner。

`BgObject.Create(null)` 创建的是一个裸 `Graphics.Scene.BgObject`。它可以拥有：

- 有效 vtable
- 有效 `ModelResourceHandle`
- `LoadState=7`
- `visible=true`
- 可读写 `Position / Rotation / Scale`
- 可计算 bounds

但它不在：

- `LayerManager.Instances`
- `LayoutManager.InstancesByType`
- 真实 `BgPartsLayoutInstance->GraphicsObject` owner 槽

因此它没有走完整的 `CreatePrimary -> SetTransformImpl -> SetActive` LayoutInstance 生命周期。

## 2. SetTransformImpl 复查

当前版本 `BgPartsLayoutInstance.SetTransformImpl`：

```text
vtable index 71 = 0x1407417A0
```

关键函数体行为：

```text
graphics = [this + 0x30]
copy transform.Translation -> graphics +0x50
copy transform.Rotation    -> graphics +0x60
copy transform.Scale       -> graphics +0x70
or [graphics +0x38], 2

if ((graphics +0x89 low nibble) == 3):
    call graphics.vfunc[0x38] ; DrawObject.UpdateTransforms(bool)

if ([this +0x38] collider exists):
    update collider / secondary transform
```

结论：

- 它主要同步视觉 transform 和 collider transform。
- 它不插入 LayoutManager / LayerManager 容器。
- 它不明显调用 render submit list / culling list insert。
- Standalone 已经能直接写 Scene.Object transform，所以单独补这个函数不太可能解决不可见。

## 3. SetActive 函数体证据

当前版本 `BgPartsLayoutInstance.SetActive`：

```text
vtable index 63 = 0x1407411C0
WantToBeActive index 55 = 0x14076AE10
```

关键行为：

```text
write [this +0x2B] bit4 from active

graphics = [this +0x30]
if graphics != null:
    write [graphics +0x88] bit0 from active

if previous active state changed and (graphics +0x89 low nibble) == 3:
    call graphics.vfunc[0x38] ; DrawObject.UpdateTransforms(bool)

if active:
    or byte ptr [graphics +0xD6], 0x20

call 0x140456100(graphics)

tail-call this.vfunc[0x128](active) ; SetColliderActive(bool)
```

回答问题：

1. `SetActive(true)` 写 `ILayoutInstance.Flags3`，也就是 `this +0x2B bit4`。
2. 它写 `GraphicsObject +0x88 bit0`，使 graphics active/visible flags 与 LayoutInstance active 同步。
3. 它会调用 helper `0x140456100(graphics)`。
4. 它依赖 `this +0x30` GraphicsObject 和 `this +0x38` Collider，并最终 tail-call `SetColliderActive`。
5. 所以 `SetActive` 本身不能脱离真实 `ILayoutInstance` 安全调用。

## 4. 0x140456100 函数体

函数：

```text
0x140456100
```

反汇编：

```text
140456100  movzx eax, byte ptr [rcx + 0xD7]
140456107  test  al, 1
140456109  jne   140456122
14045610B  or    al, 1
14045610D  mov   rdx, rcx
140456110  mov   byte ptr [rcx + 0xD7], al
140456116  mov   rcx, qword ptr [rip + 0x24A05E3] ; [0x1428F6700]
14045611D  jmp   1402C0BD0
140456122  ret
```

下级函数：

```text
0x1402C0BD0

1402C0BD0  mov       eax, 1
1402C0BD5  lock xadd dword ptr [rcx + 0x4B8], eax
1402C0BDD  cmp       eax, 0x808
1402C0BE2  jae       1402C0BEE
1402C0BE4  mov       eax, eax
1402C0BE6  xchg      qword ptr [rcx + rax*8 + 0x4C0], rdx
1402C0BEE  ret
```

解释：

- `rcx` 是 `GraphicsObject`。
- helper 先检查 `GraphicsObject +0xD7 bit0`。
- 如果该 bit 未置位，则置位并把 `GraphicsObject` 放入全局队列。
- 全局队列指针来自 `[0x1428F6700]`。
- 队列计数在 `queue +0x4B8`。
- 对象槽位从 `queue +0x4C0` 开始，最多约 `0x808` 个。

回答问题：

1. `0x140456100` 不访问 `ILayoutInstance this`。
2. 它不直接访问 LayoutManager / LayerManager。
3. 它不直接插入 `LayerManager.Instances` 或 `LayoutManager.InstancesByType`。
4. 它是 `GraphicsObject` update/active queue enqueue helper，而不是完整 scene attach。
5. 但它依赖调用前状态已经被 `SetActive` 准备好，例如 `graphics +0x88 bit0`、`graphics +0xD6 bit0x20` 和 LayoutInstance active 状态。

结论：`0x140456100` 很可能是进入后续 render/culling/update 处理的必要队列入口之一，但不是完整注册入口。Standalone 没有 LayoutInstance owner 时，即使对象本体可用，也缺少正常 `SetActive` 上下文。

## 5. 只读 active/render list 对比

新增/增强服务：

```text
StandaloneRenderListProbeService
```

新增读取：

- `LayerManager.Instances` / LayoutWorld layer scan
- `LayoutManager.InstancesByType` scan
- `ILayoutInstance +0x2A / +0x2B`
- `BgPartsLayoutInstance +0x30 / +0x38`
- `GraphicsObject +0x88 / +0x89 / +0xD6 / +0xD7`
- `cachedMatrices +0xA0`
- `stainOrBgChangeData +0xA8`
- `cachedTransform +0xB0`
- `animationData +0xB8`
- bounds center/radius

UI 输出：

- list name
- real found
- standalone found
- hit index
- scan count
- truncated
- elapsed ms

当前预期：

- 真实 BgPart 应能在 `LayerManager.Instances` 和 `LayoutManager.InstancesByType` 命中。
- Standalone 不应命中，因为它不是任何 LayoutInstance 的 graphics pointer。
- 真实 BgPart 的 `ILayoutInstance +0x2B bit4` 应反映 active 状态。
- Standalone 没有 `ILayoutInstance`，只能读取 `GraphicsObject` flags。

## 6. cachedMatrices / cachedTransform

字段：

```text
BgObject +0xA0 = cachedMatrices
BgObject +0xA8 = stainOrBgChangeData
BgObject +0xB0 = cachedTransform
BgObject +0xB8 = animationData
```

当前判断：

- 这些字段更像 render/update 之后生成或绑定的缓存结果。
- 真实 BgPart 与 Standalone 在这些字段上有差异，但还不能证明它们是可见前置条件。
- 如果 Standalone 不进入 `SetActive` / update queue / culling processing，这些缓存可能永远不会被正常填充或被 render path 使用。

## 7. 是否存在不依赖 LayoutInstance 的安全注册入口

目前没有找到。

已知：

- `BgObject.Create` 会建立对象和资源，但不足以让模型出现在画面。
- `AddChild / OnAddedToWorld` 方向已经暂停，因为手动调用后 `parentContainsThis` 没改善，并出现过 object flags 异常。
- `0x140456100` 只 enqueue `GraphicsObject`，不是完整注册。
- `SetActive` 需要真实 `ILayoutInstance`，不能拿 Standalone 裸对象直接调用。

因此当前没有可证明安全的 Standalone render-list insert 函数。

## 8. 是否应暂停 Standalone BgObject.Create(null) 路线

建议：暂停把它作为“生成可见物体”的实现路线。

继续保留它作为 Debug 只读取证路线：

- CreateOnly
- Dump
- Validate
- Position 单字段写入
- ComputeSphereBounds
- active/list 对比

如果要继续走“无中生有”，下一步不应继续裸 `BgObject`，而应转向：

1. 找到真正 render/culling manager insert 函数，并证明它不依赖 LayoutInstance。
2. 或研究如何创建/注册真实 `BgPartsLayoutInstance`，让它自然拥有 `LayerManager` / `LayoutManager` owner，并走正常 `CreatePrimary -> SetTransformImpl -> SetActive`。

在这两个条件之一满足前，稳定功能仍应保持 slot-backed copy。

## 9. 安全策略

继续禁止：

- `SetActive`
- `0x140456100`
- `AddChild`
- `OnAddedToWorld`
- parent/child/prev/next 写入
- `CleanupRender`
- Dtor
- LayoutManager insert
- LayerManager insert
- collision
- batch Standalone

本轮没有新增任何写入按钮。
