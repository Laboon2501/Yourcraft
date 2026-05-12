# Meddle Layout 列表管线取证

源码位置：`C:\Users\kiomo\Documents\New project\.research\MeddleRetry`

## Meddle 从哪里拿 instance 列表

Meddle 的 Layout 页由 `LayoutWindow.Draw()` 驱动：

```text
Meddle.Plugin/UI/Layout/LayoutWindow.cs
  Draw()
    -> SetupCurrentState()
      -> framework.RunOnTick(...)
        -> LayoutService.UpdateState(searchOrigin, requestedAt)
          -> LayoutService.GetWorldState(searchOrigin)
```

`LayoutService.GetWorldState()` 在 `Meddle.Plugin/Services/LayoutService.cs` 中组合：

- `ParseObjects()`：角色、坐骑、宠物等 draw object。
- `layoutWorld->LoadedLayouts`：当前加载的 layout。
- `layoutWorld->GlobalLayout`：全局 layout。
- `ParseLayout()`：对每个 layout 解析 layers 和 terrains。

## instance 类型是什么

Meddle 自己定义了 `ParsedInstance` 抽象模型，文件：

```text
Meddle.Plugin/Models/Layout/ParsedInstance.cs
```

主要类型：

- `ParsedBgPartsInstance`
- `ParsedSharedInstance`
- `ParsedLightInstance`
- `ParsedTerrainInstance`
- `ParsedCameraInstance`
- `ParsedCharacterInstance`
- `ParsedWorldDecalInstance`
- `ParsedUnsupportedInstance`

Layout 页列表的标题在 `Meddle.Plugin/UI/Layout/Instance.cs`：

```text
ParsedBgPartsInstance => $"{bgObject.Type} - {bgObject.Path.GamePath} - {(bgObject.IsVisible ? "Visible" : "Hidden")}"
ParsedSharedInstance => $"{sharedInstance.Type} - {sharedInstance.Name}"
ParsedCharacterInstance => $"{character.Type} - {character.Kind}"
```

距离显示同样在 `Instance.cs`：

```text
var distance = Vector3.Distance(instance.Transform.Translation, searchOrigin);
TreeNode($"[{distance:F1}y] ...")
```

## 如何获取 BgPart 的 mdl path

调用链：

```text
LayoutService.ParseLayout(LayoutManager*)
  -> ParseLayer(LayerManager*)
    -> ParseInstance(ILayoutInstance*)
      -> case InstanceType.BgPart
        -> ParseBgPart(BgPartsLayoutInstance*)
```

`ParseBgPart()` 做法：

- 将 `ILayoutInstance*` cast 为 `BgPartsLayoutInstance*`。
- 使用 `bgPart->GetPrimaryPath()` 获取 mdl path。
- 使用 `bgPart->GetTransformImpl()` 获取 transform。
- 使用 `bgPart->GraphicsObject->IsVisible` 判断 visible。
- 将 `bgPart->GraphicsObject` cast 到 Meddle 自定义 `BgObject*`，从 offset `0x90` 读取 `ModelResourceHandle*`。

Meddle 的 `BgObject` 定义在：

```text
Meddle.Plugin/Models/Structs/BgObject.cs
```

关键字段：

```text
[FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
```

## 如何获取 SharedGroup 的 sgb path

调用链：

```text
ParseInstance(ILayoutInstance*)
  -> case InstanceType.SharedGroup
    -> ParseSharedGroup(SharedGroupLayoutInstance*)
```

`ParseSharedGroup()` 做法：

- 使用 `sharedGroup->GetPrimaryPath()` 获取 sgb path。
- 使用 `sharedGroup->GetTransformImpl()` 获取 transform。
- 遍历 `sharedGroup->Instances.Instances`，递归解析子 instance。

LocalQuestReborn 本轮先显示 SharedGroup 自身，不递归展开子节点。

## 如何获取距离玩家

Meddle 在 `LayoutWindow.SetupCurrentState()` 中维护 `searchOrigin`：

- 默认可以是玩家位置。
- 也可以是 camera 或 origin，取决于 LayoutConfig。

列表显示时：

```text
Vector3.Distance(instance.Transform.Translation, searchOrigin)
```

LocalQuestReborn 本轮使用 `QuestRuntimeService.PlayerPosition` 作为 search origin。

## 如何判断 Visible

Meddle 对不同类型的 visible 来源不同：

- `BgPart`：`bgPart->GraphicsObject->IsVisible`
- `Character`：`drawObject->IsVisible`
- `Light/Terrain/Camera/SharedGroup`：没有同样的 visible 标志，Meddle 主要作为可解析 instance 显示。

LocalQuestReborn 本轮：

- `BgPart` 显示 `Visible/Hidden`
- `Character` 可选显示 `DrawObject.IsVisible`
- 其他类型默认 `Visible`

## LocalQuestReborn 应该复用的思路

本轮已采用：

- `LayoutWorld.Instance()`
- `layoutWorld->LoadedLayouts`
- `layoutWorld->GlobalLayout`
- `LayoutManager.Layers`
- `LayerManager.Instances`
- `ILayoutInstance.Id.Type`
- `BgPartsLayoutInstance.GetPrimaryPath()`
- `SharedGroupLayoutInstance.GetPrimaryPath()`
- `ILayoutInstance.GetTransformImpl()`
- `BgPart.GraphicsObject.IsVisible`

明确不再把 EventNPC/ObjectTable 当作 Layout 列表来源。

## 当前限制

- LocalQuestReborn 暂未递归展开 SharedGroup 子节点。
- Terrain 目前显示 `TerrainManager.PathString`，还未展开 `GfxTerrain.ModelResourceHandlesSpan`。
- Character 默认关闭，只作为可选项，避免 EventNPC/GameObject 列表污染 Layout 视图。
- 本轮只读，不生成，不写 native。
