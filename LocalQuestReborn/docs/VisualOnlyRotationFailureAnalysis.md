# VisualOnly Rotation Failure Analysis

## 当前结论

VisualOnly rotation matrix 写入已暂停。

已确认：

1. `GraphicsObject +0x20 MatrixRowTranslation` 是安全的 position 写入入口。
2. `GraphicsObject +0x03C / +0x040` 附近 matrix 会跟随 layout yaw / scale 变化。
3. 直接写 rotation 相关内容不安全：
   - 单 float 写入无有效旋转结果。
   - `+0x03C` 完整 rotation matrix 写入失败，出现下沉/异常。
   - `+0x040` 完整 rotation matrix 写入后模型消失。
   - 恢复 matrix 不足以保证模型立即回来。

因此不要继续直接写 `+0x03C / +0x040` 完整矩阵。

## 保留功能

VisualOnly position 正式功能继续保留：

- 只写 `GraphicsObject +0x20 MatrixRowTranslation`。
- 不写 rotation。
- 不写 scale。
- 不写 layout transform。
- 不写 collision。

## 新增救援路径

新增 BgPart visual translation 救援：

- `把选中 BgPart 视觉模型移到玩家脚下`
- `恢复选中 BgPart visual translation`
- `全部移回玩家脚下`
- `全部恢复原 visual translation`

这些按钮只写 `+0x20` translation，不碰其它 matrix 分量。

## 后续只读取证方向

Rotation 后续只能先做只读取证：

1. 比较模型消失前后的 `+0x03C / +0x040` matrix。
2. 记录异常分量：
   - NaN / Infinity
   - 极端 translation
   - scale 接近 0 或异常大
   - 非正交 basis
3. 研究是否需要同步更新：
   - dirty flag
   - GraphicsObject refresh
   - model reload
   - bounding box / culling data
   - render scene update queue

在确认安全 refresh/update 入口前，rotation 不进入正式功能。
