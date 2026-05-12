# BgPart VisualOnly Transform Probe

## 结论

当前已经验证：直接写 `ILayoutInstance.SetTransform` 可以移动 BgPart，但这属于 `FullLayoutWithCollision` 路线。它会把 layout instance 的整体 transform 改掉，碰撞体也跟着移动，角色可以站在被移动后的物体上。因此它不适合作为默认“本地视觉物件”能力。

## layout transform 控制什么

`ILayoutInstance.GetTransformImpl()` / `SetTransform()` 是 Layout instance 级别 transform。对 BgPart 来说，它影响可见模型位置，同时也会影响该 layout slot 关联的碰撞/物理表现。

风险：

- 本机玩家可以站在移动后的碰撞上。
- 其他玩家/服务器并不知道该本地移动，可能表现为玩家原地浮空或位置异常。

## graphics/draw transform 控制什么

BgPart 暴露了 `GraphicsObject` 指针，并且可以通过 Meddle 风格结构读到 `ModelResourceHandle` / mdl path。但当前还没有确认可安全写入的 render-only transform 字段。

当前 probe 会读取：

- Layout instance address
- GraphicsObject address
- ModelResourceHandle
- layout position/rotation/scale
- render/model transform candidate 状态
- collision/physics handle candidate 状态

## collision 是否随 layout transform 移动

是。现有实验已经证明写 layout transform 后，碰撞也发生移动。

## 是否存在只改视觉的 transform

本轮还没有找到安全入口。由于没有确认 `GraphicsObject` 内部 transform / world matrix 的字段布局和刷新流程，插件不会在 `VisualOnly` 模式下 fallback 到 layout transform。

当前行为：

- `VisualOnly` 是默认推荐模式。
- 但由于安全写入入口未找到，创建会被禁用并显示：
  “暂未找到只移动视觉模型的安全入口，不能创建无碰撞本地物件。”
- 只有用户显式确认 `FullLayoutWithCollision` 后，才允许写 layout transform。

## 下一步写入路径

后续需要继续取证：

1. 定位 BgPart `GraphicsObject` 的世界矩阵或 render transform。
2. 验证写 render transform 是否不影响 phyb/collision。
3. 找到 render object dirty/update/refresh 入口。
4. 保证写入只影响本地画面，不移动 layout collision。

在完成这些之前，`VisualOnly` 不应假装成功。
