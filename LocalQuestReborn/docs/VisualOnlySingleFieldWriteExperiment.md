# VisualOnly Single Field Write Experiment

## 目标

验证 `GraphicsObject + 0x20` 的 `MatrixRowTranslation.Y` 是否能只移动视觉模型，而不移动 collision/physics。

这是单字段实验，不是正式功能。

## 前置条件

必须满足：

- `probableVisualTransform=true`
- `stableFollowCount >= 2`
- 当前候选类型是 `MatrixRowTranslation`
- 当前候选 offset 是 `+0x20`
- `UnsafeMode=true`

不满足任一条件时拒绝写入。

## 写入范围

- offset：`GraphicsObject + 0x20 + 0x34`
- 字段：`MatrixRowTranslation.Y`
- write size：`4 bytes`
- write method：直接写单个 `float`

## 禁止项

本实验不会：

- 写 layout transform
- 调用 `ILayoutInstance.SetTransform`
- 写 collision/physics 字段
- 写整个 `Matrix4x4`
- memcpy
- 批量写
- 跨 instance 写

## UI 输出

写入后显示：

- original translation
- written translation
- readback translation
- graphicsObjectAddress
- matrixOffset
- write size
- write method

## 人工确认项

UI 提供人工确认按钮：

- 模型是否移动
- 碰撞是否没动
- 玩家是否还能站在原位置空气墙
- 是否只有视觉变化

## 恢复

“恢复 VisualOnly transform” 会写回保存的 original `MatrixRowTranslation.Y`。

注意：当前只保存并恢复 Y 字段，因为本实验只写了 Y 字段。
