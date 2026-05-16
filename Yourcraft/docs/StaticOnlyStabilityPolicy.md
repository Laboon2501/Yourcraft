# Static-Only Stability Policy

Yourcraft v9.8 暂停动态 BgPart / SharedGroup / controller-driven 物体实验。

## 当前支持

- 静态 `bg/.../*.mdl`
- 静态 `bgcommon/.../*.mdl`
- VisualOnly 静态摆放：写 `Graphics.Scene.Object` 的 Position / Rotation / Scale，不移动 collision。
- FullLayoutWithCollision 静态摆放：写 `LayoutInstance` transform，模型和 collision 一起变化。
- 批量静态实例。
- 单实例 / 批量实例恢复、删除、恢复全部。
- custom mdl path 通过 `DestroyPrimary -> CreatePrimary`，不使用 `SetModel` 直接调用。

## 当前拒绝

以下对象会被视为动态或高风险 source / carrier / target，并在创建或应用 mdl path 时拒绝：

- SharedGroup child
- VisibilityCycling / 多 child 轮换类对象
- transform 每帧变化或被 runtime/controller 覆盖的对象
- `ControlledByRuntime`
- `UnsafeComplexModel`
- 路径包含 `/vfx/`
- 路径包含 `/light/`
- 路径包含 `/shared/`
- 路径包含 `/evt/`
- 路径包含 `/aet/`
- 城镇动态屏幕、广告牌、monitor/screen/scr/ad 类资源

UI 提示为：

> 该物体疑似由地图 controller / SharedGroup / 动态材质驱动，当前版本不支持本地移动。为避免闪退已阻止创建。

## 原因

动态 BgPart 可能依赖地图 controller、SharedGroup visible cycling、材质动画、event update listener 或 layout 初始化上下文。此前实验表明这些对象可能覆盖本地 transform，或在恢复、清理、重建时造成 native 崩溃。

## 恢复策略

`RestoreAll` 会先执行动画残留清理，然后仅对静态本地实例按原始 slot 快照恢复：

- original mdl
- original collision source
- original layout transform
- original graphics transform
- original visible

如果某个实例 render 已失效，恢复流程不会继续写坏指针，而是标记失败并提示切图/重载地图恢复。
