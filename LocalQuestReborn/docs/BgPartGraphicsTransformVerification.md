# BgPart Graphics Transform Verification

## 目标

验证 `BgPart.GraphicsObject + offset` 附近的候选字段是否是真正跟随 BgPart 的 render/world transform。

本版本仍然不写 `GraphicsObject` 字段。

## 验证流程

UI 按钮：“重新验证当前候选”

流程：

1. 使用上一轮 Dump 保存的最佳候选：
   - `graphicsObjectAddress`
   - `layoutInstanceAddress`
   - matrix/vector offset
   - candidate translation
2. 读取当前 candidate translation，记为 before。
3. 临时调用 `ILayoutInstance.SetTransform`，把 layout transform 的 Y 增加 `1.0`。
4. 重新读取同一个 graphicsObject offset，记为 after。
5. 计算 `delta = after - before`。
6. 立即恢复原始 layout transform。
7. 如果 `delta ≈ (0, 1, 0)`，记录本轮 stable follow。
8. 连续多次 stable 后标记：
   - `probableVisualTransform=true`

## 输出字段

- before translation
- after translation
- delta
- expected layout delta
- stableFollow
- stableFollowCount
- probableVisualTransform
- layout transform 是否已恢复

## 安全边界

本验证只把 layout transform 临时移动作为探针，随后恢复。

不会写入：

- GraphicsObject 字段
- matrix 候选字段
- vector 候选字段
- collision/physics 字段
- resourcePath

## 下一步

如果某个候选连续稳定跟随 layout 变化，下一步可以增加单字段 GraphicsObject 写入实验：

1. 仅 UnsafeMode=true 时显示。
2. 每次只写一个候选 offset。
3. 写前保存原值。
4. 写后 readback。
5. 人工确认视觉移动。
6. 同时确认碰撞没有移动。

在碰撞验证之前，不能启用 VisualOnly 创建。
