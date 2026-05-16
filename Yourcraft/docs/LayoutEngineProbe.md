# LayoutEngine / LayoutInstance 只读取证

## 本轮目标

停止 Brio Prop 路线，改为研究 FFXIV 原生 LayoutEngine。本轮只读取当前地图已有 layout object / bgpart / scene object，不生成、不写入。

## 当前能访问哪些 manager

通过 `FFXIVClientStructs.xml` 与运行时 reflection 可以看到这些 LayoutEngine 相关类型：

- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.ILayoutInstance`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.FileLayerGroupInstance`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.FileLayerGroupInstanceBgPart`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.FileLayerGroupInstanceSharedGroup`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group.SharedGroupLayoutInstance`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.*LayoutInstance`
- `FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.LayerManager`

当前 UI 的 `Dump 当前 LayoutManager` 会列出运行时可见的 LayoutEngine 类型与 public 成员。manager 指针本轮还没有直接解引用，只做类型/成员取证。

## 当前能枚举哪些 instance

本轮实现的稳定只读枚举路径是：

```text
Dalamud IObjectTable
  -> IGameObject.Address
  -> FFXIVClientStructs GameObject*
  -> GameObject.SharedGroupLayoutInstance
```

也就是说，目前能枚举的是 ObjectTable 里已经对应到 GameObject 的 layout/shared group instance，而不是完整地图所有 bgparts。

UI 中“枚举当前地图 layout instances”会显示前 100 个带 `SharedGroupLayoutInstance` 指针的对象。

## 当前可读字段

已读字段：

- index
- key: ObjectIndex + DataId
- type: Dalamud ObjectKind
- layout instance address: `GameObject.SharedGroupLayoutInstance`
- position: `GameObject.Position`
- rotation: `GameObject.Rotation`
- distance to player
- native GameObject debug:
  - GameObject address
  - ObjectKind
  - BaseId
  - `SharedGroupLayoutInstance` offset

支持过滤：

- 按距离排序
- 只显示玩家附近 50m 内
- 按 type / key / debug 文本过滤

## 暂时读不到或未完全解析

暂未稳定读取：

- 完整 `LayoutManager` 实例指针
- 完整 `LayerManager` 链表/数组
- 全地图所有 `ILayoutInstance`
- `ILayoutInstance.GetPrimaryPath()`
- `ILayoutInstance.GetSecondaryPath()`
- `ILayoutInstance.GetTranslation/GetRotation/GetScale`
- layer id / group id 的 native 解引用
- bgpart resource/model path

原因：这些字段需要进一步确认 manager 容器结构与 `ILayoutInstance*` 的安全枚举入口。为了避免 AccessViolation，本轮不遍历未知 native 容器。

## 下一步移动 instance 需要哪些字段

移动 layout instance 前至少需要确认：

1. `ILayoutInstance*` 的真实地址与生命周期。
2. instance 所在 `LayerManager*` / `LayoutManager*`。
3. `ILayoutInstance.GetTransform(Transform*)` 返回的 Transform 布局。
4. `ILayoutInstance.SetTransform(Transform*)` 是否会同步 graphics/collision。
5. instance 是否有 primary graphics object，以及移动后是否需要更新 collider。
6. 对 shared group / bgpart / event object 是否有不同更新路径。

下一步建议先做纯读取：

- 对 `SharedGroupLayoutInstance` 指针调用 `GetTranslation/GetRotation/GetScale/GetPrimaryPath`。
- 如果稳定，再做 UnsafeMode=true 的单实例 `SetTransform` 实验。
