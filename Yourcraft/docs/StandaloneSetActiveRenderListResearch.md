# Standalone SetActive / Render List Research

版本：v11.6

目标：继续解释为什么 `BgObject.Create(modelPath, poolName, null)` 返回的 Standalone `BgObject` 拥有有效对象、资源、位置和 bounds，但仍不进入游戏画面可见渲染链。

本轮结论先写在前面：

- `BgObject.Create(null allocation)` 能创建一个字段和资源都基本完整的 `Graphics.Scene.BgObject`。
- `Position` 单字段写入和 `ComputeSphereBounds` 能工作，说明对象本体不是完全无效。
- 真实 `BgPart` 的可见链路在 `CreatePrimary -> SetTransformImpl -> SetActive` 之后仍依赖 `ILayoutInstance` 状态。
- Standalone 没有 `BgPartsLayoutInstance` owner，因此缺少 Layout active/culling/render registration 链。
- 本轮没有新增任何写入按钮；`AddChild` / `OnAddedToWorld` 继续暂停。

## 1. SetTransformImpl 函数体结论

当前版本 `BgPartsLayoutInstance.SetTransformImpl` vfunc 目标：

```text
BgPartsLayoutInstance vtable index 71 = 0x1407417A0
```

关键行为：

```text
mov rcx, [this + 0x30]          ; GraphicsObject
copy transform.Position -> Graphics.Scene.Object +0x50
copy transform.Rotation -> Graphics.Scene.Object +0x60
copy transform.Scale    -> Graphics.Scene.Object +0x70
or  qword ptr [GraphicsObject + 0x38], 2

if (GraphicsObject outline/load low nibble == 3):
    call GraphicsObject.vfunc[0x38]    ; DrawObject.UpdateTransforms(bool)

if ([this + 0x38] collider exists):
    update collider / secondary transform path
```

解释：

- `SetTransformImpl` 的视觉部分基本等价于我们已经验证过的 `Graphics.Scene.Object Position / Rotation / Scale` 写入。
- 它没有明显执行 `AddChild`、`OnAddedToWorld` 或 LayoutWorld/Layer 容器插入。
- 它会更新 `this + 0x38` 的 collider/secondary，因此它依赖真实 `BgPartsLayoutInstance`。
- 对 Standalone 来说，单独补做这一步不能解决不可见，因为 Standalone 的 transform/bounds 已经能读写。

结论：`SetTransformImpl` 不是 Standalone 缺失的主要 render registration 步骤。

## 2. SetActive 函数体结论

当前版本 `BgPartsLayoutInstance.SetActive` vfunc 目标：

```text
BgPartsLayoutInstance vtable index 63 = 0x1407411C0
WantToBeActive vtable index 55 = 0x14076AE10
```

`SetActive(bool active)` 关键行为：

```text
write [this + 0x2B] bit 4 from active

graphics = [this + 0x30]
if graphics != null:
    write [graphics + 0x88] bit 0 from active

if active state changed and graphics outline/load low nibble == 3:
    call graphics.vfunc[0x38]          ; DrawObject.UpdateTransforms(bool)

if [this + 0x2B] bit 4 set:
    or byte ptr [graphics + 0xD6], 0x20

call 0x140456100(graphics)             ; unknown helper

tail-call this.vfunc[0x128](active)    ; SetColliderActive(bool)
```

`WantToBeActive` 极小：

```text
movzx eax, byte ptr [this + 0x2B]
shr al, 5
and al, 1
ret
```

解释：

- `SetActive` 会同步 LayoutInstance active flag 与 `GraphicsObject` flags。
- 它调用了未知 helper `0x140456100(graphics)`，这是本轮发现的候选 render/culling refresh 点。
- 它最后 tail-call `SetColliderActive(bool)`，因此明显假设 `this` 是真实 `ILayoutInstance`。
- Standalone 没有合法 `this + 0x2A/0x2B/0x30/0x38` LayoutInstance 字段，不能安全直接调用 `SetActive`。

