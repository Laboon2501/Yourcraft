# VisualOnly Rotation Single Float Experiment

## 当前结论

`GraphicsObject + 0x03C / +0x040` 附近的单个 float 会跟随 layout rotation / scale 变化，但单个 float 写入没有产生可见旋转效果。

因此该实验标记为失败/无效，只保留为取证记录。VisualOnly 正式功能仍然只支持 position。

## 原始目标

验证 `BgPart.GraphicsObject` 附近稳定跟随 layout yaw / scale 的矩阵候选，是否可以通过单个 float 写入实现只改变视觉 rotation，而不移动 collision。

本实验不是正式功能，只用于取证。

## 候选字段

当前优先测试：

- `Matrix4x4 @ GraphicsObject + 0x03C`
- `Matrix4x4 @ GraphicsObject + 0x040`

UI 允许选择 matrix offset，并只允许写入以下 rotation 候选 float index：

- `float[1]`
- `float[2]`
- `float[4]`
- `float[6]`
- `float[8]`
- `float[9]`

这些 index 避开了 row translation 的 `float[12] / [13] / [14]`，也避开了常见 scale diagonal 的 `float[0] / [5] / [10]`。

## 写入规则

`VisualOnlyRotationWriteExperimentService` 每次只写一个 4-byte float：

```text
address = graphicsObjectAddress + matrixOffset + floatIndex * 4
```

按钮：

- `Rotation 单分量 +0.1`
- `Rotation 单分量 -0.1`
- `恢复 rotation 原值`

写入前保存：

- `graphicsObjectAddress`
- `matrixOffset`
- `floatIndex`
- 原始 float 值
- 写入前 matrix dump

写入后记录：

- 写入值
- readback
- 写入后 matrix dump
- 人工确认结果

## 禁止事项

本实验明确禁止：

- 写 translation
- 写 scale
- 写 layout transform
- 写整个 `Matrix4x4`
- `memcpy`
- 跨 instance 写入
- 批量写入

## 人工确认项

每次写入后需要人工确认：

- 模型是否旋转
- collision 是否没动
- 模型是否炸裂/消失

## 当前判断标准

只有当单个 float 写入能稳定让视觉模型旋转，并且 collision 不移动、不生成空气墙、不影响 layout transform 时，才考虑继续扩大实验范围。

在此之前，LocalQuestReborn 的 VisualOnly 正式功能仍然只支持 position。
