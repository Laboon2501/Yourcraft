# BgPart collision path table 取证

版本：v8.5  
范围：只读 dump，不调用 `CreateSecondary` / `DestroySecondary`，不写 `CollisionMeshPathCrc`。

## 背景

当前已确认：

- `VisualOnly` 替换模型成功，且不移动 collision。
- `FullLayoutWithCollision` 的目标是：替换后使用 target 模型自己的 collision。
- `CreateSecondary()` 没有 path/transform 参数。
- `CreateSecondary()` 读取 `BgPartsLayoutInstance` 内部的 `CollisionMeshPathCrc` / `AnalyticShapeDataCrc`，再通过 `LayoutManager.CrcToPath` 解析 path。

因此下一步必须先确定 target collision source 是否已经在当前 layout path table 中，而不是直接调用 `CreateSecondary`。

## 新增只读工具

新增服务：

- `Services/BgPartCollisionSourceProbeService.cs`

Debug 页新增折叠区：

- `BgPart collision source / path table 取证（只读）`

按钮：

- `Dump 当前 BgPart collision source / path table`

读取内容：

- 当前选中 BgPart：
  - `CollisionMeshPathCrc`
  - `AnalyticShapeDataCrc`
  - inferred `ColliderType`
  - `Collider pointer`
  - `CollisionUpdateListener`
  - material id/mask
  - runtime `GetPrimaryPath()`，作为 `PathMdl` 读回
  - runtime `GetSecondaryPath()`，作为 `PathPcb` / secondary path 读回
- 当前 `LayoutManager.CrcToPath`：
  - `crc -> path`
  - 通过字符串反查 `path -> crc`
- 多个 BgPart 对比：
  - `mdl path`
  - `pcb path`
  - `CollisionMeshPathCrc`
  - `AnalyticShapeDataCrc`
  - inferred `ColliderType`
- target 候选：
  - `targetPcbPath`
  - `targetCollisionCrc`
  - 是否存在于当前 layout path table

## 字段来源

`BgPartsLayoutInstance`：

```csharp
GraphicsObject             // 0x30
Collider                   // 0x38
CollisionMeshPathCrc       // 0x40
AnalyticShapeDataCrc       // 0x44
CollisionMaterialIdLow     // 0x48
CollisionMaterialMaskLow   // 0x4C
CollisionMaterialIdHigh    // 0x50
CollisionMaterialMaskHigh  // 0x54
CollisionUpdateListener    // 0x60
```

`ILayoutInstance`：

```csharp
GetPrimaryPath()    // 当前 primary/model path
GetSecondaryPath()  // 当前 secondary/collision path
```

`LayoutManager`：

```csharp
CrcToPath // offset 0x278, StdMap<uint, Pointer<RefCountedString>>
```

`FileLayerGroupInstanceBgPart` 文件结构里仍然有：

```csharp
OffsetPathMdl
OffsetPathPcb
ColliderType
OffsetColliderAnalyticData
PathMdl
PathPcb
```

但运行态当前工具不直接持有原始 `FileLayerGroupInstanceBgPart*`，因此 UI 中的 `PathMdl/PathPcb` 读回使用 `GetPrimaryPath()` / `GetSecondaryPath()`。这比猜文件 offset 更安全，也更贴近 `CreatePrimary/CreateSecondary` 实际运行态。

## crc -> path

当前可从：

```csharp
((ILayoutInstance*)bgPart)->Layout->CrcToPath
```

枚举到当前 loaded layout 的 path table。

每一项为：

```text
0xCRC -> path
```

其中 `.mdl` 与 `.pcb` 都可能出现。`GetSecondaryPath()` 的反汇编也证明它从 `CollisionMeshPathCrc` 进入 layout path table 解析，而不是自己拼字符串。

## path -> crc

本轮没有写 path table，也没有调用注册函数。

当前反查方式是：

1. 枚举 `CrcToPath`。
2. 用字符串大小写无关比较查找 path。
3. 找到则使用对应 key 作为候选 CRC。

这只能回答：

```text
target path 是否已经存在于当前 layout path table 中
```

不能回答：

```text
如何把任意新 path 注册进 path table
```

后者仍需要继续取证。

## ColliderType 判定

运行态 inferred 规则：

```text
AnalyticShapeDataCrc != 0 -> Analytic
CollisionMeshPathCrc != 0 -> Mesh
否则 -> None
```

这是运行态推断，不等同于直接读取 `FileLayerGroupInstanceBgPart.ColliderType`。如果后续找到运行态保存原始 file instance data 的指针，可以再补充真实 `ColliderType`。

## target pcb 候选规则

输入：

```text
targetMdlPath = bg/.../xxx.mdl
```

候选：

```text
targetPcbPath = bg/.../xxx.pcb
```

然后在 `LayoutManager.CrcToPath` 里查：

- `targetMdlPath`
- `targetPcbPath`

输出：

- `targetPcbPath`
- `targetCollisionCrc`
- `target pcb in path table`

重要边界：

- 同名 `.pcb` 是候选规则，不是安全事实。
- 只有 `targetPcbPath` 已存在于当前 `CrcToPath` 时，才有明确 `targetCollisionCrc`。
- 如果 target `.pcb` 不在 path table 中，本轮不能构造有效 collision source。

## 多 BgPart 对比目的

对比多个 BgPart 可以观察：

- `.mdl` 与 `.pcb` 是否常常同名。
- 哪些 BgPart 是 mesh collider。
- 哪些 BgPart 是 analytic collider。
- 哪些 BgPart 没有 collision。
- 当前地图 path table 中是否已经包含 target `.pcb`。

Debug 输出格式：

```text
distance | colliderType | CollisionMeshPathCrc | AnalyticShapeDataCrc | mdl | pcb
```

## 当前判断

如果 target `.pcb` 已经在当前 path table 中：

- 可以得到 `targetCollisionCrc`。
- 但仍不能直接写 `CollisionMeshPathCrc`。
- 下一步必须研究 `SetProperties(FileLayerGroupInstanceBgPart*)` 或等价初始化逻辑如何同步：
  - `CollisionMeshPathCrc`
  - `AnalyticShapeDataCrc`
  - material mask/id
  - collider type / analytic data

如果 target `.pcb` 不在当前 path table 中：

- 当前没有可用 `targetCollisionCrc`。
- `CreateSecondary()` 无法通过外部参数接收 target path。
- FullLayout target collision 不能继续。

## 安全结论

本轮严格只读：

- 不调用 `CreateSecondary()`。
- 不调用 `DestroySecondary()`。
- 不写 `CollisionMeshPathCrc`。
- 不写 `AnalyticShapeDataCrc`。
- 不写 path table。
- 不接入批量/创建/删除/恢复流程。

FullLayoutWithCollision 的 target collision 仍未实现。当前新增的是判定工具：先知道 target collision source 是否存在、候选 CRC 是否可信，再决定下一轮是否能做单字段/单实例实验。

## 下一步

建议继续取证：

1. 反汇编 `SetProperties(FileLayerGroupInstanceBgPart*)`。
2. 确认它如何从 `PathPcb` 写入 `CollisionMeshPathCrc`。
3. 确认它如何从 `ColliderType` / analytic data 写入 `AnalyticShapeDataCrc`。
4. 研究是否存在 path string -> CRC / path table register helper。
5. 如果 target `.pcb` 已在 path table，设计但默认禁用的单实例实验：
   - 保存旧 collision source 字段；
   - 写 target collision source 字段；
   - `DestroySecondary -> CreateSecondary`；
   - readback collider/path；
   - 人工确认 collision 行为；
   - 提供恢复字段与切图恢复提示。
