# BgPart Recreate Single Instance Experiment

## 状态

v8.2 只新增 Debug-only 高风险实验入口。默认禁用，必须同时满足：

- `UnsafeMode=true`
- 已选中一个 `LocalLayoutObjectInstance`
- 显式启用 BgPart recreate 实验
- 勾选二次确认
- target path 为 `bg/...mdl`
- 当前资源 category 为 `Bg`

该路径不会接入创建、删除、恢复全部或批量复制。

## 实验链

已确认的函数体链路：

```text
DestroyPrimary()
  -> CleanupRender()
  -> this->GraphicsObject = null

CreatePrimary(originalTransform, &targetPathPointer)
  -> BgObject.Create(modelGamePath=*targetPathPointer, poolName="Client.LayoutEngine.Layer.BgPartsLayoutInstance", existingAllocation)
  -> BgObject.SetModel(...)
  -> SetTransformImpl(originalTransform)
  -> SetActive(...)
```

## 指针生命周期

`CreatePrimary` 的第二参数按函数体取证是 `char** / byte**`。插件不会使用临时栈内存作为 target path：

- UTF-8 null-terminated path buffer 使用 `GCHandle` 固定。
- `char**` storage 使用 `Marshal.AllocHGlobal(IntPtr.Size)` 保存，并写入 pinned buffer 地址。
- 快照记录 buffer 地址和 `char**` storage 地址。

## 安全边界

本实验不做：

- 不调用 `SetGraphics`
- 不直接调用 `BgObject.Create`
- 不 `memcpy`
- 不批量
- 不碰 Collider
- 不接入 `RestoreAll`

执行后如果 `GraphicsObject=null`、`ModelResourceHandle=null` 或 `visible=false`，实例会标记：

```text
isRenderInvalid=true
transformWriteDisabledReason=请切图/重载地图恢复
```

后续 transform 写入和 RestoreAll 写入会被现有保护跳过。
