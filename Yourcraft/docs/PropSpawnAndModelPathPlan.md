# Prop Spawn 与 modelPath 实验计划

## 取证结论

1. **Brio 可以生成 Prop 形态的本地对象。**
   Brio 的 `ActorSpawnService` 中存在 `SpawnFlags.IsProp` 与组合值 `SpawnFlags.Prop`。当前 Brio 3.0 源码里还有 `SpawnNewProp(out ICharacter? gamechara)`，内部调用：

   `CreateCharacter(out ICharacter? chara, SpawnFlags.IsProp | SpawnFlags.CopyPosition, true)`

   然后通过 Brio 的实体系统把这个 actor 标记为 `ActorType.Prop`。

2. **SpawnFlags.Prop / IsProp 的用途。**
   `ActorEntity.GetActorType()` 会读取 ActorSpawnService 记录的 spawn flags：

   - 包含 `IsProp` 时，Brio UI / capability 认为它是 `ActorType.Prop`。
   - `Prop` 是组合值，等价于 `IsProp | SetDefaultAppearance | CopyPosition`。
   - Prop 仍然是由 `CreateCharacter` 得到的 `ICharacter`，不是原生地图 `BG` / `bgparts` object。

3. **生成出来的是 Character 包装的 Prop，不是真正地图物件。**
   Brio 仍然用 `ClientObjectManager.CreateBattleCharacter` 创建对象，返回 `ICharacter`。Prop 行为来自 Brio capability 和外观/武器槽处理，不是客户端原生场景物件系统。

4. **当前没有发现可安全直接传入任意 `.mdl` path 的 Brio public API。**
   Brio 的 Prop 外观路径主要是 `ActorAppearanceCapability.SetProp(WeaponModelId modelId)`，也就是通过 `WeaponModelId` 写主手/Prop 槽，再 redraw/attach weapon。

5. **`bg/.../xxx.mdl` 不能直接等同于 runtime model id。**
   示例路径 `bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl` 是资源路径。要在运行时显示它，通常需要：

   - Penumbra temporary resource replacement，把某个已知装备/Prop 模型替换成该 `.mdl`；
   - 或找到游戏内部 `DrawObject` / `ResourceHandle` 的安全 load API；
   - 或通过 Brio/AnamCore 暴露的模型 capability，把路径转换成它能识别的 model id。

6. **AQuestReborn 没有独立 map object / BG prop spawn 路线。**
   AQuestReborn 的本地 NPC 生成路线仍然围绕 Brio `ActorSpawnService.CreateCharacter` 和 actor appearance/capability。

## 本轮实现

- 新增 `CustomProp`：保存 Prop 配置、坐标、旋转、缩放、modelPath。
- 新增 `RuntimePropInstance`：保存运行态 Brio 对象、objectIndex、address、drawObjectAddress、dump 信息。
- 新增 `PropRuntimeService`：统一生成、删除、移动、保存坐标、dump。
- 新增 `PropModelService`：记录 modelPath、dump DrawObject/模型资源信息；默认不执行未知 native 写入。
- UI 新增“场景物体”页签。

## 最小实验步骤

1. 创建 Prop 配置，填写 `modelPath`。
2. 点击“生成 Prop via Brio SpawnFlags.Prop”。
3. 点击“Dump DrawObject”和“Dump model/resource info”。
4. 如果 Brio 生成对象稳定，再研究 `ActorAppearanceCapability.SetProp(WeaponModelId)` 的反射接入。
5. 如果要支持任意 `bg/...mdl`，优先研究 Penumbra temporary replacement，不直接写 DrawObject 指针。

## 风险点

- 直接写 `DrawObject` / `ResourceHandle` / native model pointer 很容易 AccessViolation。
- `CreateCharacter` 生成的 Prop 本质仍在 battle character/object table 路线里，不保证和地图物件生命周期一致。
- 任意 `.mdl` path 缺少 material、skeleton、shader、resource lifetime 管理时可能不可见或导致 draw object 异常。
- `SpawnFlags.Prop` 只告诉 Brio 这是 Prop actor，不代表游戏客户端会原生加载指定 bgparts 模型。
