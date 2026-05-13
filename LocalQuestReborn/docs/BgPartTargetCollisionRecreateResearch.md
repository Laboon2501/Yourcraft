# BgPart target collision recreate 取证

版本：v8.4  
范围：只读取证，不新增写入按钮，不调用 `CreateSecondary` / `DestroySecondary`。

## 目标

当前已经确认：

- `DestroyPrimary -> CreatePrimary` 可以让 `BgPart` 的可见模型切到 target `.mdl`。
- `VisualOnly` recreate 已修正：只重建/移动 `GraphicsObject`，不把碰撞带到玩家位置。
- 需要研究 `FullLayoutWithCollision`：模型替换后，collision 是否能跟随 target 模型，而不是继续使用旧 slot 的 collider。

本轮结论先说清楚：`CreateSecondary()` 不是 `CreateSecondary(transform, path)` 这种可传路径的入口。它没有外部参数，碰撞路径来自 `BgPartsLayoutInstance` 内部字段，尤其是 `CollisionMeshPathCrc` / `AnalyticShapeDataCrc`，再通过 layout path table 解析。因此“直接传 target pcb path 让它重建碰撞”这条路目前不成立。

## 结构字段证据

来源：

- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Layer/BgPartsLayoutInstance.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/FileFormat.cs`
- `.research/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/LayoutEngine/ILayoutInstance.cs`
- `.research/FFXIVClientStructs/ida/data.yml`
- 当前本机客户端函数体反汇编。地址随客户端版本变化，后续版本需要重新验证。

`BgPartsLayoutInstance` 的关键字段：

```csharp
GraphicsObject             // offset 0x30, BgObject*
Collider                   // offset 0x38, Collider*
CollisionMeshPathCrc       // offset 0x40
AnalyticShapeDataCrc       // offset 0x44
CollisionMaterialIdLow     // offset 0x48
CollisionMaterialMaskLow   // offset 0x4C
CollisionMaterialIdHigh    // offset 0x50
CollisionMaterialMaskHigh  // offset 0x54
CollisionUpdateListener    // offset 0x60
```

`FileLayerGroupInstanceBgPart` 的关键字段：

```csharp
OffsetPathMdl              // offset 0x30
OffsetPathPcb              // offset 0x34
ColliderType               // offset 0x38, None / Mesh / Analytic
MaterialMaskLow            // offset 0x3C
MaterialIdLow              // offset 0x40
MaterialMaskHigh           // offset 0x44
MaterialIdHigh             // offset 0x48
OffsetColliderAnalyticData // offset 0x4C
PathMdl                    // OffsetPathMdl > 0 时解析
PathPcb                    // OffsetPathPcb > 0 时解析
ColliderAnalyticData       // OffsetColliderAnalyticData > 0 时解析
```

`ILayoutInstance` 的 virtual function 表显示：

```csharp
CreatePrimary(Transform* transform, void* pathOrType)
DestroyPrimary()
IsColliderLoaded()
GetSecondaryPath()
CreateSecondary()
DestroySecondary()
GetCollider()
SetColliderActive(bool active)
UpdateCollider()
```

注意：`CreateSecondary()` 没有路径参数，也没有 transform 参数。

## 函数体级取证

当前本机客户端 `BgPartsLayoutInstance` vtable：

- `GetSecondaryPath`：`0x14073FD20`
- `CreateSecondary`：`0x140740050`
- `DestroySecondary`：`0x140740340`
- `GetCollider`：`0x14076A2A0`
- `SetColliderActive`：`0x1407403B0`
- `UpdateCollider`：`0x1407403D0`

### GetSecondaryPath

函数体显示：

```asm
cmp dword ptr [rcx+0x40], 0       ; CollisionMeshPathCrc
lea rdx, [rcx+0x40]
je  empty
mov rcx, [rcx+0x10]               ; layout / owner
add rcx, 0x278                    ; path table
jmp 0x140319830                   ; resolve CRC -> path
```

结论：

- secondary path 从 `this+0x40 CollisionMeshPathCrc` 解析。
- 解析依赖 `this+0x10` 指向的 layout owner/path table。
- 它不是从当前 `ModelResourceHandle.FileName` 推导。
- 它也不是直接把 `.mdl` 替换成 `.pcb` 后读取。

### CreateSecondary

函数体入口先检查：

```asm
cmp [rcx+0x38], 0           ; Collider 已存在则返回
test byte ptr [rcx+0x2a], 1 ; instance flag gate
mov rcx, [rcx+0x10]         ; layout / owner 必须存在
mov edx, [rdi+0x44]         ; AnalyticShapeDataCrc
test edx, edx
je mesh_path_branch
```

也就是说 `CreateSecondary` 有两条路线：

1. `AnalyticShapeDataCrc != 0`：走 analytic collider。
2. `AnalyticShapeDataCrc == 0`：走 mesh/pcb collider。

#### Analytic collider 分支

观察到的行为：

- 用 layout owner + `AnalyticShapeDataCrc` 查 analytic collider data。
- 调用 transform/bounds helper 计算 collider transform。
- 根据 analytic data 的 collider type 调不同创建函数：
  - type 1：`AddColliderBox`
  - type 2：`AddColliderSphere`
  - type 3：`AddColliderMeshCylinder`
  - type 4：`AddColliderPlane`
- 创建成功后写：

```asm
mov [this+0x38], rax ; Collider
```

#### Mesh / pcb 分支

当 analytic data 不存在时：

```asm
cmp dword ptr [rdi+0x40], 0       ; CollisionMeshPathCrc
lea rdx, [rdi+0x40]
je  no_collision
mov rcx, [rdi+0x10]
add rcx, 0x278
call 0x140319830                 ; resolve CollisionMeshPathCrc -> pcb path
...
call 0x1405F3410                 ; AddColliderMesh
mov [rdi+0x38], rax              ; Collider
```

结论：

- mesh collider 使用 `CollisionMeshPathCrc`，不是外部传入 path。
- path 实际来自 layout path table。
- `PathPcb` 在文件数据层存在，但运行态 `CreateSecondary` 使用的是 CRC / path table 解析结果。

#### Collider 注册和 listener

公共收尾逻辑会对创建出来的 collider 做初始化：

```asm
mov rdx, [rdi+0x38]
[collider+0x58] = (this+0x20 << 32) | [this+0x1c]
[collider+0x50] = 0x3000
vcall [collider+0x38] with material masks / ids
SetColliderActive(flag from [this+0x2b])
if CollisionUpdateListener at this+0x60:
    register listener with collider+0x88
