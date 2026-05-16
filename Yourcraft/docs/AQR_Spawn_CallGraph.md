# AQuestReborn Spawn Call Graph Evidence

## 前置命令

在 `C:\Users\kiomo\Documents\New project` 执行：

```powershell
git submodule update --init --recursive
```

结果：失败。

```text
D:/Git/mingw64/libexec/git-core\git-submodule: line 7: basename: command not found
D:/Git/mingw64/libexec/git-core\git-submodule: line 7: sed: command not found
D:/Git/mingw64/libexec/git-core\git-submodule: line 22: .: git-sh-setup: file not found
```

随后用以下命令取得完整 AQuestReborn 源码与子模块：

```powershell
git clone --recurse-submodules https://github.com/Sebane1/AQuestReborn .research\AQuestRebornFull
```

结果：成功。已检出 Brio、MCDF-Loader、RoleplayingQuestCore、RoleplayingVoiceCore、AnamCore、PenumbraAndGlamourerHelpers 等子模块。

再次在 `.research\AQuestRebornFull` 执行：

```powershell
git submodule update --init --recursive
```

结果：同样因本机 Git shell 工具缺失失败。取证基于 `git clone --recurse-submodules` 成功检出的源码。

## ripgrep 搜索结果

命令：

```powershell
foreach ($k in 'Spawn','Despawn','Summon','Create','Actor','EntityActorManager','ActorSpawn','InteractiveNpc','CustomNpcCharacter','ICharacter','PosingCapability','ActorAppearanceCapability','MCDF','Glamourer','Penumbra') {
  $c=(rg -n --fixed-strings $k .research\AQuestRebornFull | Measure-Object).Count
  "$k`t$c"
}
```

结果：

```text
Spawn                   188
Despawn                 16
Summon                  33
Create                  594
Actor                   1557
EntityActorManager      4
ActorSpawn              33
InteractiveNpc          70
CustomNpcCharacter      79
ICharacter              127
PosingCapability        70
ActorAppearanceCapability 26
MCDF                    39
Glamourer               274
Penumbra                382
```

关键 `rg` 命中：

```text
.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1460:string buttonLabel = isSpawned ? Translator.LocalizeUI("Dismiss NPC") : Translator.LocalizeUI("Summon NPC");
.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1461:if (ImGui.Button(buttonLabel, new Vector2(ImGui.GetColumnWidth(), 30)))
.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1467:_plugin.AQuestReborn.SummonCustomNpc(_customNpcCharacters[_currentSelection]);
.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1479:_plugin.AQuestReborn.DismissCustomNpc(_customNpcCharacters[_currentSelection].NpcName);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1427:var result = Brio.Brio.TryGetService<ActorSpawnService>(out _actorSpawnService);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1469:ICharacter character = null;
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1470:if (_actorSpawnService.CreateCharacter(out character, SpawnFlags.DefinePosition, true,
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1474:var npc = new InteractiveNpc(Plugin, character);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1491:PenumbraAndGlamourerIpcWrapper.Instance.SetCollectionForObject.Invoke(character.ObjectIndex, collectionGuid, true, true);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1502:native->ModelContainer.ModelCharaId = (int)npcData.MonsterModelId;
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1508:AppearanceAccessUtils.AppearanceManager?.LoadAppearance(npcData.McdfFilePath, character, (int)AppearanceSwapType.EntireAppearance);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1514:PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1550:Plugin.AnamcoreManager.TriggerEmote(character.Address, (ushort)emote.ActionTimeline[0].Value.RowId);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1596:if (_actorSpawnService.CreateCharacter(out character, SpawnFlags.DefinePosition, true,
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1600:var npc = new InteractiveNpc(Plugin, character);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1833:BrioAccessUtils.EntityManager.TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(out var appearance);
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2398:public void SummonCustomNpc(CustomNpcCharacter npcData)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2515:private void FreshSpawnCustomNpc(CustomNpcCharacter npcData)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2520:public void SummonCustomNpcAtPosition(CustomNpcCharacter npcData, System.Numerics.Vector3 position, System.Numerics.Vector3 rotation)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2558:public void DismissCustomNpc(string npcName)
.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:53:public bool CreateCharacter(...)
.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:67:public unsafe bool CloneCharacter(...)
.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:78:public unsafe void TriggerEmote(...)
.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:369:public unsafe void SetWeapon(...)
```

## UI Button

UI Button
  -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1460`
     `buttonLabel` 在 `Summon NPC` 和 `Dismiss NPC` 之间切换。
  -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1461`
     `ImGui.Button(buttonLabel, ...)`
  -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1467`
     `_plugin.AQuestReborn.SummonCustomNpc(_customNpcCharacters[_currentSelection])`

方法名：`CustomNpcWindow.Draw...` 的 NPC 编辑 UI 分支。文件中该片段位于自定义 NPC 窗口绘制流程内；该文件没有为这个按钮单独拆出独立命名方法。

调用的下一层方法：`AQuestReborn.SummonCustomNpc(CustomNpcCharacter npcData)`。

## Spawn Call Graph

UI Button
  -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1461`
     `ImGui.Button(buttonLabel, ...)`
    -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1467`
       `_plugin.AQuestReborn.SummonCustomNpc(_customNpcCharacters[_currentSelection])`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2398`
         `public void SummonCustomNpc(CustomNpcCharacter npcData)`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2400`
           Guard: `_actorSpawnService == null || !Plugin.ClientState.IsLoggedIn`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2401`
           Guard: `Plugin.ClientState.IsGPosing`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2406`
           Guard: `ConditionFlag.InDeepDungeon`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2412`
           Native duty guard via `FFXIVClientStructs.FFXIV.Client.Game.UI.Conditions.Instance()->BoundByDuty`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2424`
           If already valid: `DismissCustomNpc(npcData.NpcName)`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2436`
           If hidden pool contains NPC, reuse existing actor wrapper instead of spawning
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2502`
           If hidden pool failed: `FreshSpawnCustomNpc(npcData)`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2509`
           Normal path: `FreshSpawnCustomNpc(npcData)`
          -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2515`
             `private void FreshSpawnCustomNpc(CustomNpcCharacter npcData)`
            -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2517`
               `_customNpcActorSpawnQueue.Enqueue(npcData)`
              -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1447`
                 `private void CheckForCustomNpcCreationLoad()`
                -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1451`
                   Guard: local player exists and is valid
                -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1454`
                   Native guard: local player `DrawObject != null`
                -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1469`
                   `ICharacter character = null`
                -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1470`
                   `_actorSpawnService.CreateCharacter(out character, SpawnFlags.DefinePosition, true, spawnPos, 0, customName: ...)`
                  -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:53`
                     `public bool CreateCharacter(out ICharacter outCharacter, SpawnFlags flags, bool disableSpawnCompanion, Vector3 position, float rotation, string? customName)`
                    -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:58`
                       `var localPlayer = _objectTable.LocalPlayer`
                    -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:61`
                       `CloneCharacter(localPlayer, out outCharacter, flags, disableSpawnCompanion, position, rotation, customName)`
                      -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:67`
                         `public unsafe bool CloneCharacter(ICharacter sourceCharacter, out ICharacter outCharacter, ...)`
                        -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:84`
                           `CreateEmptyCharacter(out outCharacter, flags, customName)`
                        -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:90`
                           `targetNative->CharacterSetup.CopyFromCharacter(sourceCharacter.Native(), copyFlags)`
                        -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:91`
                           `targetNative->CharacterSetup.CopyFromCharacter(outCharacter.Native(), CharacterCopyFlags.None)`
                        -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:114`
                           If `SpawnFlags.DefinePosition`: writes `targetNative->GameObject.DefaultPosition`, `Position`, `Rotation`, `DefaultRotation`
                        -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:121`
                           `_actorRedrawService.DrawWhenReady(outCharacter)`

## Post-Spawn Runtime Object

Brio/ActorSpawnService.CreateCharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1473`
     `_customNpcCharacters[npcData.NpcName] = character`
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1474`
     `var npc = new InteractiveNpc(Plugin, character)`
    -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:236`
       `public InteractiveNpc(Plugin plugin, ICharacter character)`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:238`
         `_character = character`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:239`
         `_cachedObjectIndex = character.ObjectIndex`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:241`
         `_plugin.Framework.Update += Framework_Update`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:242`
         `_plugin.ClientState.TerritoryChanged += ClientState_TerritoryChanged`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:243`
         `BrioAccessUtils.EntityManager.SetSelectedEntity(_character)`
      -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:244`
         `BrioAccessUtils.EntityManager.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var posing)`
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1475`
     `_customNpcDictionary[npcData.NpcName] = npc`
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1476`
     `_interactiveNpcDictionary[npcData.NpcName] = npc`

结论：这一步确实生成了新的 Brio actor / `ICharacter`，不是单纯包装已有对象。`InteractiveNpc` 包装的是 `ActorSpawnService.CreateCharacter` 返回的新 `ICharacter`。

## Position Chain

UI Button
  -> `SummonCustomNpc`
    -> `FreshSpawnCustomNpc`
      -> `CheckForCustomNpcCreationLoad`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1463`
           `spawnX = playerPos.X + 2`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1464`
           `spawnZ = playerPos.Z + 2`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1465`
           `spawnY = GroundMap.GetGroundY(spawnX, spawnZ, playerPos.Y)`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1467`
           `spawnPos = new Vector3(spawnX, spawnY, spawnZ)`
        -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1470`
           `_actorSpawnService.CreateCharacter(..., spawnPos, 0, ...)`
          -> `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs:114`
             Brio writes native `targetNative->GameObject.Position = position`

Runtime movement / stay position:

`InteractiveNpc.SetTransform`
  -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:1139`
     `public void SetTransform(Vector3 position, Vector3 rotation, Vector3 scale)`
    -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:1157`
       Native `native->GameObject.SetPosition(position.X, position.Y, position.Z)`
    -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:1162`
       Native `native->GameObject.SetRotation(rotationRadians)`
    -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:1174`
       Brio `PosingCapability.ModelPosing.Transform = new Brio.Core.Transform { Position, Rotation, Scale }`

## Appearance Chain

Spawned ICharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1482`
     If Penumbra collection configured
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1485`
       `Brio.Brio.TryGetService<Brio.IPC.PenumbraService>(out var penumbraService)`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1491`
       `PenumbraAndGlamourerIpcWrapper.Instance.SetCollectionForObject.Invoke(character.ObjectIndex, collectionGuid, true, true)`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1492`
       `PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw)`

Spawned ICharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1497`
     If monster model configured
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1500`
       Native pointer cast to `FFXIVClientStructs.FFXIV.Client.Game.Character.Character*`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1502`
       Native `native->ModelContainer.ModelCharaId = (int)npcData.MonsterModelId`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1504`
       Penumbra redraw

Spawned ICharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1506`
     If MCDF configured
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1508`
       `AppearanceAccessUtils.AppearanceManager?.LoadAppearance(npcData.McdfFilePath, character, (int)AppearanceSwapType.EntireAppearance)`

Spawned ICharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1510`
     If Glamourer design configured
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1512`
       `Guid.TryParse(npcData.NpcGlamourerAppearanceString, out var designGuid)`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1514`
       `PenumbraAndGlamourerIpcWrapper.Instance.ApplyDesign.Invoke(designGuid, character.ObjectIndex)`

Queued appearance path:

`AQuestReborn.LoadAppearance`
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1764`
     `public void LoadAppearance(string appearanceData, AppearanceSwapType appearanceSwapType, ICharacter character)`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1776`
       `_appearanceApplicationQueue.Enqueue(...)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1832`
         `BrioAccessUtils.EntityManager.SetSelectedEntity(item.Item3)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1833`
         `TryGetCapabilityFromSelectedEntity<ActorAppearanceCapability>(out var appearance)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1836`
         `appearance.ImportAppearance(appearanceDataItem, Brio.Game.Actor.Appearance.AppearanceImportOptions.All)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1849`
         `AppearanceAccessUtils.AppearanceManager?.LoadAppearance(appearanceDataItem, item.Item3, (int)appearanceSwapType)`

## AnamCore Chain

Spawned ICharacter
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1518`
     `Plugin.AnamcoreManager.SetVoice(character, 0)`
  -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1550`
     `Plugin.AnamcoreManager.TriggerEmote(character.Address, (ushort)emote.ActionTimeline[0].Value.RowId)`
    -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:78`
       `public unsafe void TriggerEmote(nint characterAddress, uint animationId, bool forceBaseOverride = false)`
      -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:90`
         Native `chara->Timeline.BaseOverride = (ushort)animationId`
      -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:91`
         Native `chara->SetMode(CharacterModes.Normal, 0)`
      -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:98`
         Native `chara->Timeline.TimelineSequencer.PlayTimeline((ushort)animationId)`

Weapon chain:

`InteractiveNpc.ApplyClassWeapon`
  -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:273`
     `_plugin.AnamcoreManager.SetWeapon(_character, 0, 0)`
  -> `.research\AQuestRebornFull\AQuestReborn\InteractiveNpc.cs:326`
     `_plugin.AnamcoreManager.SetWeapon(_character, mainHandModel, offHandModel)`
    -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:369`
       `public unsafe void SetWeapon(ICharacter character, ulong mainHandModel, ulong offHandModel)`
      -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:376`
         Native `chara->DrawData.LoadWeapon(MainHand, ...)`
      -> `.research\AQuestRebornFull\AQuestReborn\AnamCore\AnamcoreManager.cs:381`
         Native `chara->DrawData.LoadWeapon(OffHand, ...)`

## Dismiss / Despawn Path

UI Button
  -> `.research\AQuestRebornFull\AQuestReborn\CustomNpc\CustomNpcWindow.cs:1479`
     `_plugin.AQuestReborn.DismissCustomNpc(_customNpcCharacters[_currentSelection].NpcName)`
    -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2558`
       `public void DismissCustomNpc(string npcName)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2563`
         `npc.StopFollowingPlayer()`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2566`
         `npc.HideNPC()`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2567`
         `npc.SetDefaults(new Vector3(0, -5000f, 0), Vector3.Zero)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2570`
         `_customNpcDictionary.Remove(npcName)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2571`
         `_interactiveNpcDictionary.Remove(npcName)`
      -> `.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2578`
         `_hiddenNpcPool[npcName] = (npc, character)`

结论：普通自定义 NPC 的 Dismiss 不是立即 Brio destroy。它隐藏 NPC、移动到地下、从活动字典移除，并放进 hidden pool 复用。

真正 destroy 的证据存在于其他清理路径：

```text
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:911:_actorSpawnService?.DestroyObject(kvp.Value)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1185:_actorSpawnService?.ClearAll()
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:1943:_actorSpawnService.DestroyObject(item.Value)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2262:Brio.Game.Actor.ActorRedrawService.SuspendRedraws = true
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2272:_actorSpawnService.DestroyObject(kvp.Value)
.research\AQuestRebornFull\AQuestReborn\AQuestReborn.cs:2286:_actorSpawnService.DestroyObject(kvp.Value.Character)
```

## 中断点

没有在 AQuestReborn 层中断。已追踪到：

- UI `ImGui.Button`
- AQuestReborn `SummonCustomNpc`
- 队列 `FreshSpawnCustomNpc`
- 处理队列 `CheckForCustomNpcCreationLoad`
- Brio `ActorSpawnService.CreateCharacter`
- Brio `CloneCharacter`
- native `CharacterSetup.CopyFromCharacter`
- native `GameObject.Position/Rotation`
- Brio redraw `ActorRedrawService.DrawWhenReady`
- AnamCore emote/weapon native methods
- Glamourer/Penumbra/MCDF/ActorAppearanceCapability 外观应用分支

唯一未继续深入的是 Brio `CreateEmptyCharacter` 和 `ActorRedrawService.DrawWhenReady` 的内部实现，因为本任务目标已到达 Brio 具体 service/method 以及 native 写入点。下一步若要继续，应打开：

- `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorSpawnService.cs`
- `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorRedrawService.cs`
- `.research\AQuestRebornFull\Brio\Brio\Game\Actor\ActorTableHelpers.cs`
