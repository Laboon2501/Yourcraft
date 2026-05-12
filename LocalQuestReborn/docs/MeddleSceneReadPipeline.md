# Meddle 场景读取管线取证

源码位置：`C:\Users\kiomo\Documents\New project\.research\MeddleRetry`

## Meddle 如何枚举环境/地图模型

Meddle 的核心入口在 `.research\MeddleRetry\Meddle\Meddle.Plugin\Services\LayoutService.cs`。

`LayoutService.GetWorldState(searchOrigin)` 会组合三类来源：

1. `ParseObjects()`
   - 从 `GameObjectManager.Instance()->Objects.GameObjectIdSorted` 枚举当前 game objects。
   - 读取 `GameObject.DrawObject`。
   - 跳过 `DrawObject == null` 和 placeholder。
   - 用 `DrawObject.Position / Rotation / Scale` 作为 transform。

2. `LayoutWorld`
   - 通过 `LayoutWorld.Instance()` 读取当前 layout world。
   - 遍历 `layoutWorld->LoadedLayouts`。
   - 额外遍历 `layoutWorld->GlobalLayout`。
   - 对每个 `LayoutManager` 遍历 `Layers` 和 `Terrains`。

3. `Terrain`
   - `ParseTerrain()` 读取 `TerrainManager.PathString`。
   - `TerrainDebugTab` 进一步从 `TerrainManager.GfxTerrain` 读取 terrain model resource handles。

## LayoutEngine 读取链

取证文件：

- `.research\MeddleRetry\Meddle\Meddle.Plugin\Services\LayoutService.cs`
- `.research\MeddleRetry\Meddle\Meddle.Plugin\UI\Layout\LayoutWindow.cs`
- `.research\MeddleRetry\Meddle\Meddle.Plugin\UI\Layout\Instance.cs`

调用链：

```text
LayoutWindow.Draw()
  -> SetupCurrentState()
    -> framework.RunOnTick(...)
      -> LayoutService.UpdateState(searchOrigin, requestedAt)
        -> LayoutService.GetWorldState(searchOrigin)
          -> ParseObjects()
          -> ParseLayout(layoutWorld->LoadedLayouts)
          -> ParseLayout(layoutWorld->GlobalLayout)
            -> ParseLayer(layerPtr)
              -> ParseInstance(instancePtr)
                -> ParseBgPart(BgPartsLayoutInstance*)
                -> ParseSharedGroup(SharedGroupLayoutInstance*)
                -> ParsedLightInstance(...)
                -> ParseDecalInstance(...)
          -> ParseTerrain(TerrainManager*)
```

`ParseInstance()` 根据 `ILayoutInstance.Id.Type` 分发：

- `InstanceType.BgPart`
- `InstanceType.SharedGroup`
- `InstanceType.Light`
- `InstanceType.Decal`
- unsupported fallback

## DrawObject / ResourceHandle / model path 来源

### 普通 GameObject / Character 路线

`ParseObjects()`：

- 从 `GameObjectManager.Instance()->Objects.GameObjectIdSorted` 取 `GameObject*`。
- 读取 `obj->DrawObject`。
- transform 来自 `drawObject->Position / Rotation / Scale`。
- character mesh/material 不是直接在 `ParseObjects()` 里解析，而是后续由 `ResolverService.ResolveParsedCharacterInstance()` 调 `ParseMaterialUtil.ParseDrawObject(...)`。

相关文件：

- `.research\MeddleRetry\Meddle\Meddle.Plugin\Services\LayoutService.cs`
- `.research\MeddleRetry\Meddle\Meddle.Plugin\Services\ResolverService.cs`
- `.research\MeddleRetry\Meddle\Meddle.Plugin\Utils\ParseMaterialUtil.cs`

### BgPart / 地图物件路线

`ParseBgPart(BgPartsLayoutInstance*)`：

- 将 `bgPart->GraphicsObject` cast 为 Meddle 自定义结构 `BgObject*`。
- `BgObject` 在 `.research\MeddleRetry\Meddle\Meddle.Plugin\Models\Structs\BgObject.cs` 中定义：
  - offset `0x00` 是 `FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject Base`
  - offset `0x90` 是 `ModelResourceHandle* ModelResourceHandle`
  - offset `0xA8` 是 bg change material 数据
- 如果 `graphics->ModelResourceHandle->LoadState < 7`，Meddle 跳过。
- `bgPart->GetPrimaryPath()` 给出主要模型路径。
- `graphics->ModelResourceHandle` 被保存为 `ParsedBgPartsInstance.ModelPtr`，用于 debug material window 和 mesh cache。