```

结论：

- `CreateSecondary` 会写 `this+0x38 Collider`。
- 会注册/绑定 `CollisionUpdateListener`。
- 会使用当前 instance 的 material id/mask 字段。
- 会根据 instance flag 设置 collider active。
- 会使用当前 layout transform / helper，不接收独立 transform 参数。

### DestroySecondary

函数体显示：

```asm
mov rcx, [this+0x38]
if null return

mov rax, [this+0x60]        ; CollisionUpdateListener
if listener:
    unregister listener path

call 0x142062440(collider, 0)
call 0x1420625E0(collider, 0)

rcx = global collision manager + 0x2b58
rdx = [this+0x38]
call 0x1405F3340            ; RemoveCollider

mov [this+0x38], 0
```

结论：

- `DestroySecondary` 会移除/注销当前 collider。
- 会清空 `this+0x38 Collider`。
- 如果有 `CollisionUpdateListener`，会先走 listener 解绑路径。
- 它不处理 primary graphics。

## PathPcb 与 target pcb 推断

文件数据层有：

- `PathMdl`
- `PathPcb`
- `ColliderType`
- analytic collider data

运行态 `GetSecondaryPath` / `CreateSecondary` 使用：

- `CollisionMeshPathCrc`
- `AnalyticShapeDataCrc`
- layout owner 的 path table

因此 target collision 不是简单：

```text
bg/.../xxx.mdl -> bg/.../xxx.pcb
```

虽然资源命名上常见 `.mdl` 与 `.pcb` 同名同目录，但这只是一条可能规则，不能作为安全调用依据。真正 recreate collider 需要满足至少一个条件：

1. target `.pcb` 已经存在于当前 layout path table，并能得到合法 `CollisionMeshPathCrc`。
2. 或者能安全修改/重建 path table，让 `CollisionMeshPathCrc` 指向 target `.pcb`。
3. 或者 target 使用 analytic collider，需要对应 `AnalyticShapeDataCrc` 和 analytic data entry。

目前没有证据表明可以把任意 target `.pcb` 字符串直接传给 `CreateSecondary`。

如果 target 没有 pcb：

- `CollisionMeshPathCrc == 0` 时 mesh collider 分支不会创建 collider。
- 如果也没有 analytic data，则 `CreateSecondary` 应该保持 `Collider == null`。
- UI 应该显示“目标模型没有可确认 collision，无法同步碰撞”，而不是假装成功。

## FullLayoutWithCollision 最小实验设计

本轮不执行，只设计。默认禁用，必须 `UnsafeMode=true` + 二次确认。

### 前置只读验证

对当前 slot 读取：

- `Collider` 地址
- `CollisionMeshPathCrc`
- `AnalyticShapeDataCrc`
- `CollisionMaterialIdLow/High`
- `CollisionMaterialMaskLow/High`
- `CollisionUpdateListener`
- `GetSecondaryPath()` 结果
- `IsColliderLoaded()`
- `IsColliderActive()`
- 当前 layout transform

对 target path 需要额外确认：

- 是否能推断 target pcb path。
- target pcb 是否真实存在。
- target pcb 是否已在当前 layout path table 中。
- 是否能得到 target 对应 CRC。

### 可能实验流程

只有在能确认 target collision source 时，才考虑：

```text
1. 保存 current transform / primary / secondary 快照
2. DestroyPrimary()
3. DestroySecondary()
4. CreatePrimary(currentTransform, &targetMdlPathPointer)
5. 准备 target collision 数据
   - 如果只能沿用旧 CollisionMeshPathCrc：这不是 target collision
   - 如果能设置 target CollisionMeshPathCrc：才可能继续
