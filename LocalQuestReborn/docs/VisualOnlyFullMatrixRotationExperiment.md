# VisualOnly Full Matrix Rotation Experiment

## 当前前提

已确认：

1. `GraphicsObject +0x20 MatrixRowTranslation` 已正式支持 position。
2. `GraphicsObject +0x03C / +0x040` 附近 matrix 会稳定跟随 layout yaw / scale。
3. 单 float rotation 写入失败：
   - `+0x03C` 会下沉。
   - `+0x040` 会绕 pivot 平移。

因此 rotation 不能通过单 float 修改。

## 本轮目标

验证完整 rotation matrix block 写入：

1. 读取当前候选 matrix block。
2. `Matrix4x4.Decompose` 分离：
   - translation
   - rotation
   - scale
3. 保持 `translation = originalTranslation`。
4. 构造 yaw delta：
   - `+10°`
   - `-10°`
5. 重组：

```text
newMatrix =
    Scale(originalScale)
  * Rotation(newYaw)
  * Translation(originalTranslation)
```

6. 只写回对应的 `GraphicsObject` matrix block。
7. readback matrix 和 translation。
8. 检查 translation 是否未变化。

## UI 按钮

Debug -> VisualOnly Rotation Pivot 实验：

- `yaw +10°`
- `yaw -10°`
- `恢复原始 matrix`
- `readback matrix`

人工确认：

- 原地旋转
- 仍然绕圈
- 下沉
- collision 没动
- 模型炸裂

## 安全边界

本实验仍然是 Debug/UnsafeMode 功能：

- 不写 layout transform。
- 不写 collision。
- 不 memcpy 整个 instance。
- 不批量。
- 只允许当前选中的一个 BgPart。
- 写前保存原始 matrix，失败可恢复。

## 判定

如果完整 matrix block 重组后仍然绕圈或下沉，则说明 `+0x03C / +0x040` 不是可以直接用于 VisualOnly rotation 的最终渲染矩阵，下一步需要继续寻找 render update / dirty 入口，或寻找真正的 local/world visual rotation 字段。
