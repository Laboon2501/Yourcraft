# 本地场景物体正式 mdl / collision 流程

版本：v8.7 + bgcommon 补充

## 目标

把已验证的两条路线合并进“本地场景物体”正式流程：

- `VisualOnly`：替换模型后只改变视觉，不移动/创建 collision。
- `FullLayoutWithCollision`：替换模型后自动查找 target mdl 对应 BgPart，并复制它的 collision source，再重建 secondary collider。

旧的 Debug 手动流程不再作为用户主入口。

## 支持的 mdl path

正式 `custom mdl path` 只支持：

- `bg/.../*.mdl` -> `ResourceCategory.Bg = 2`
- `bgcommon/.../*.mdl` -> `ResourceCategory.BgCommon = 1`

校验规则：

- 必须以 `.mdl` 结尾。
- 必须以 `bg/` 或 `bgcommon/` 开头。
- 当前实例的 before category 与 target category 可以不同，插件会记录：
  - before category
  - target category
  - after category

其它资源类型暂不支持。

## 正式入口

位置：

- `本地场景物体` 页
- 选中某个 `LocalLayoutObjectInstance`
- 输入 `custom mdl path`
- 点击 `应用 mdl path`

模式由同页顶部开关统一控制：

- 未勾选 `模型和碰撞体一起变化（危险）`：`VisualOnly`
- 勾选并二次确认：`FullLayoutWithCollision`

## VisualOnly 规则

应用 mdl path 时：

1. `DestroyPrimary -> CreatePrimary(targetMdl)`
2. 不调用 `CreateSecondary`
3. 不复制 collision source
4. 恢复 `LayoutInstance` 到原始 slot transform
5. 对新 `GraphicsObject` 写 `Graphics.Scene.Object Position/Rotation/Scale`
6. `collision moved=false`

结果：

- 外观变成 target mdl。
- 当前本地位置没有 collision。
- 原 slot collision 保持在原地图位置。

## FullLayoutWithCollision 规则

应用 mdl path 时：

1. `DestroyPrimary -> CreatePrimary(targetMdl)`
2. 通过 `BgPartCollisionSourceResolver` 在当前 Layout/BgPart Slot Pool 中查找：
   - `resourcePath == targetMdlPath`
   - 支持 `bg/` 与 `bgcommon/`
3. 优先选择有 collision 的 source：
   - `AnalyticShapeDataCrc != 0`
   - 或 `CollisionMeshPathCrc != 0`
   - 或 `Collider != null`
4. 复制 source collision 字段：
   - `CollisionMeshPathCrc`
   - `AnalyticShapeDataCrc`
   - material id/mask
5. `DestroySecondary -> CreateSecondary`
6. 应用 `LayoutInstance transform`

结果：

- 外观变成 target mdl。
- collision 变成 target BgPart 的 collision source。
- 位移/旋转/缩放会让模型和 collision 一起变化。

如果找不到 source：

- 仍替换模型。
- 不假装 collision 已替换。
- UI 显示“未找到目标 mdl 对应的 collision source，模型已替换，但 collision 未替换/保持原状态。”

## 服务分工

- `BgPartCollisionSourceResolver`
  - 输入 target mdl path + 当前 BgPart 列表
  - 输出 source slot、source resourcePath、collision type、CRC、material、secondary path
- `BgPartRecreateExperimentService`
  - 负责 `DestroyPrimary -> CreatePrimary`
  - 按 target path 前缀记录 `Bg` / `BgCommon` category
- `BgPartCollisionExperimentService`
  - 负责复制 source collision 并 `DestroySecondary -> CreateSecondary`
- `LayoutObjectTransformService`
  - 根据 `transformMode` 写 VisualOnly 或 FullLayout transform

## 安全边界

仍然禁止：

- `SetModel`
- `CleanupRender`
- path table 写入
- 从任意 `.mdl` 猜 collision
- 批量 native collision 操作
- `VisualOnly` 创建/移动 collision

正式 `应用 mdl path` 仍需要：

- `UnsafeMode=true`
- FullLayout 模式需要二次确认
- target path 必须是 `bg/...mdl` 或 `bgcommon/...mdl`

## v9.0 模板批量创建

“本地场景物体”页的模板创建遵循：

- 模板 BgPart 只作为默认 mdl path、默认 transform、初始外观参考。
- 模板 slot 永远从可分配 slot 中排除，不会被搬走或改 mdl。
- 未填写批量默认 `custom mdl path` 时，只使用同 `resourcePath` 的可用 slot。
- 填写批量默认 `custom mdl path` 时，允许使用任意 `bg/` 或 `bgcommon/` 可用 BgPart slot，并在每个实例创建后立刻对该实例执行正式 `应用 mdl path` 流程。
- 每个实例拥有独立 `occupiedSlotAddress`、`customModelPath`、`currentModelPath` 和 `transformMode`。
- 批量应用 mdl 失败只标记对应实例，不影响其它已创建实例。

## 验收目标

1. 不勾选危险模式：
   - target mdl = `gote1`
   - 外观变成 `gote1`
   - 脚下无碰撞
2. 勾选危险模式并确认：
   - target mdl = `gote1`
   - 外观变成 `gote1`
   - collision source 自动匹配 `gote1` 原地图实例
   - 脚下碰撞变成 `gote1` 的 analytic/mesh collision
3. 移动/旋转/缩放：
   - VisualOnly 只动视觉
   - FullLayout 模型和 collision 一起动