6. CreateSecondary()
7. readback GraphicsObject / Collider / transform / secondary path
```

重要修正：`CreateSecondary` 没有 `currentTransform` 和 `targetPcbPath` 参数。它只能读取 instance 内部状态。因此流程中的第 5 步才是真正难点。

## 风险点

- `DestroySecondary` 会真实移除 collider，失败后可能只剩 visual 模型。
- `CreateSecondary` 会注册 collision listener，错误数据可能破坏 collision manager 状态。
- 任意写 `CollisionMeshPathCrc` / path table 都属于高风险，当前没有安全证据。
- 复制/伪造 `CollisionUpdateListener` 不安全，不能盲写。
- 使用旧 slot 的 `CollisionMeshPathCrc` 重建，只会重建旧 collider，不会得到 target collider。
- 对 VisualOnly 路线不能调用 `CreateSecondary`，否则会违背“不带碰撞”的目标。

## 当前结论

1. `CreateSecondary` 会创建 collider，并写入 `BgPartsLayoutInstance.Collider`。
2. `DestroySecondary` 会移除 collider，并清空 `Collider`。
3. `CreateSecondary` 没有 path 参数，也没有 transform 参数。
4. mesh collider path 来自 `CollisionMeshPathCrc -> layout path table`。
5. `PathPcb` 是文件数据层字段，运行态很可能在初始化/SetProperties 阶段被转换成 `CollisionMeshPathCrc`。
6. target `.mdl` 对应的 target `.pcb` 不能只靠字符串规则直接交给 `CreateSecondary`。
7. FullLayout target collision 的下一步不是调用 `CreateSecondary(targetPath)`，而是研究如何安全设置/准备 target collision source：
   - target pcb path 是否存在；
   - target path 是否在 path table；
   - target `CollisionMeshPathCrc` 如何得到；
   - analytic collider 是否可用；
   - `SetProperties(FileLayerGroupInstanceBgPart*)` 是否能安全更新这些字段。

## 建议下一步

1. 只读 dump 当前 slot 的 `GetSecondaryPath()`、`CollisionMeshPathCrc`、`PathPcb`、`ColliderType` 对应关系。
2. 搜索/反汇编 `SetProperties(FileLayerGroupInstanceBgPart*)`，确认它如何把 `PathPcb` 写成 `CollisionMeshPathCrc`。
3. 研究 layout owner `+0x278` path table 的结构，只读确认 CRC 到 path 的映射。
4. 确认是否存在公开/半公开 helper 可把 path string 注册到 path table 并返回 CRC。
5. 在没有 path table 写入证据前，FullLayout target collision recreate 只能标记为“外观可替换，target collision 未实现”。
