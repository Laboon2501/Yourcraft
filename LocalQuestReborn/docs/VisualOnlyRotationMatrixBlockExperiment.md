# VisualOnly Rotation Matrix Block Experiment

## 背景

v5.4 的单 float 实验结果：

- `GraphicsObject + 0x03C / +0x040` 附近的 float 会跟随 layout rotation / scale 变化。
- 但单个 float 写入没有产生可见旋转效果。

当前判断：rotation 可能不能靠单分量写入，需要验证完整 matrix block，或者需要找到 render update / dirty flag。

VisualOnly position 正式功能保持不变，仍然只写 visual translation。

## v5.6 结论

matrix block 实验已确认仍不是想要的正式 rotation：

- 写 `GraphicsObject + 0x03C` 的 yaw block 会导致模型向下平移。
- 写 `GraphicsObject + 0x040` 的 yaw block 会导致模型围绕自身原点或某个 pivot 平移绕圈。

这说明直接写候选 block 不等于“原地改变朝向”。VisualOnly rotation 尚未跑通，不能作为正式功能。

下一步实验方向改为 Pivot 保持位置：

1. 读取当前 VisualOnly translation `P`。
2. 构造 yaw rotation `R`。
3. 写入候选 matrix block。
4. 立即强制恢复 `GraphicsObject +0x20` row translation 为 `P`。
5. readback 检查 translation 是否保持不变、rotation 分量是否变化。

## 实验范围

新增 `VisualOnlyRotationMatrixBlockExperimentService`。

只允许测试两个候选：

- `Matrix4x4 @ GraphicsObject + 0x03C`
- `Matrix4x4 @ GraphicsObject + 0x040`

按钮：

- 写入 yaw +10° matrix block
- 恢复原始 matrix block
- 尝试 refresh/dirty

## 写入策略

写入 yaw +10° 时：

1. 读取当前候选 `Matrix4x4`。
2. 保存原始 matrix block。
3. 对 matrix 的 3x3 basis 应用 yaw +10°。
4. 保留原 matrix block 的 translation 行。
5. 写回同一个 matrix block。
6. 立即 readback 并 dump。

当前没有确认安全的 refresh / dirty 入口，因此“尝试 refresh/dirty”只记录状态，不做未知 native 写入。

## 禁止事项

本实验禁止：

- 写 layout transform。
- 写 collision / physics。
- memcpy 整个 instance。
- 批量写入。
- 跨 instance 写入。

## 人工确认

写入后需要人工确认：

- 模型是否旋转。
- collision 是否不动。
- 模型是否炸裂/消失。
- 如果 readback 改变但画面无效，记录为需要 render update / dirty 入口。

## 失败判定

如果 matrix block 写入 readback 成功但视觉仍然无效，则当前结论应更新为：

VisualOnly 暂只支持 position；rotation / scale 需要找到 render update / dirty 入口，或找到另一组真正参与渲染提交的 transform 字段。
