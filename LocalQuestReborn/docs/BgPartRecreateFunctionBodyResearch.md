# BgPart recreate 函数体级取证

日期：2026-05-13

本轮目标是函数体级取证，确认 BgPart recreate 是否存在可控最小实验路径。没有新增按钮，没有调用 `CreatePrimary`、`DestroyPrimary`、`SetGraphics`、`BgObject.Create`，也没有恢复 `CleanupRender`。

## 版本匹配说明

默认 `.research/FFXIVClientStructs/ida/data.yml` 与当前本机 `ffxiv_dx11.exe` 不匹配。按默认符号读 vtable 时，`CreatePrimary` / `SetGraphics` 指向了明显不对位的函数。

本机游戏：

```text
D:\ffxiv\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe
ImageBase = 0x140000000
```

匹配的符号文件：

```text
.research/FFXIVClientStructs/ida/old/data_2026.04.21.0000.0000.yml
```

对应地址：

```text
BgPartsLayoutInstance vtable = 0x142178288
SetGraphics           = 0x14073FBB0
CreatePrimary         = 0x14073FC00
DestroyPrimary        = 0x14073FCD0
BgObject.Create       = 0x14079A830
BgObject.SetModel     = 0x140452430
BgObject.ResetFlags   = 0x140452010
RenderManager.CreateModel = 0x1402B90F0
```

反汇编来源：`pefile` 解析 PE 映射，`capstone` 反汇编目标函数字节。

## BgPartsLayoutInstance.CreatePrimary

函数地址：

```text
0x14073FC00
```

关键反汇编：

```asm
14073FC0A  mov eax, dword ptr [rcx + 0x24]
14073FC0D  mov r9, r8
14073FC10  xor r8d, r8d
14073FC13  mov rdi, rdx
14073FC16  mov rbx, rcx
14073FC19  cmp eax, -1
14073FC1C  je  14073FC35
14073FC1E  mov rcx, qword ptr [rcx + 0x10]
14073FC27  imul r8, rax, 0xE0
14073FC2E  add r8, qword ptr [rcx + 0xC30]
14073FC35  mov rcx, qword ptr [r9]
14073FC38  lea rdx, [rip + 0x1A35DD9]
14073FC3F  call 14079A830 ; BgObject.Create
14073FC44  mov qword ptr [rbx + 0x30], rax
14073FC48  mov rdx, rdi
14073FC4B  mov rax, qword ptr [rbx]
14073FC4E  mov rcx, rbx
14073FC51  call qword ptr [rax + 0x238]
```

### pathOrType 真实解引用方式

Windows x64 调用约定下：

- `rcx = this`
- `rdx = transform`
- `r8 = pathOrType`

函数体立即执行：

```asm
mov r9, r8
...
mov rcx, qword ptr [r9]
```

结论：对 `BgPartsLayoutInstance.CreatePrimary` 来说，`pathOrType` 是“指向 path 指针的指针”，即 `byte** path` / `char** path` 形态。它不是直接的 `byte*`，也不是 `int* type`。

### modelGamePath 来源

`BgObject.Create` 的第一个参数 `rcx` 来自：

```asm
mov rcx, qword ptr [r9]
```

也就是：

```text
modelGamePath = *(char**)pathOrType
```

结合 `FileLayerGroupInstanceBgPart.PathMdl` 字段，可以判断 BgPart recreate 的模型路径来自 layout 文件中的 mdl path 指针，而不是当前 `BgObject.ModelResourceHandle.FileName`。

### poolName 来源

`BgObject.Create` 的第二个参数 `rdx` 来自：

```asm
lea rdx, [rip + 0x1A35DD9]
```

读取该地址得到字符串：

```text
Client.LayoutEngine.Layer.BgPartsLayoutInstance
```

结论：CreatePrimary 传给 `BgObject.Create` 的 poolName 是固定字符串 `Client.LayoutEngine.Layer.BgPartsLayoutInstance`。

### existingAllocation 来源

`BgObject.Create` 的第三个参数 `r8`：

```asm
xor r8d, r8d
cmp dword ptr [this + 0x24], -1
je  no_existing_allocation
mov rcx, qword ptr [this + 0x10]      ; LayoutManager*
imul r8, indexInPool, 0xE0            ; BgObject size
add r8, qword ptr [layout + 0xC30]    ; gfx bg object pool ptr
```

结论：