### Terrain 路线

`TerrainDebugTab`：

- `LayoutWorld.Instance()->ActiveLayout->Terrains`
- `terrainPtr->GfxTerrain`
- cast 到 Meddle 自定义 `Terrain*`
- 读取 `ModelResourceHandlesSpan`
- 每个 `ModelResourceHandle->FileName.ParseString()` 是 terrain model path。

相关文件：

- `.research\MeddleRetry\Meddle\Meddle.Plugin\UI\TerrainDebugTab.cs`
- `.research\MeddleRetry\Meddle\Meddle.Plugin\Models\Structs\Terrain.cs`

## Meddle 是否读取 LayoutEngine

是。Meddle 明确读取：

- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.LayoutWorld`
- `LayoutManager`
- `LayerManager`
- `ILayoutInstance`
- `BgPartsLayoutInstance`
- `SharedGroupLayoutInstance`
- `TerrainManager`

这不是单纯从 render scene/draw list 导出。

## Meddle 是否也从 render scene / draw object 导出

是，但用途不同：

- `ParseObjects()` 使用 `GameObjectManager` 和 `DrawObject` 枚举角色、坐骑、宠物等可见 draw object。
- `ResolverService.ParseCharacter()` / `ParseMaterialUtil.ParseDrawObject()` 再从 draw object 解析 character models/materials/textures。
- 地图 `BgPart` 主要从 LayoutEngine 的 `BgPartsLayoutInstance` + `ModelResourceHandle` 获取路径和资源。

所以 Meddle 是混合管线：

- 当前可见角色/动态对象：`GameObjectManager + DrawObject`
- 静态地图/环境对象：`LayoutWorld + LayoutManager + ILayoutInstance`
- terrain：`TerrainManager + GfxTerrain + ModelResourceHandle`

## 可以借鉴到 LocalQuestReborn 的代码思路

可以借鉴：

- 使用 `GameObjectManager.Instance()->Objects.GameObjectIdSorted` 作为可见 draw object 的只读枚举入口。
- 使用 `drawObject->Position / Rotation / Scale`，比只读 `GameObject.Position` 更贴近渲染 transform。
- 使用 `LayoutWorld.Instance()` 作为真正地图 layout object 的入口。
- 对 `ILayoutInstance.Id.Type` 做分发，而不是只看 `GameObject.SharedGroupLayoutInstance`。
- 对 `BgPartsLayoutInstance` 使用 `GetPrimaryPath()` 读取模型路径。
- 对 `BgPartsLayoutInstance.GraphicsObject` 读取 `ModelResourceHandle`，再读 `FileName.ParseString()` 或 material handles。
- terrain 可从 `TerrainManager.PathString` 和 `GfxTerrain.ModelResourceHandles` 继续展开。

当前 LocalQuestReborn 已按 Meddle 的第一条路线改造：

- `MeddleStyleSceneProbeService` 优先使用 `GameObjectManager.Instance()->Objects.GameObjectIdSorted`。
- 只读 `GameObject.DrawObject`。
- 显示 draw object type、position、rotation、scale、visible、readyToDraw、distance。

## 只能用于导出，不能直接用于生成的部分

Meddle 的以下部分是导出管线，不等价于 runtime 生成：

- `InstanceComposer.Compose(...)`
- `ComposeBgPartsInstance(...)`
- `ComposeTerrain(...)`
- `ModelBuilder.BuildMeshes(...)`
- `ComposerCache.ComposeMaterial(...)`
- `SqPack.GetFileOrReadFromDisk(...)`

这些逻辑把游戏资源读出并构建 glTF/OBJ 场景，不会在游戏里创建新的 layout object，也不会向 LayoutEngine 注册对象。

## LocalQuestReborn 下一步

1. 把现有 `LayoutProbeService` 从 `ObjectTable.SharedGroupLayoutInstance` 升级为 `LayoutWorld -> LoadedLayouts/GlobalLayout -> Layers/Terrains`。
2. 对 `BgPartsLayoutInstance` 读取：
   - `GetPrimaryPath()`
   - `GetTransformImpl()`
   - `GraphicsObject`
   - `ModelResourceHandle`
3. 对 terrain 读取：
   - `TerrainManager.PathString`
   - `GfxTerrain`
   - `ModelResourceHandlesSpan`
4. 在 UI 中分开显示：
   - Meddle-style visible draw objects
   - LayoutEngine bgparts/shared groups
   - Terrain model handles

本轮仍保持只读，不生成 object，不写 native。
