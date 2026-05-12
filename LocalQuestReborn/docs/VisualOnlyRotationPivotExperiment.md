# VisualOnly Rotation Pivot Experiment

## 当前正式功能

VisualOnly 本地场景物体的正式能力仍然只有 position：

- 占用当前地图已有 BgPart slot。
- 只写 `GraphicsObject +0x20 MatrixRowTranslation`。
- 不写 layout transform。
- 不移动 collision / physics。
- 支持创建、移动、恢复、删除、恢复全部。

## 已废弃实验

以下内容已从主 UI 移到 Debug：

- VisualOnly rotation 单 float 写入。
- 旧 matrix offset 探针。
- 旧 matrix block yaw 写入。
- FullLayoutWithCollision 危险路线。

当前结论：

- 单 float 写入没有视觉旋转效果。
- `+0x03C` yaw block 会导致向下平移。
- `+0x040` yaw block 会导致围绕 pivot 平移绕圈。

所以 rotation / scale 不能作为正式功能。

## Pivot 实验目标

验证是否可以“只改变朝向，不改变最终世界位置”。

实验步骤：

1. 读取当前 VisualOnly translation `P`。
2. 读取候选 matrix block。
3. 对候选 block 的 3x3 basis 应用 yaw +10 度。
4. 写入候选 block。
5. 立即把 `GraphicsObject +0x20` row translation 强制恢复为 `P`。
6. readback：
   - translation 是否保持不变。
   - rotation 分量是否变化。
7. 人工确认：
   - 原地旋转。
   - 仍然平移。
   - 模型异常。

## 安全边界

本实验仍然只在 Debug 中出现，并且必须 `UnsafeMode=true`：

- 不写 layout transform。
- 不写 collision。
- 不批量。
- 只对当前候选 BgPart。
- 写前保存原始 matrix block。
- 失败可一键恢复。