- 如果 `this->IndexInPool` (`this+0x24`) 为 `-1`，`existingAllocation = null`。
- 如果 `IndexInPool != -1`，`existingAllocation = LayoutManager + 0xC30 指向的 BgObject pool + IndexInPool * 0xE0`。
- 这不是当前 `this->GraphicsObject` 直接传回去，而是 layout 的 BgObject pool slot。

这说明 `existingAllocation` 是 layout pool 预分配槽位机制，不是热替换时随便可传 live pointer 的通用重建 API。

### 是否调用 BgObject.Create

确认调用：

```asm
14073FC3F call 14079A830 ; BgObject.Create
```

传参：

```text
rcx = *(char**)pathOrType
rdx = "Client.LayoutEngine.Layer.BgPartsLayoutInstance"
r8  = null 或 layout BgObject pool slot
```

### CreatePrimary 是否调用 SetGraphics

未调用 vfunc 25 `SetGraphics`。

实际顺序是：

```asm
call BgObject.Create
mov [this + 0x30], rax
call qword ptr [this->vtable + 0x238]
```

`0x238` 对应 `BgPartsLayoutInstance.SetTransformImpl`。也就是说 `CreatePrimary` 自己写 `GraphicsObject`，然后调用 `SetTransformImpl(transform)`，不是通过 `SetGraphics`。

### CreatePrimary 之后的 active 处理

CreatePrimary 后半段会根据 `Flags3`、LayoutManager 状态、Layer flags、`WantToBeActive` 等条件跳到 vfunc offset `0x1F8`，这在 `ffxiv_structs.yml` 中对应 `SetActive(bool active)`。

所以完整行为是：

```text
BgObject.Create(...)
this->GraphicsObject = result
SetTransformImpl(transform)
SetActive(true/false) 条件执行
```

## BgObject.Create

函数地址：

```text
0x14079A830
```

关键反汇编：

```asm
14079A83A  mov rbx, r8
14079A83D  mov rdi, rcx
14079A840  test r8, r8
14079A843  jne 14079A873
14079A845  mov rax, qword ptr [rip + 0x215C90C]
14079A850  mov edx, 0xE0
14079A855  mov rcx, qword ptr [rax + 0x30]
14079A85C  call qword ptr [rax + 0x10]
14079A867  call 140451F70
14079A873  mov rcx, rbx
14079A876  call 140452010 ; BgObject.ResetFlags
14079A87B  mov rcx, qword ptr [rip + 0x215C8EE]
14079A882  mov rdx, rbx
14079A885  call 14041E7C0
14079A88D  call 14041E8B0
14079A897  call 1402FE230
14079A8A4  call 1402FE2D0
14079A8AF  mov rcx, rbx
14079A8B2  call 140452430 ; BgObject.SetModel
14079A8B7  mov rax, rbx
```

### 参数使用

调用约定：

- `rcx = modelGamePath`
- `rdx = poolName`
- `r8 = existingAllocation`

函数体观察：

- `modelGamePath` 保存到 `rdi`，后续传给 string/resource path helper，再传给 `SetModel`。
- `existingAllocation` 保存到 `rbx`。
- 如果 `existingAllocation == null`，通过全局 allocator 分配 `0xE0` 字节，再调用 `0x140451F70` 初始化。
- `poolName` 在当前函数体中没有被直接使用；进入函数后 `rdx` 很快被覆盖为 allocator size /其它参数。

### 是否调用 RenderManager.CreateModel

在 `BgObject.Create` 函数体内，没有直接调用 `RenderManager.CreateModel (0x1402B90F0)`。

扫描到的直接 call：

```text
call [allocator vfunc + 0x10]
call 0x140451F70
call BgObject.ResetFlags
call 0x14041E7C0
call 0x14041E8B0
call 0x1402FE230
call 0x1402FE2D0
call BgObject.SetModel
```

`BgObject.SetModel` 当前函数体内也只观察到一个直接 call：

```text
call 0x1402FE8A0
```

因此本轮能确认：

- `BgObject.Create` 不直接调用 `RenderManager.CreateModel`。
- 可见 render mesh 的创建可能发生在 `SetModel` 内部后续资源回调、render update、或其它异步链中，而不是 `BgObject.Create` 直接同步调用 `RenderManager.CreateModel`。

### 是否注册 culling/render scene

函数体内没有看到明显的 `UpdateRender`、`UpdateCulling`、`OnAddedToWorld` 直接调用。

不过 `0x14041E7C0` / `0x14041E8B0` 的语义仍未命名，可能是 scene/object 管理相关辅助函数。当前只能确认 `BgObject.Create` 做了：

