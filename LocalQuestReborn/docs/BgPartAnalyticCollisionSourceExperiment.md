# BgPart target analytic collision 单实例实验

版本：v8.6  
范围：Debug-only，高风险，单实例。不会接入正式创建、删除、恢复全部、批量流程。

## 背景

v8.5 的 path table 取证后，新的关键发现是：

- 很多有碰撞的 BgPart 的 `CollisionMeshPathCrc = 0`。
- 这些 BgPart 通过 `AnalyticShapeDataCrc` 创建 analytic collider。
- 没碰撞的物体通常 `CollisionMeshPathCrc = 0`、`AnalyticShapeDataCrc = 0`、`Collider = null`。

因此 `FullLayoutWithCollision` 的 target collision 不能只找 target `.pcb`。需要支持从“目标 BgPart 实例”复制 collision source。

## 新增实现

新增服务：

- `Services/BgPartCollisionExperimentService.cs`

扩展模型：

- `Models/LocalLayoutObjectInstance.cs`

新增 UI：

- Debug 页
- `BgPart collision source / path table 取证（只读）`
- 内部新增 `FullLayout target collision source 单实例实验（高风险）`

## UI 流程

1. 在“本地场景物体”页选中一个 `LocalLayoutObjectInstance`。
2. 确认该实例处于 `FullLayoutWithCollision` 模式。
3. 在 BgPart 候选列表中选中一个目标 BgPart，例如 target 模型 `gote1` 的原地图实例。
4. 进入 Debug -> `BgPart collision source / path table 取证（只读）`。
5. 点击：
   - `选择当前 BgPart 为 collision source`
   - `保存当前实例 collision 快照`
6. 勾选：
   - `模型和碰撞体一起变化（危险）`
   - `我确认启用危险 FullLayoutWithCollision 模式`
   - `我确认只对当前单实例执行 DestroySecondary -> CreateSecondary collision 实验`
7. 点击：
   - `应用 source collision 到当前实例（高风险）`
8. 人工确认：
   - 碰撞是否变成 target 碰撞
   - 是否只有当前实例受影响
   - 是否稳定

## 保存的旧字段

对当前本地实例保存：

- `CollisionMeshPathCrc`
- `AnalyticShapeDataCrc`
- `CollisionMaterialIdLow`
- `CollisionMaterialMaskLow`
- `CollisionMaterialIdHigh`
- `CollisionMaterialMaskHigh`
- `Collider pointer`
- inferred `ColliderType`
- `GetSecondaryPath()` 读回

这些字段用于 `恢复原 collision source（高风险）`。

## source 字段

从目标 BgPart 复制：

- `CollisionMeshPathCrc`
- `AnalyticShapeDataCrc`
- `CollisionMaterialIdLow`
- `CollisionMaterialMaskLow`
- `CollisionMaterialIdHigh`
- `CollisionMaterialMaskHigh`

UI 会显示：

- source resourcePath
- source CollisionMeshPathCrc
- source AnalyticShapeDataCrc
- source material id/mask
- source ColliderType
- source secondary path

## 应用规则

### Analytic source

如果 source 是 `Analytic`：

```text
CollisionMeshPathCrc = 0
AnalyticShapeDataCrc = source.AnalyticShapeDataCrc
material id/mask = source material
DestroySecondary()
CreateSecondary()
```

### Mesh source

如果 source 是 `Mesh`：

```text
CollisionMeshPathCrc = source.CollisionMeshPathCrc
AnalyticShapeDataCrc = 0
material id/mask = source material
DestroySecondary()
CreateSecondary()
```

### None source

如果 source 是 `None`：

```text
CollisionMeshPathCrc = 0
AnalyticShapeDataCrc = 0
material id/mask = source material
DestroySecondary()
不调用 CreateSecondary()
```

这表示“当前实例无碰撞”。

## 安全边界

允许：

- 只对当前选中单实例执行。
- 只在 `UnsafeMode=true` 时执行。
- 只在 `FullLayoutWithCollision` 已二次确认时执行。
- 只复制已有 BgPart 的 collision source。

禁止：

- 不从任意 `.mdl` 猜 collision。
- 不写 layout path table。
- 不批量。
- 不接入正式创建/删除/恢复全部。
- 不碰 `VisualOnly`。
- 不碰 `CleanupRender`。

## 当前验收目标

目标测试：

1. 当前实例替换外观为 `gote1`。
2. source 选择 `gote1` 原地图实例。
3. 应用 source collision。
4. 当前实例碰撞变成 `gote1` 的 analytic collision。
5. `VisualOnly` 模式仍然无碰撞。
6. `FullLayoutWithCollision` 模式才允许执行。

## 风险点

- `DestroySecondary -> CreateSecondary` 真实操作 collision manager。
- `AnalyticShapeDataCrc` 必须在当前 layout 的 analytic data map 中有效。
- 复制 source CRC 只在当前 loaded layout 内较可信；跨地图、跨 layout 不可信。
- 如果 source 的 analytic data 不在当前实例 layout manager 中，`CreateSecondary` 可能不会生成有效 collider。
- 恢复按钮依赖实验前保存的旧字段，测试前务必先保存快照。

## 下一步

如果单实例验证成功，可以继续研究：

1. source 与 target visual model 的绑定 UI。
2. 对 recreate 后 FullLayout 实例自动提示“可手动应用 source collision”。
3. source collision 与 target model path 的保存格式。
4. 仍然不要接入批量，直到确认稳定恢复策略。
