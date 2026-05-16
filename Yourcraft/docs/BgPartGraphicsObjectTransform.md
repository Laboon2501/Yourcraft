# BgPart GraphicsObject Transform

## 当前结论

BgPart 的 `GraphicsObject` 不是普通 `GameObject.DrawObject`。它可以读到模型资源句柄，但还不能确认一个安全的 render-only transform 写入字段。

因此 `VisualOnly` 仍然保持禁用：没有确认只影响视觉、不影响 collision/physics 的写入入口前，不允许 fallback 到 `ILayoutInstance.SetTransform`。

## GraphicsObject 类型结构

当前通过 Meddle 风格结构读取：

- `BgPartsLayoutInstance.GraphicsObject`
- `GraphicsObject + 0x90` 附近的 `ModelResourceHandle`
- `ModelResourceHandle.FileName`

这足以确认 mdl/resource 来源，但不足以确认 transform 字段。

## 已新增只读矩阵扫描

`BgPartVisualTransformProbeService` 会在 `graphicsObjectAddress` 附近做只读扫描：

- 扫描范围：`0x00` 到 `0x300`
- 对齐：4 字节
- 读取候选：
  - `Vector3`
  - `Matrix4x4`
- 过滤：
  - 非 NaN / 非 Infinity
  - 数值范围合理
  - translation 距离 layout position 50m 内

输出内容包括：

- `graphicsObjectAddress`
- `modelResourceHandle`
- layout position / rotation / scale
- Vector3 候选偏移
- Matrix4x4 候选偏移
- row-major / column-major translation 候选和 layout position 的距离

## 哪些字段像 position/matrix

判断标准：

- `Vector3` 值接近 `layoutPosition`
- `Matrix4x4.M41/M42/M43` 或 `M14/M24/M34` 接近 `layoutPosition`
- 矩阵元素有限且 `M44` 合理

这些仍只是候选，不代表可写。

## 哪些字段可能只影响视觉

可能方向：

- `GraphicsObject` 内部 world matrix
- model/render object local transform
- graphics scene object dirty/update 字段

当前还未确认写入后是否只影响视觉，也未确认是否需要调用 refresh/update。

## 哪些字段会带 collision

已确认：

- `ILayoutInstance.SetTransform`

这会移动 layout instance 的整体 transform，碰撞体也随之移动。

## 下一步最小写入实验

下一步不应直接批量写。

建议顺序：

1. 在 UI 中观察扫描结果，找出和 layout position 高度接近的 matrix/vector offset。
2. 对单个候选 offset 增加只读稳定性验证：移动 layout 后重新 dump，看候选字段是否同步变化。
3. 如果某个 graphics matrix 稳定跟随视觉模型，再新增单字段写入按钮。
4. 写入前保存原值。
5. 写入后 readback。
6. 人工确认：
   - 模型是否移动
   - 碰撞是否没动
7. 只有确认 collision 不动，才允许启用 `VisualOnly`。
