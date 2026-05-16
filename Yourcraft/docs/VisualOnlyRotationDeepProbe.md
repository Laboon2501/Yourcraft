# VisualOnly Rotation Deep Probe

## 目标

继续研究 VisualOnly rotation，但本轮只做取证，不做危险写入。

禁止：

- 写 `GraphicsObject +0x03C`
- 写 `GraphicsObject +0x040`
- 写完整 matrix
- 写 resourcePath
- 写 layout collision

允许：

- 临时写 layout transform 做对比，然后立即恢复。
- 读取 GraphicsObject 附近矩阵和 Vector3 候选。

## Dump 内容

对选中 BgPart 读取：

- `+0x20 Matrix4x4`
- `+0x30 Matrix4x4`
- `+0x3C Matrix4x4`
- `+0x40 Matrix4x4`
- `+0x48 Matrix4x4`
- `+0x50` 到 `+0x90` 附近 Vector3
- layout position
- layout rotation
- layout scale
- visual translation

## 对比实验

`VisualOnlyRotationDeepProbeService` 执行：

1. baseline dump
2. layout yaw +10 度
3. dump
4. restore
5. layout yaw +30 度
6. dump
7. restore
8. layout pitch +10 度
9. dump
10. restore
11. layout roll +10 度
12. dump
13. restore

每一步只改 layout transform，并在 capture 后恢复原 transform。

## 输出分析

UI Debug 页显示：

- rotation candidate table
- pivot analysis
- dirty/update candidate
- 建议下一步写入方案

candidate table 会标记：

- 哪些 matrix basis 分量变化
- translation 是否跟随变化
- 哪些候选像 rotation-only
- 哪些候选像 rotation + translation / pivot
- 哪些候选像 scale

## Pivot 研究方向

当前关注：

- layout position
- visual translation
- matrix translation
- resource/model local origin
- pivot candidate

如果某个 matrix 在 yaw 变化时 translation 同时变化，它更可能是 pivot/culling/中间空间数据，而不是可直接覆盖的最终 visual rotation。

## Dirty / Refresh 方向

只读扫描 FFXIVClientStructs 当前可访问类型，寻找：

- Update
- Refresh
- Dirty
- SetTransform
- CalculateTransform
- DrawObject
- Model
- bounding / culling

在确认安全入口前，rotation 写入继续保持暂停。