结论：真实 BgPart 可见很可能不只是 `BgObject` 自己的字段，而是通过 LayoutInstance `SetActive` 把 `GraphicsObject` 置为 active 并刷新/注册到某个渲染或 culling 路径。

## 3. Active / Render / Culling List 对比

新增只读服务：

```text
StandaloneRenderListProbeService
```

UI 按钮：

```text
Standalone active/render list 对比（只读）
```

它只做以下读取：

- 扫描 `LayoutWorld.Instance()->LoadedLayouts` 和 `GlobalLayout`。
- 遍历 Layer `Instances`。
- 只检查 `InstanceType.BgPart`。
- 对比：
  - 当前真实 BgPart layout address 是否命中。
  - 真实 BgPart `GraphicsObject` 是否命中。
  - Standalone object address 是否作为某个 BgPart `GraphicsObject` 命中。
- 有限时限的 parent child ring scan：
  - `parentContainsThis`
  - `hitIndex`
  - `scanCount`
  - `truncated`
  - `endedByNull / endedByCycle / endedByLimit`

当前预期结果：

- 真实 BgPart 会命中 LayoutWorld/Layer/BgPart instance scan。
- Standalone 不会命中，因为它不是任何 `BgPartsLayoutInstance->GraphicsObject`。
- 真实 BgPart 的 parent child ring 应包含自身。
- Standalone 可能有 parent/prev/next 字段，但 parent child list 不包含它。

注意：

- 这还不是完整的 render submit list / culling list。
- 目前尚未定位真正的独立 culling object list 或 render object list 指针。
- 本轮只读 probe 的价值是确认 Standalone 至少缺少 LayoutWorld/Layer/BgPart owner 链。

## 4. Standalone 缺失的具体注册位置

根据函数体和对比结果，目前最可疑缺失点：

1. `BgPartsLayoutInstance.SetActive(true)` 中的 active flag 同步。
2. `SetActive` 内部 helper `0x140456100(GraphicsObject)`。
3. `SetColliderActive` 之前/之后的 Layout active path。
4. LayoutWorld / LayerManager active instance list。
5. Culling manager / render submit list，可能由 `0x140456100` 或 `UpdateCulling` callsite 间接维护。

已经基本排除：

- 只写 `Graphics.Scene.Object Position / Rotation / Scale`。
- 只调用 `UpdateTransforms / UpdateRender / UpdateMaterials`。
- 只调用 `ComputeSphereBounds / UpdateCulling`。
- 手动 `AddChild / OnAddedToWorld`。这些入口已经暂停，原因是之前 AddChild 后 parentContainsThis 仍 false，并出现过 objectFlags 异常。

## 5. 下一步是否值得做写入实验

暂时不值得新增写入按钮。

下一步应继续只读研究：

- 反查 `0x140456100(GraphicsObject)` 的函数体和 callsite。
- 追 `UpdateCulling` 的 manager/list 参数来源。
- 搜索真实 render submit / culling list：
  - Graphics scene world object list
  - DrawObject active list
  - Culling object manager
  - Render object manager
- 确认是否存在安全的单对象 insert 函数。

只有满足以下条件，才考虑新的最小写入实验：

- 明确函数签名。
- 明确参数含义。
- 明确不会修改 parent/child/prev/next 链表为不一致状态。
- 明确不会要求真实 `ILayoutInstance` / `LayerManager` ownership。

如果后续证明 `SetActive` 或 render registration 必须依赖真实 `ILayoutInstance`，则 Standalone `BgObject` 不能直接稳定可见；当前可靠路线仍应保持 slot-backed copy，或另行研究如何安全创建/注册新的 LayoutInstance。

## 6. 当前安全策略

继续禁用：

- `AddChild`
- `OnAddedToWorld`
- `AddChild -> OnAddedToWorld`
- parent/child/prev/next 写入
- `CleanupRender`
- Dtor / RemoveFromWorld
- LayoutManager / LayerManager 容器写入
- collision
- 批量 Standalone

保留：

- CreateOnly
- Dump
- Validate
- Position 单字段写入
- ComputeSphereBounds bounds dump
- Standalone vs real BgPart 对比
- Standalone active/render list 只读取证