```text
分配或使用 existingAllocation
初始化/ResetFlags
若干 scene/resource helper
SetModel(modelGamePath)
返回 BgObject*
```

不能确认它单独完成 culling/render scene 注册。

## SetGraphics

函数地址：

```text
0x14073FBB0
```

完整关键反汇编：

```asm
14073FBB0  mov eax, dword ptr [r8]
14073FBB3  mov dword ptr [rdx + 0x50], eax
14073FBB6  mov eax, dword ptr [r8 + 4]
14073FBBA  mov dword ptr [rdx + 0x54], eax
14073FBBD  mov eax, dword ptr [r8 + 8]
14073FBC1  mov dword ptr [rdx + 0x58], eax
14073FBC4  mov qword ptr [rcx + 0x30], rdx
14073FBC8  movups xmm0, xmmword ptr [r8 + 0x10]
14073FBCD  movups xmmword ptr [rdx + 0x60], xmm0
14073FBD1  mov eax, dword ptr [r8 + 0x20]
14073FBD5  mov dword ptr [rdx + 0x70], eax
14073FBD8  mov eax, dword ptr [r8 + 0x24]
14073FBDC  mov dword ptr [rdx + 0x74], eax
14073FBDF  mov eax, dword ptr [r8 + 0x28]
14073FBE3  mov dword ptr [rdx + 0x78], eax
14073FBE6  or qword ptr [rdx + 0x38], 2
14073FBEB  ret
```

调用约定：

- `rcx = this`
- `rdx = Graphics.Scene.Object* obj`
- `r8 = Transform* transform`

### 是否写 this+0x30 GraphicsObject

确认写入：

```asm
mov qword ptr [rcx + 0x30], rdx
```

`this+0x30` 正是 `BgPartsLayoutInstance.GraphicsObject`。

### 是否同步 Object.Position/Rotation/Scale

确认同步。

`Transform` 布局：

```text
0x00 Translation
0x10 Rotation quaternion
0x20 Scale
```

`Graphics.Scene.Object` 布局：

```text
0x50 Position
0x60 Rotation
0x70 Scale
```

SetGraphics 逐项写入：

```text
obj+0x50 = transform+0x00 Translation
obj+0x60 = transform+0x10 Rotation
obj+0x70 = transform+0x20 Scale
```

### 是否调用 OnAddedToWorld / UpdateRender / culling

确认没有。

`SetGraphics` 内没有任何 call 指令。它只：

1. 写 Object.Position。
2. 写 this+0x30 GraphicsObject。
3. 写 Object.Rotation。
4. 写 Object.Scale。
5. `obj->ObjectFlags |= 2`。
6. 返回。

结论：`SetGraphics` 不是 render/culling 注册入口；它是一个非常小的绑定和 transform 同步 helper。

## DestroyPrimary

函数地址：

```text
0x14073FCD0
```

关键反汇编：

```asm
14073FCD9  mov rcx, qword ptr [rcx + 0x30]
14073FCDD  test rcx, rcx
14073FCE0  je  14073FD09
14073FCE2  mov rax, qword ptr [rcx]
14073FCE5  call qword ptr [rax + 8]
14073FCE8  cmp dword ptr [rbx + 0x24], -1
14073FCEC  jne 14073FD01
14073FCEE  mov rcx, qword ptr [rbx + 0x30]
14073FCF7  mov rax, qword ptr [rcx]
14073FCFA  mov edx, 1
14073FCFF  call qword ptr [rax]
14073FD01  mov qword ptr [rbx + 0x30], 0
```

### 是否释放 GraphicsObject

部分确认。

无论 pool slot 还是非 pool slot，只要 `GraphicsObject` 非空，都会先调用：

```text
obj->vfunc[1] = Object.CleanupRender
```

如果 `this->IndexInPool == -1`，还会调用：

```text
obj->Dtor(freeFlags = 1)
```

如果 `IndexInPool != -1`，不会调用 Dtor，只 CleanupRender，然后清空指针。这和 `CreatePrimary` 使用 layout pool slot 的逻辑一致。

### 是否 null 掉 this+0x30

确认：

```asm
mov qword ptr [rbx + 0x30], 0
```

### 是否影响 Collider

函数体内没有观察到对 `this+0x38 Collider` 的读取或写入，也没有调用 `DestroySecondary`。

结论：`DestroyPrimary` 只处理 primary graphics，不直接处理 collider。但它调用 `CleanupRender`，这正是之前实验中导致模型消失和后续 transform 写入不安全的高风险路径。

## 对 recreate 最小实验路径的判断

