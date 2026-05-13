# BgPart Recreate Collision Modes

## 当前结论

`DestroyPrimary -> CreatePrimary` 已能让 `BgPart` 的 target mdl 外观变化，但碰撞行为不能简单等同于视觉替换：

- `CreatePrimary` 只重建 primary graphics。
- 旧 slot 的 secondary/collider 可能仍然存在。
- 替换 mdl 后，碰撞不一定自动变成 target 模型对应 collision。

因此 v8.3 把 recreate 后的碰撞行为绑定到 `LocalLayoutObjectInstance.transformMode`。

## VisualOnly

目标：替换/重建 GraphicsObject，但不把 collision 移到本地实例位置。

实现顺序：

1. 保存快照：
   - `GraphicsObject`
   - `IndexInPool`
   - `original Layout transform`
   - `Collider pointer`，只读，offset `0x38`
   - `transformMode`
   - `ModelResourceHandle`
2. 调用：
   - `DestroyPrimary()`
   - `CreatePrimary(originalLayoutTransform, &targetPathPointer)`
3. 立刻恢复 LayoutInstance 到原始 slot transform。
4. 只写 `Graphics.Scene.Object Position/Rotation/Scale` 到本地实例位置。
5. 不调用：
   - `CreateSecondary`
   - `DestroySecondary`
   - `SetGraphics`
   - `BgObject.Create`
   - Collider 写入

预期行为：

- target mdl 外观变化。
- 本地脚下只有视觉模型。
- collider 仍留在原 slot，不跟随到玩家脚下。

如果 `CreatePrimary` 后 `GraphicsObject=null`、`ModelResourceHandle=null` 或 `visible=false`，实例会标记 `isRenderInvalid=true`，后续 transform / RestoreAll 写入会被跳过，需要切图或重载地图恢复。

## FullLayoutWithCollision

当前只保留高风险实验入口，不完整实现 target collision。

FullLayout recreate 当前行为：

- `CreatePrimary` 使用当前 Layout transform。
- 模型和 layout transform 可一起变化。
- 记录 Collider before/after。
- 不调用 `CreateSecondary`，所以 target collision 尚未保证同步。

后续需要只读取证：

- `BgPartsLayoutInstance.CreateSecondary`
- `BgPartsLayoutInstance.DestroySecondary`
- `Collider pointer`
- `OffsetPathPcb / PathPcb`
- target mdl 到 pcb/collision 的路径规则
- `CreateSecondary` 是否使用 `PathPcb`
- collision transform 来源

## 风险边界

- 不批量 recreate。
- 不接入正式批量复制。
- VisualOnly 默认不移动 collision。
- FullLayoutWithCollision 必须二次确认。
- 不 memcpy。
- 不碰 Collider 字段写入。
