# YourCraft / 你的世界

YourCraft is a map-editing plugin.

YourCraft 是一款可以进行地图编辑的插件。

---

## Features / 功能

- Build your own map!
- 
  建造你自己的地图！

- Persistently save your map edits. Your edits can be preserved after changing zones, relogging, or entering GPose. However, due to conflicts with Brio, Actors may need to be manually adjusted after entering GPose to make them appear.
  
  持久化保存地图编辑结果。切图、重新登陆、进入Gpose都可以保持。然而，因为与Brio冲突，Actor需要进入Gpose后手动调整一下坐标来让它出现。

- Freely spawn, move, and delete Actors, objects, and lights on the map.
  
  在地图上随意生成/移动/删除角色/物件/灯光。

- Supports generated Actors with action sequences, blend expressions, and lip sync.
  
  支持生成的Actor进行序列动作、blend表情和口型。

- Supports keeping Actors active inside GPose.
  
  支持在Gpose内保持角色。

---

## Installation / 安装

1. Open Dalamud Plugin Installer.
   
   打开 Dalamud 插件安装器。

3. Open **Third-Party Plugin Repositories** settings.
   
   打开 **第三方插件仓库** 设置。

5. Add the following repository URL:
   
   添加以下插件仓库地址：

   ```text
   https://raw.githubusercontent.com/Laboon2501/DalamudPlugins/main/repo.json
   ```

7. Update the plugin list and install **YourCraft**.
   
   更新插件列表，然后安装 **YourCraft**。

---

## Usage / 使用方法

### Actor / 角色相关
1. Generate Actors from Glamourer Designs or NPCs on the Actor page!
   
   在Actor页面通过Glamourer Design或NPC来生成Actor！

3. Use the Actor list at the bottom left to manage Actors.
   
   使用左侧底部的Actor列表来管理Actors

5. Use the Actor configuration panel on the right to control Actor transforms, action sequences, and behavior. You can also enter a Model ID to replace the model.
   
   通过右侧Actor配置来控制Actor变换、动作序列、行为，还可以输入Model ID来替换模型。

### Bgparts / 背景物件相关
1. Scan and select an Object to create a copy.
   
   扫描并选择一个Object来创建复制体。

3. Protect background objects you do not want to change, or mark Bgparts that should be modified first!
   
   保护你不想改变的背景物件，或设置为希望优先改动的Bgparts！

   Note: Since copied objects are created by randomly occupying other objects, you can protect objects you do not want to be changed, or mark objects you want to be occupied first.

   *注意：因为复制物体是通过随机占用其他物件实现的，所以可以保护你希望不要变动的物件，或设置你希望优先被占用的物件。

5. Change objects by specifying a model path.
   
   通过指定模型路径来改变物件。

7. Note: This plugin provides functionality to modify collision objects. Please enable it with caution. For native map objects, collision changes may still take effect even when the collision option is not checked.
   
   注意：插件提供修改碰撞体的功能，请谨慎开启。对于地图原生Object，也可能会出现碰撞体不勾选也生效的情况。

### Lights / 灯光
1. Create local point lights, area lights, or spotlights.
   
   创建本地点光源、面光源或聚焦光源。

3. Adjust light parameters to modify color, intensity, range, and more.
   
   通过调整灯光参数来修改颜色、强度、范围等。

### Scene Editor / 画面编辑
1. Enable Scene Editor to edit the game scene. It supports selecting Bgparts, Actors, and Lights.
   
   启用画面编辑来编辑游戏场景，支持选中Bgparts / Actor / Lights。

3. Hide unwanted NPCs, Bgparts, or Lights. Hidden Bgparts will be prioritized for occupation when creating copies.
   
   可以隐藏不需要的NPC/Bgparts/Lights。被隐藏的Bgparts会优先被拿来占用生成复制体。

5. Quickly adjust parameters through the Scene Editor mini panel!
   
   可以通过画面编辑的小面板来快捷调整参数！

### Native Scene Edits / 原生场景修改
Undo edits made to native map objects through `Scene Editor` here.

若在`画面编辑`中对地图原本的事物进行修改，可以在这里撤销。

### Settings / 设置
1. Supports exporting all modified behaviors to a file!
   
   支持将所有修改过的行为导出成文件！

3. Supports import warnings: if the imported preset conflicts with currently modified objects, a secondary confirmation will be shown.
   
   支持导入提醒：若导入预设与当前修改过的事物有冲突，提供二次确认功能。

---

## Disclaimer / 免责声明

Do not use it for any behavior that violates the game’s Terms of Service.

请勿将其用于任何违反游戏服务条款的行为。

---

## License / 许可证

AGPL-3.0-only. See `LICENSE.md`.

本项目采用 AGPL-3.0-only 许可证。详情请参阅 `LICENSE.md`。