### 可以确认的真实创建链

函数体级证据支持以下链路：

```text
CreatePrimary(this, transform, pathOrType)
  -> modelGamePath = *(char**)pathOrType
  -> poolName = "Client.LayoutEngine.Layer.BgPartsLayoutInstance"
  -> existingAllocation =
       if this->IndexInPool != -1:
         this->Layout->BgObjectPool + this->IndexInPool * 0xE0
       else:
         null
  -> BgObject.Create(modelGamePath, poolName, existingAllocation)
     -> if existingAllocation == null: allocate 0xE0 and init
     -> ResetFlags
     -> SetModel(modelGamePath)
     -> return BgObject*
  -> this->GraphicsObject = returned BgObject*
  -> SetTransformImpl(transform)
  -> SetActive(...)
```

`SetGraphics` 是独立 helper，不在 `CreatePrimary` 中调用。

### 是否有可控最小实验路径？

有理论路径，但当前不建议直接做按钮。

理论上，如果要 recreate 当前 BgPart，应当接近游戏自己的链：

```text
DestroyPrimary()
CreatePrimary(currentTransform, &pathPointer)
```

或避免 `DestroyPrimary`，尝试：

```text
CreatePrimary(currentTransform, &newPathPointer)
```

但实际风险很高：

- `DestroyPrimary` 必定调用 `CleanupRender`，已知这会让当前实例 transform 写入不安全。
- `CreatePrimary` 使用 `IndexInPool` 和 `Layout+0xC30` 的 pool slot，误用可能覆盖或重置 live pool 对象。
- `pathOrType` 必须是稳定生命周期的 `char**`，不是临时 `byte*`。
- `CreatePrimary` 调用后会执行 `SetTransformImpl` 和 `SetActive`，可能同步 collision/active 状态。
- 如果目标 path 不是原 layout 文件里的 path，需要保证字符串内存生命周期、资源 category、layout/Rsv/Rsf 解析一致。

因此当前最小安全结论是：

1. `SetGraphics` 已确认安全性更高但能力有限：它只绑定 object 和写 transform，不重建 mesh。
2. `BgObject.Create(existingAllocation)` 已确认是创建链的一部分，但不能脱离 `CreatePrimary` 单独使用。
3. `DestroyPrimary -> CreatePrimary` 是最接近真实 recreate 的路径，但因为 `DestroyPrimary` 必定调用 `CleanupRender`，目前应继续禁用，直到找到可恢复/可验证的保护策略。
4. 如果后续做实验，必须单实例、UnsafeMode、二次确认、先保存 path/transform/GraphicsObject/pool index，并且不能接入 RestoreAll/Create/Delete 自动流程。

## 与当前 SetModel 失败的关系

`SetModel` 只在 `BgObject.Create` 中被调用，用来设置 resource handle。可见 mesh 的真正绑定很可能依赖 `CreatePrimary` 后续的 `SetTransformImpl` / `SetActive` / render update 时机，而不是 `SetModel` 本身。

这解释了当前现象：

- 单独 `SetModel` 返回 true。
- `ModelResourceHandle.FileName` 变化。
- 但 visible mesh 不变。

因为没有重新走 `CreatePrimary` 的 pool slot + object lifecycle + active/update 链。

## 结论

函数体级证据已经确认：

- `pathOrType` 对 BgPart 是 `char**` / `byte**`，函数体用 `mov rcx, [pathOrType]` 解引用。
- `CreatePrimary` 确认调用 `BgObject.Create`。
- `modelGamePath` 来自 `*pathOrType`。
- `poolName` 是固定字符串 `Client.LayoutEngine.Layer.BgPartsLayoutInstance`。
- `existingAllocation` 来自 `LayoutManager + 0xC30` 的 BgObject pool slot，索引是 `this->IndexInPool`。
- `SetGraphics` 确认写 `this+0x30 GraphicsObject`，同步 Object Position/Rotation/Scale，不调用 render/culling。
- `DestroyPrimary` 确认调用 `CleanupRender`，必要时调用 Dtor，并 null 掉 `this+0x30`，不直接处理 collider。
- `BgObject.Create` 函数体内不直接调用 `RenderManager.CreateModel`，而是调用 `SetModel` 和若干未命名 helper。

当前仍不应新增 recreate 写入按钮。下一步如果要进入实验，必须围绕 `DestroyPrimary -> CreatePrimary(currentTransform, &pathPointer)` 做单实例高风险原型，并先解决 CleanupRender 后实例失效保护与恢复策略。
