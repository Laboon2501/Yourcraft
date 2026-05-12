# VisualOnly Rotation / Scale Probe

## 当前结论

LocalQuestReborn 的 VisualOnly 本地场景物体目前只把 `BgPart.GraphicsObject + 0x20` 处矩阵的 row translation 写入为正式功能。

已验证有效：

- 写入 `MatrixRowTranslation.X/Y/Z` 可以移动视觉模型。
- 不写 layout transform。
- 不移动 collision / physics。

未验证为有效功能：

- 直接写 rotation。
- 直接写 scale。
- 写完整 `Matrix4x4` 来组合 position / rotation / scale。

因此 v5.3 修正后，UI 中的 rotation / scale 只保留为取证输入和状态记录，不再被标记为已支持功能。

## 新增取证流程

`BgPartVisualTransformProbeService.ProbeRotationScaleCandidates()` 用于寻找 GraphicsObject 附近可能跟随 layout rotation / scale 变化的字段。

流程：

1. 使用当前已选 BgPart 的 `graphicsObjectAddress`。
2. 在 `graphicsObjectAddress + 0x00` 到 `+0x300` 范围内扫描合理的 `Matrix4x4` 候选。
3. 保存改动前矩阵快照。
4. 临时把 layout rotation 改为 yaw +10 度。
5. 再次扫描矩阵，比较哪些 offset 发生同步变化。
6. 恢复 layout transform。
7. 临时把 layout scale 改为原 scale * 1.1。
8. 再次扫描矩阵，比较哪些 offset 发生同步变化。
9. 最后恢复 layout transform。

本取证只临时写 layout transform，然后立即恢复；不会写 GraphicsObject 字段。

## UI 表现

本地场景物体页现在明确显示：

- VisualOnly 当前正式支持：position。
- Rotation / Scale 输入仅用于取证记录，写入未实现。
- “应用 transform”改为“应用 position”。
- “重置旋转 / 重置缩放 / 缩放 +/-”标记为未实现或实验中。

## 下一步

如果 rotation / scale 取证能稳定找到跟随字段，需要继续做极小范围实验：

1. 只选一个 BgPart。
2. 只写一个候选字段或最小矩阵分量。
3. 写前保存原始值。
4. 写后立即 readback。
5. 人工确认视觉模型是否旋转/缩放。
6. 确认 collision 是否完全不动。

在完成以上验证前，rotation / scale 不应进入正式 VisualOnly 功能。
