using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Numerics;

namespace LocalQuestReborn.UI;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly QuestDatabase database;
    private readonly QuestRuntimeService runtime;
    private readonly RealNpcSpawnService realNpcSpawn;
    private readonly LayoutProbeService layoutProbe;
    private readonly LayerDumpService layerDump;
    private readonly LocalLayoutObjectService localLayoutObjects;
    private readonly LocalLightNativeService localLights;
    private readonly BgPartCollisionSourceProbeService bgPartCollisionSourceProbe;
    private readonly AnimatedBgPartControllerProbeService animatedBgPartControllerProbe;
    private readonly StandaloneBgObjectProbeService standaloneBgObjectProbe;
    private readonly StandaloneRenderListProbeService standaloneRenderListProbe;
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly ActorAnimationPickerService actorAnimationPicker;
    private readonly ActionTimelinePickerWindow actionTimelinePickerWindow;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly Action reloadAction;

    private string selectedNpcId = string.Empty;
    private string selectedActorRuntimeId = string.Empty;
    private string selectedLocalLayoutObjectId = string.Empty;
    private string selectedLocalLightId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string templateBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
    private string protectedBgPartSearchText = string.Empty;
    private string preferredModifyBgPartSearchText = string.Empty;
    private string glamourerSearchText = string.Empty;
    private string gameNpcSearchText = string.Empty;
    private int actorBatchCount = 3;
    private Vector3 actorBatchOffset = new(1.5f, 0f, 0f);
    private bool actorBatchUsePlayerPosition;
    private bool localLayoutFullCollisionMode;
    private bool confirmFullLayoutCollisionMode;
    private bool allowDifferentResourcePathSlots;
    private bool createAsManyAsPossible = true;
    private int layoutCopyCount = 1;
    private float layoutCopySpacing = 2f;
    private float layoutCopySpacingY;
    private float layoutCopySpacingZ;
    private LocalLayoutTransformMode layoutCopyDefaultMode = LocalLayoutTransformMode.VisualOnly;
    private Vector3 layoutCopyDefaultRotationEuler;
    private Vector3 layoutCopyDefaultScale = Vector3.One;
    private bool layoutCopyUseTemplateScale;
    private bool layoutUseManualBasePosition;
    private Vector3 layoutManualBasePosition;
    private string layoutBatchCustomMdlPath = string.Empty;
    private string collisionProbeTargetMdlPath = string.Empty;
    private string selectedStandaloneObjectId = string.Empty;
    private string standaloneModelPath = "bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl";
    private int standalonePoolNameIndex;
    private Vector3 standalonePosition;
    private Vector3 standaloneRotationDegrees;
    private Vector3 standaloneScale = Vector3.One;
    private bool confirmStandaloneBgObjectExperiment;

    public MainWindow(
        Configuration configuration,
        QuestDatabase database,
        QuestRuntimeService runtime,
        ExperimentalNpcService experimentalNpc,
        RealNpcSpawnService realNpcSpawn,
        PropRuntimeService propRuntime,
        LayoutProbeService layoutProbe,
        LayoutInstanceTransformService layoutTransform,
        LayoutInstanceCloneService layoutClone,
        LayerDumpService layerDump,
        LocalLayoutObjectService localLayoutObjects,
        LocalLightNativeService localLights,
        BgPartVisualTransformProbeService bgPartVisualProbe,
        RotationMatrixExperimentService rotationMatrixExperiment,
        BgPartVisualRescueService bgPartVisualRescue,
        VisualOnlyRotationDeepProbeService visualOnlyRotationDeepProbe,
        DrawObjectUpdateDirtyProbeService drawObjectUpdateDirtyProbe,
        GraphicsSceneObjectTransformService graphicsSceneObjectTransform,
        BgPartCollisionSourceProbeService bgPartCollisionSourceProbe,
        AnimatedBgPartControllerProbeService animatedBgPartControllerProbe,
        StandaloneBgObjectProbeService standaloneBgObjectProbe,
        StandaloneRenderListProbeService standaloneRenderListProbe,
        MeddleStyleSceneProbeService meddleSceneProbe,
        GameNpcCatalogService gameNpcCatalog,
        GameNpcAppearanceResolver gameNpcAppearanceResolver,
        GlamourerDesignCatalogService glamourerDesignCatalog,
        ActorAnimationPickerService actorAnimationPicker,
        ActionTimelinePickerWindow actionTimelinePickerWindow,
        PenumbraIpcService penumbraIpc,
        Action reloadAction)
        : base("任务编辑器##LocalQuestRebornMain")
    {
        this.configuration = configuration;
        this.database = database;
        this.runtime = runtime;
        this.realNpcSpawn = realNpcSpawn;
        this.layoutProbe = layoutProbe;
        this.layerDump = layerDump;
        this.localLayoutObjects = localLayoutObjects;
        this.localLights = localLights;
        this.bgPartCollisionSourceProbe = bgPartCollisionSourceProbe;
        this.animatedBgPartControllerProbe = animatedBgPartControllerProbe;
        this.standaloneBgObjectProbe = standaloneBgObjectProbe;
        this.standaloneRenderListProbe = standaloneRenderListProbe;
        this.gameNpcCatalog = gameNpcCatalog;
        this.glamourerDesignCatalog = glamourerDesignCatalog;
        this.actorAnimationPicker = actorAnimationPicker;
        this.actionTimelinePickerWindow = actionTimelinePickerWindow;
        this.penumbraIpc = penumbraIpc;
        this.reloadAction = reloadAction;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 680),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        if (ImGui.Button("重新读取配置"))
            this.reloadAction();
        ImGui.SameLine();
        if (ImGui.Button("保存配置"))
            this.database.Save();
        ImGui.SameLine();
        if (ImGui.Button("重新探测 IPC"))
        {
            this.realNpcSpawn.ProbeBrioIpc();
            this.realNpcSpawn.ProbeGlamourerIpc();
        }

        ImGui.Separator();
        if (!ImGui.BeginTabBar("LqrMainTabs"))
            return;

        if (ImGui.BeginTabItem("运行调试"))
        {
            this.DrawRuntimeDebug();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("NPC 管理"))
        {
            this.DrawNpcManagement();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Actor 实例"))
        {
            this.DrawActorInstances();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("本地场景物体"))
        {
            this.DrawLocalLayoutObjects();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("本地灯光"))
        {
            this.DrawLocalLights();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("BgPart Slot Pool"))
        {
            this.DrawBgPartPool();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("BgPart 保护列表"))
        {
            this.DrawBgPartProtectionTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("BgPart 优先改动列表"))
        {
            this.DrawBgPartPreferredModifyTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug / 维护"))
        {
            this.DrawDebug();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawRuntimeDebug()
    {
        ImGui.TextWrapped($"配置版本：{this.configuration.Version}");
        ImGui.TextWrapped($"NPC/数据文件：{this.database.QuestFilePath}");
        ImGui.TextWrapped($"使用开发路径：{this.database.IsUsingDevelopmentQuestPath}");
        ImGui.TextWrapped($"当前地图 territory：{this.runtime.TerritoryType}");
        ImGui.TextWrapped($"玩家位置：{FormatVector(this.runtime.PlayerPosition)}");
        ImGui.Separator();
        ImGui.TextWrapped($"Unsafe/native 写入：{this.realNpcSpawn.EnableUnsafeNativeWrites}");
        ImGui.TextWrapped($"Brio Assembly：{this.realNpcSpawn.BrioAssemblyStatus}");
        ImGui.TextWrapped($"Brio IPC：{this.realNpcSpawn.BrioIpcProbeMessage}");
        ImGui.TextWrapped($"Glamourer/Penumbra：{this.realNpcSpawn.GlamourerIpcProbeMessage}");
        ImGui.TextWrapped($"Penumbra IPC：available={this.penumbraIpc.IsAvailable}, enabled={this.penumbraIpc.IsEnabled}, api={this.penumbraIpc.ApiVersionText}, collections={this.penumbraIpc.Collections.Count}");
        if (!string.IsNullOrWhiteSpace(this.penumbraIpc.LastError))
            ImGui.TextWrapped($"Penumbra IPC error：{this.penumbraIpc.LastError}");
        ImGui.TextWrapped($"外观队列：长度 {this.realNpcSpawn.AppearanceQueueLength}，当前 {this.realNpcSpawn.AppearanceQueueCurrentActor}");
        ImGui.TextWrapped($"本地场景物体：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"模型 override：{this.localLayoutObjects.LastModelOverrideStatus}");
        ImGui.TextWrapped($"本地灯光：{this.localLights.LastStatus} | pending={this.localLights.PendingOperationCount}");
    }

    private void DrawNpcManagement()
    {
        if (ImGui.Button("创建 NPC"))
            this.CreateNpcAtPlayer();
        ImGui.SameLine();
        if (ImGui.Button("重新扫描 Glamourer 设计"))
            this.glamourerDesignCatalog.Scan();
        ImGui.SameLine();
        if (ImGui.Button("重新读取 NPC 目录"))
            this.gameNpcCatalog.ReloadCatalog();

        ImGui.TextWrapped($"NPC 数量：{this.database.Npcs.Count} | Glamourer 设计：{this.glamourerDesignCatalog.Designs.Count} | NPC 目录：ENpc {this.gameNpcCatalog.ENpcCount} / BNpc {this.gameNpcCatalog.BNpcCount} / ModelChara {this.gameNpcCatalog.ModelCharaCount}");
        ImGui.TextWrapped($"Glamourer 扫描：{this.glamourerDesignCatalog.LastScanMessage}");
        ImGui.TextWrapped($"NPC 目录：{this.gameNpcCatalog.LastLoadMessage}");
        if (this.database.Npcs.Count == 0)
        {
            ImGui.TextWrapped("还没有 NPC 配置。点击“创建 NPC”会在玩家当前位置创建一个本地 NPC。");
            return;
        }

        this.EnsureSelectedNpc();
        if (ImGui.BeginCombo("选择 NPC", this.SelectedNpcLabel()))
        {
            foreach (var optionNpc in this.database.Npcs)
            {
                var selected = string.Equals(this.selectedNpcId, optionNpc.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{optionNpc.Name} ({optionNpc.Id})", selected))
                    this.selectedNpcId = optionNpc.Id;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var currentNpc = this.GetSelectedNpc();
        if (currentNpc == null)
            return;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "基础信息");
        ImGui.TextWrapped($"NPC ID：{currentNpc.Id}");
        EditString("名称", currentNpc.Name, 128, value => currentNpc.Name = value);
        EditString("名称模板", currentNpc.NameTemplate, 128, value => currentNpc.NameTemplate = value);
        var radius = currentNpc.InteractRadius;
        if (ImGui.InputFloat("默认交互半径", ref radius))
            currentNpc.InteractRadius = Math.Max(0.1f, radius);
        EditVector3Data("默认生成偏移", currentNpc.DefaultSpawnOffset);
        EditVector3DataDegrees("默认旋转", currentNpc.DefaultRotationEuler);
        EnsureNpcDefaultScale(currentNpc);
        EditVector3Data("默认缩放", currentNpc.DefaultScale);

        var defaultAnimation = (int)Math.Min(currentNpc.DefaultAnimationId, int.MaxValue);
        if (ImGui.InputInt("默认动画 ID", ref defaultAnimation))
            currentNpc.DefaultAnimationId = (uint)Math.Max(0, defaultAnimation);
        ImGui.SameLine();
        this.DrawAnimationPickerButton("##NpcDefaultAnimationPicker", ActorAnimationPickerRequest.ForNpcDefault(currentNpc.Id, ActorAnimationPickerMode.EmoteActionsOnly));
        var autoPlay = currentNpc.AutoPlayDefaultAnimation;
        if (ImGui.Checkbox("生成后自动播放默认动画", ref autoPlay))
            currentNpc.AutoPlayDefaultAnimation = autoPlay;
        var lookAtPlayerEnabled = currentNpc.LookAtPlayerEnabled;
        if (ImGui.Checkbox("默认靠近时看向玩家", ref lookAtPlayerEnabled))
            currentNpc.LookAtPlayerEnabled = lookAtPlayerEnabled;
        var lookRadius = currentNpc.LookAtRadius;
        if (ImGui.InputFloat("默认看向半径", ref lookRadius))
            currentNpc.LookAtRadius = Math.Max(0.1f, lookRadius);
        currentNpc.LookAtMode = NpcLookAtMode.NativeLookAt;
        ImGui.TextDisabled("看向方式：NativeLookAt（固定）");
        var respawn = currentNpc.RespawnAfterGpose;
        if (ImGui.Checkbox("退出 GPose 后自动重建", ref respawn))
            currentNpc.RespawnAfterGpose = respawn;
        EditString("模板备注", currentNpc.Notes, 512, value => currentNpc.Notes = value);
        if (currentNpc.TerritoryType != 0 || currentNpc.Position.X != 0f || currentNpc.Position.Y != 0f || currentNpc.Position.Z != 0f)
            ImGui.TextDisabled($"旧版地图/坐标已保留但不用于模板生成：territory={currentNpc.TerritoryType}, position={FormatVector(ToVector3(currentNpc.Position))}");

        if (ImGui.Button("保存 NPC 配置"))
            this.database.Save();
        ImGui.SameLine();
        if (ImGui.Button("删除 NPC 配置"))
        {
            this.realNpcSpawn.DespawnAllForNpc(currentNpc.Id);
            this.database.Npcs.Remove(currentNpc);
            this.database.Save();
            this.selectedNpcId = this.database.Npcs.FirstOrDefault()?.Id ?? string.Empty;
            return;
        }

        this.DrawNpcAppearanceEditor(currentNpc);
        ImGui.Separator();
        ImGui.TextWrapped($"当前由此模板生成的 Actor 数量：{this.realNpcSpawn.GetActorCountForNpc(currentNpc.Id)}");
        ImGui.TextDisabled("生成、移动、应用外观等运行态操作请到“Actor 实例”页处理。");
    }

    private void DrawNpcAppearanceEditor(CustomNpc npc)
    {
        var appearance = npc.Appearance;
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "外观来源");
        DrawEnumCombo("sourceType", appearance.SourceType, value => appearance.SourceType = value);
        EditString("displayName", appearance.DisplayName, 160, value => appearance.DisplayName = value);

        switch (appearance.SourceType)
        {
            case CustomNpcAppearanceSourceType.None:
                ImGui.TextWrapped("不应用外观，保持生成时的玩家 clone 外观。");
                break;
            case CustomNpcAppearanceSourceType.CurrentPlayer:
                ImGui.TextWrapped("使用生成时复制的当前玩家外观。");
                break;
            case CustomNpcAppearanceSourceType.GlamourerDesign:
                this.DrawGlamourerDesignPicker(npc);
                break;
            case CustomNpcAppearanceSourceType.GameNpc:
                this.DrawGameNpcPicker(npc);
                break;
            case CustomNpcAppearanceSourceType.MCDF:
                EditString("MCDF 路径", appearance.McdfPath, 512, value => appearance.McdfPath = value);
                ImGui.TextWrapped("MCDF 外观应用接口仍是占位，当前只保存配置。");
                break;
            case CustomNpcAppearanceSourceType.PenumbraCollection:
                ImGui.TextWrapped("旧版 Penumbra 外观来源已改为下方 NPC 级 Collection 选择器。这里不再单独保存 collection 名称。");
                break;
        }

        this.DrawNpcPenumbraCollectionSelector(npc);
        EditString("notes/debugInfo", appearance.Notes, 512, value => appearance.Notes = value);
    }

    private void DrawNpcPenumbraCollectionSelector(CustomNpc npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Penumbra 预设组 / Collection");
        ImGui.TextWrapped($"IPC：available={this.penumbraIpc.IsAvailable}, enabled={this.penumbraIpc.IsEnabled}, api={this.penumbraIpc.ApiVersionText}");
        ImGui.TextWrapped(this.penumbraIpc.LastStatus);
        if (!string.IsNullOrWhiteSpace(this.penumbraIpc.LastError))
            ImGui.TextWrapped($"错误：{this.penumbraIpc.LastError}");

        if (ImGui.Button("重新获取 Penumbra IPC"))
            this.penumbraIpc.TryConnectOrRefresh("npc ui");
        ImGui.SameLine();
        if (ImGui.Button("刷新 Penumbra 预设组"))
            this.penumbraIpc.RefreshCollections();

        var currentLabel = this.GetNpcPenumbraCollectionLabel(npc);
        if (ImGui.BeginCombo("该 NPC 启用 Penumbra 哪组预设 / Collection", currentLabel))
        {
            if (ImGui.Selectable("不使用 / 不修改 Penumbra", npc.PenumbraMode == PenumbraCollectionMode.DoNotTouch))
                this.SetNpcPenumbraSelection(npc, PenumbraCollectionMode.DoNotTouch, null, string.Empty);
            if (ImGui.Selectable("继承默认 / 清除单独分配", npc.PenumbraMode == PenumbraCollectionMode.InheritDefault))
                this.SetNpcPenumbraSelection(npc, PenumbraCollectionMode.InheritDefault, null, string.Empty);

            var currentId = npc.PenumbraCollectionId;
            var missingSelection = npc.PenumbraMode == PenumbraCollectionMode.UseCollection &&
                currentId.HasValue &&
                this.penumbraIpc.Collections.All(item => item.Id != currentId.Value);
            if (missingSelection)
            {
                ImGui.Separator();
                ImGui.TextDisabled($"Missing: {npc.PenumbraCollectionNameCache} / {currentId}");
            }

            if (this.penumbraIpc.Collections.Count > 0)
                ImGui.Separator();

            foreach (var collection in this.penumbraIpc.Collections)
            {
                var selected = npc.PenumbraMode == PenumbraCollectionMode.UseCollection && npc.PenumbraCollectionId == collection.Id;
                if (ImGui.Selectable($"{collection.Name}##{collection.Id}", selected))
                    this.SetNpcPenumbraSelection(npc, PenumbraCollectionMode.UseCollection, collection.Id, collection.Name);
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (!this.penumbraIpc.IsAvailable)
            ImGui.TextDisabled("Penumbra IPC 未连接，插件会自动重试；当前保存值不会被清空。");
        if (npc.PenumbraMode == PenumbraCollectionMode.UseCollection && npc.PenumbraCollectionId.HasValue)
            ImGui.TextDisabled($"保存依据：{npc.PenumbraCollectionId}；名称缓存：{npc.PenumbraCollectionNameCache}");
    }

    private string GetNpcPenumbraCollectionLabel(CustomNpc npc)
    {
        return npc.PenumbraMode switch
        {
            PenumbraCollectionMode.DoNotTouch => "不使用 / 不修改 Penumbra",
            PenumbraCollectionMode.InheritDefault => "继承默认 / 清除单独分配",
            PenumbraCollectionMode.UseCollection => this.GetCollectionDisplayName(npc.PenumbraCollectionId, npc.PenumbraCollectionNameCache),
            _ => npc.PenumbraMode.ToString(),
        };
    }

    private string GetCollectionDisplayName(Guid? id, string nameCache)
    {
        if (id == null)
            return "Missing: 未选择 Collection";

        var found = this.penumbraIpc.Collections.FirstOrDefault(item => item.Id == id.Value);
        if (found != null)
            return found.Name;

        return $"Missing: {nameCache} / {id}";
    }

    private void SetNpcPenumbraSelection(CustomNpc npc, PenumbraCollectionMode mode, Guid? collectionId, string collectionName)
    {
        npc.PenumbraMode = mode;
        npc.PenumbraCollectionId = collectionId;
        npc.PenumbraCollectionNameCache = collectionName;
        this.database.Save();
        if (this.realNpcSpawn.GetActorCountForNpc(npc.Id) > 0)
            this.realNpcSpawn.ApplyNpcAppearanceForNpc(npc.Id);
    }

    private void DrawGlamourerDesignPicker(CustomNpc npc)
    {
        var appearance = npc.Appearance;
        ImGui.TextWrapped($"当前 design 名称：{appearance.DisplayName}");
        EditString("当前 design GUID", appearance.GlamourerDesignId, 128, value => appearance.GlamourerDesignId = value);
        ImGui.InputText("设计搜索", ref this.glamourerSearchText, 128);
        if (ImGui.Button("扫描 Glamourer 设计"))
            this.glamourerDesignCatalog.Scan();
        ImGui.SameLine();
        if (ImGui.Button("探测 Glamourer IPC"))
            this.realNpcSpawn.ProbeGlamourerIpc();

        var designs = this.glamourerDesignCatalog.Search(this.glamourerSearchText, 60);
        if (!ImGui.BeginTable("GlamourerDesigns", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("GUID");
        ImGui.TableSetupColumn("来源文件");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();
        foreach (var design in designs)
        {
            ImGui.TableNextRow();
            ImGui.PushID($"glamourer-{design.Identifier}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(design.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(design.Identifier);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(design.FilePath);
            ImGui.TableSetColumnIndex(3);
            if (ImGui.Button("选为此 NPC 外观"))
            {
                this.glamourerDesignCatalog.ApplyDesignToNpc(npc, design);
                this.database.Save();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawGameNpcPicker(CustomNpc npc)
    {
        var appearance = npc.Appearance;
        ImGui.TextWrapped($"当前 NPC 名称：{appearance.GameNpcName}");
        ImGui.TextWrapped($"gameNpcKind：{appearance.GameNpcKind}");
        var baseId = (int)Math.Min(appearance.GameNpcBaseId, int.MaxValue);
        if (ImGui.InputInt("gameNpcBaseId", ref baseId))
            appearance.GameNpcBaseId = (uint)Math.Max(0, baseId);
        var modelId = (int)Math.Min(appearance.GameNpcModelId, int.MaxValue);
        if (ImGui.InputInt("gameNpcModelId", ref modelId))
            appearance.GameNpcModelId = (uint)Math.Max(0, modelId);
        EditString("gameNpcName", appearance.GameNpcName, 160, value => appearance.GameNpcName = value);
        DrawEnumCombo("gameNpcKind", appearance.GameNpcKind, value => appearance.GameNpcKind = value);

        if (ImGui.Button("从当前 Target 读取 NPC 信息"))
        {
            if (this.gameNpcCatalog.SaveCurrentTargetAsGameNpcAppearance(npc, moveNpcToPlayer: false, this.runtime.PlayerPosition, out var message))
                this.database.Save();
            this.realNpcSpawn.SetMessage(message);
        }

        ImGui.InputText("NPC 目录搜索", ref this.gameNpcSearchText, 128);
        var entries = this.gameNpcCatalog.Search(this.gameNpcSearchText, 100);
        if (!ImGui.BeginTable("GameNpcCatalog", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 220f)))
            return;

        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("类型");
        ImGui.TableSetupColumn("RowId");
        ImGui.TableSetupColumn("ModelCharaId");
        ImGui.TableSetupColumn("解析摘要");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();
        foreach (var entry in entries)
        {
            ImGui.TableNextRow();
            ImGui.PushID($"gamenpc-{entry.SourceKind}-{entry.RowId}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Name : entry.DisplayName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(entry.SourceKind.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(entry.RowId.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(entry.ModelCharaId.ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(entry.RawDebugInfo) ? entry.DebugInfo : entry.RawDebugInfo);
            ImGui.TableSetColumnIndex(5);
            if (ImGui.Button("选为此 NPC 外观"))
            {
                this.gameNpcCatalog.ApplyCatalogEntryToNpc(npc, entry);
                this.database.Save();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawActorInstances()
    {
        this.EnsureSelectedNpc();
        var available = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Min(560f, Math.Max(380f, available.X * 0.45f));

        if (ImGui.BeginChild("ActorInstancesLeftPanel", new Vector2(leftWidth, 0f), true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            this.DrawActorSpawnAndListPanel();
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("ActorInstancesDetailsPanel", Vector2.Zero, true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            this.DrawSelectedActorDetailsPanel();
        ImGui.EndChild();
    }

    private void DrawActorSpawnAndListPanel()
    {
        var selectedNpc = this.GetSelectedNpc();
        if (ImGui.BeginCombo("选中 NPC 模板", this.SelectedNpcLabel()))
        {
            foreach (var optionNpc in this.database.Npcs)
            {
                var selected = string.Equals(this.selectedNpcId, optionNpc.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{optionNpc.Name} ({optionNpc.Id})", selected))
                    this.selectedNpcId = optionNpc.Id;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        selectedNpc = this.GetSelectedNpc();

        if (ImGui.Button("刷新有效性"))
            this.realNpcSpawn.RefreshActors();
        ImGui.SameLine();
        ImGui.BeginDisabled(selectedNpc == null || !this.realNpcSpawn.CanSpawnRealActor);
        if (ImGui.Button("从模板生成唯一 Actor") && selectedNpc != null)
        {
            var actor = this.realNpcSpawn.SpawnUnique(selectedNpc);
            if (actor != null)
                this.selectedActorRuntimeId = actor.RuntimeId;
        }
        ImGui.SameLine();
        if (ImGui.Button("生成一个新 Actor") && selectedNpc != null)
        {
            var actor = this.realNpcSpawn.SpawnNew(selectedNpc);
            if (actor != null)
                this.selectedActorRuntimeId = actor.RuntimeId;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(selectedNpc == null);
        if (ImGui.Button("删除此模板的全部 Actor") && selectedNpc != null)
            this.realNpcSpawn.DespawnAllForNpc(selectedNpc.Id);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("删除全部 Actor"))
            this.realNpcSpawn.DespawnAll();

        if (selectedNpc != null)
            this.DrawActorBatchSpawnControls(selectedNpc);

        ImGui.TextWrapped($"Actor 数量：{this.realNpcSpawn.Actors.Count} | 队列长度：{this.realNpcSpawn.AppearanceQueueLength} | {this.realNpcSpawn.AppearanceQueueStatus}");
        ImGui.TextWrapped($"SpawnIntent 数量：{this.realNpcSpawn.SpawnIntentCount}");

        if (!ImGui.BeginTable("RuntimeActors", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 320f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("runtimeId");
        ImGui.TableSetupColumn("npcId");
        ImGui.TableSetupColumn("npcName");
        ImGui.TableSetupColumn("objectIndex");
        ImGui.TableSetupColumn("address");
        ImGui.TableSetupColumn("valid");
        ImGui.TableSetupColumn("position");
        ImGui.TableHeadersRow();
        foreach (var actor in this.realNpcSpawn.Actors)
        {
            ImGui.TableNextRow();
            ImGui.PushID(actor.RuntimeId);
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable("选中", string.Equals(this.selectedActorRuntimeId, actor.RuntimeId, StringComparison.Ordinal)))
                this.selectedActorRuntimeId = actor.RuntimeId;
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(ShortId(actor.RuntimeId));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(actor.NpcId);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(actor.NpcName);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(actor.ObjectIndex);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(actor.Address);
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(actor.IsValid ? "有效" : "失效");
            ImGui.TableSetColumnIndex(7);
            ImGui.TextWrapped(FormatVector(actor.LastKnownPosition));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawSelectedActorDetailsPanel()
    {
        var selectedActor = string.IsNullOrWhiteSpace(this.selectedActorRuntimeId) ? null : this.realNpcSpawn.GetActor(this.selectedActorRuntimeId);
        if (selectedActor == null)
        {
            ImGui.TextWrapped("当前没有选中 Actor。请在左侧 Actor 列表中选择一个实例。");
            return;
        }
        var npc = this.database.GetNpcById(selectedActor.NpcId);
        ImGui.PushID(selectedActor.RuntimeId);
        ImGui.TextWrapped($"选中 Actor：{selectedActor.RuntimeId}");
        ImGui.TextWrapped($"模板 NPC：{selectedActor.TemplateNpcId} / {selectedActor.NpcId}");
        ImGui.TextWrapped($"显示名：{selectedActor.DisplayName}");
        ImGui.TextWrapped($"生成地图：{selectedActor.SpawnedTerritoryType} {selectedActor.SpawnedTerritoryName}");
        ImGui.TextWrapped($"当前外观来源：{selectedActor.AppearanceSourceType}");
        ImGui.TextWrapped($"Post-spawn pipeline：{selectedActor.PostSpawnPipelineState} / {selectedActor.PostSpawnPipelineStatus}");
        ImGui.TextWrapped($"最后外观：{selectedActor.LastAppearanceApplyResult}");
        ImGui.TextWrapped($"错误：{selectedActor.LastError}");
        if (ImGui.Button("删除此 Actor"))
        {
            this.DeleteSelectedActor(selectedActor);
            ImGui.PopID();
            return;
        }

        this.DrawSelectedActorTransformEditor(selectedActor, npc);
        this.DrawSelectedActorBehaviorEditor(selectedActor, npc);
        this.DrawSelectedActorActionSequenceEditor(selectedActor);
        this.DrawSelectedActorAppearanceEditor(selectedActor, npc);
        ImGui.PopID();
    }

    private void DrawSelectedActorTransformEditor(RuntimeActorInstance actor, CustomNpc? npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Actor Transform 编辑");
        if (!actor.IsValid || actor.CharacterObject == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "当前 Actor 无效或已删除。");
            return;
        }

        if (actor.TransformEditScale == Vector3.Zero)
            actor.TransformEditScale = actor.LastKnownScale == Vector3.Zero ? Vector3.One : actor.LastKnownScale;
        if (actor.TransformEditPosition == Vector3.Zero && actor.LastKnownPosition != Vector3.Zero)
            actor.TransformEditPosition = actor.LastKnownPosition;

        var editPosition = actor.TransformEditPosition;
        if (ImGui.InputFloat("Actor Position X", ref editPosition.X)) actor.TransformEditPosition = editPosition;
        if (ImGui.InputFloat("Actor Position Y", ref editPosition.Y)) actor.TransformEditPosition = editPosition;
        if (ImGui.InputFloat("Actor Position Z", ref editPosition.Z)) actor.TransformEditPosition = editPosition;

        var rotationDegrees = new Vector3(
            RadiansToDegrees(actor.TransformEditRotationEuler.X),
            RadiansToDegrees(actor.TransformEditRotationEuler.Y),
            RadiansToDegrees(actor.TransformEditRotationEuler.Z));
        if (ImGui.InputFloat("Actor Pitch X (deg)", ref rotationDegrees.X))
            actor.TransformEditRotationEuler = new Vector3(DegreesToRadians(rotationDegrees.X), actor.TransformEditRotationEuler.Y, actor.TransformEditRotationEuler.Z);
        if (ImGui.InputFloat("Actor Yaw Y (deg)", ref rotationDegrees.Y))
            actor.TransformEditRotationEuler = new Vector3(actor.TransformEditRotationEuler.X, DegreesToRadians(rotationDegrees.Y), actor.TransformEditRotationEuler.Z);
        if (ImGui.InputFloat("Actor Roll Z (deg)", ref rotationDegrees.Z))
            actor.TransformEditRotationEuler = new Vector3(actor.TransformEditRotationEuler.X, actor.TransformEditRotationEuler.Y, DegreesToRadians(rotationDegrees.Z));

        var editScale = actor.TransformEditScale;
        if (ImGui.InputFloat("Actor Scale X", ref editScale.X)) actor.TransformEditScale = Vector3.Max(editScale, new Vector3(0.01f));
        if (ImGui.InputFloat("Actor Scale Y", ref editScale.Y)) actor.TransformEditScale = Vector3.Max(editScale, new Vector3(0.01f));
        if (ImGui.InputFloat("Actor Scale Z", ref editScale.Z)) actor.TransformEditScale = Vector3.Max(editScale, new Vector3(0.01f));

        ImGui.TextWrapped($"readback position：{FormatVector(actor.LastKnownPosition)}");
        ImGui.TextWrapped($"readback rotation：pitch {RadiansToDegrees(actor.LastKnownRotationEuler.X):F1}, yaw {RadiansToDegrees(actor.LastKnownRotationEuler.Y):F1}, roll {RadiansToDegrees(actor.LastKnownRotationEuler.Z):F1}");
        ImGui.TextWrapped($"readback scale：{FormatVector(actor.LastKnownScale)}");
        ImGui.TextWrapped($"last transform readback：{(string.IsNullOrWhiteSpace(actor.LastTransformReadback) ? "未读取" : actor.LastTransformReadback)}");
        ImGui.TextWrapped($"last transform error：{(string.IsNullOrWhiteSpace(actor.LastTransformError) ? "无" : actor.LastTransformError)}");

        ImGui.BeginDisabled(!actor.IsValid);
        if (ImGui.Button("应用 Transform"))
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        ImGui.SameLine();
        if (ImGui.Button("移动到玩家当前位置") && this.runtime.PlayerPosition.HasValue)
        {
            actor.TransformEditPosition = this.runtime.PlayerPosition.Value;
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }
        ImGui.SameLine();
        if (ImGui.Button("重置位置"))
        {
            actor.TransformEditPosition = actor.SpawnPosition == Vector3.Zero ? actor.LastKnownPosition : actor.SpawnPosition;
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }

        if (ImGui.Button("重置旋转"))
        {
            actor.TransformEditRotationEuler = Vector3.Zero;
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }
        ImGui.SameLine();
        if (ImGui.Button("重置缩放"))
        {
            actor.TransformEditScale = Vector3.One;
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }
        ImGui.SameLine();
        if (ImGui.Button("保存当前 Transform"))
            this.realNpcSpawn.SaveActorTransformSnapshot(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);

        if (ImGui.Button("从当前 Actor 读取 Transform"))
            this.realNpcSpawn.RefreshActorTransform(actor.RuntimeId);
        ImGui.EndDisabled();
    }

    private void DrawSelectedActorBehaviorEditor(RuntimeActorInstance actor, CustomNpc? npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Actor 行为");
        var lookAtEnabled = actor.LookAtPlayerEnabled;
        var lookAtChanged = false;
        if (ImGui.Checkbox("此 Actor 看向玩家", ref lookAtEnabled))
        {
            actor.LookAtPlayerEnabled = lookAtEnabled;
            actor.LookAtMode = NpcLookAtMode.NativeLookAt;
            lookAtChanged = true;
        }

        var lookRadius = actor.LookAtRadius <= 0.1f ? npc?.LookAtRadius ?? 8f : actor.LookAtRadius;
        if (ImGui.InputFloat("此 Actor 看向半径", ref lookRadius))
        {
            actor.LookAtRadius = Math.Max(0.1f, lookRadius);
            lookAtChanged = true;
        }

        actor.LookAtMode = NpcLookAtMode.NativeLookAt;
        ImGui.TextDisabled("看向方式：NativeLookAt（固定）");

        if (lookAtChanged)
            this.realNpcSpawn.UpdateActorLookAtSettings(actor.RuntimeId, actor.LookAtPlayerEnabled, actor.LookAtRadius);

        var animationId = (int)Math.Min(actor.CurrentAnimationId == 0 ? actor.DefaultAnimationId : actor.CurrentAnimationId, int.MaxValue);
        if (ImGui.InputInt("此 Actor 动画 ID", ref animationId))
            actor.CurrentAnimationId = (uint)Math.Max(0, animationId);
        ImGui.SameLine();
        this.DrawAnimationPickerButton("##ActorCurrentAnimationPicker", ActorAnimationPickerRequest.ForActorCurrent(actor.RuntimeId, ActorAnimationPickerMode.EmoteActionsOnly));

        this.DrawActorRigControls(actor);
        ImGui.TextWrapped("动画骨架 / Animation Rig（实验）：当前不会写 Race/Gender/Customize、不会调用 Penumbra redraw、不会改变外观。未找到安全动画-only 数据路径前会显示 Unsupported。");
        ImGui.TextWrapped($"Rig 状态：{actor.AnimationRigStatus}");

        ImGui.TextWrapped($"动画状态：enabled={actor.AnimationEnabled}, current={actor.CurrentAnimationId}, error={(string.IsNullOrWhiteSpace(actor.LastAnimationError) ? "无" : actor.LastAnimationError)}");
        ImGui.TextWrapped($"看向状态：enabled={actor.LookAtPlayerEnabled}, registered={actor.LookAtRegistered}, target={actor.LookAtTargetDebug}, looking={actor.IsLookingAtPlayer}, error={(string.IsNullOrWhiteSpace(actor.LastLookAtError) ? "无" : actor.LastLookAtError)}");

        ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
        if (ImGui.Button("播放动画"))
            this.realNpcSpawn.PlayAnimation(actor.RuntimeId, actor.CurrentAnimationId);
        ImGui.SameLine();
        if (ImGui.Button("停止/恢复 idle"))
            this.realNpcSpawn.StopAnimation(actor.RuntimeId);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(npc == null);
        if (ImGui.Button("保存为模板默认值") && npc != null)
        {
            npc.DefaultAnimationId = actor.CurrentAnimationId;
            npc.LookAtPlayerEnabled = actor.LookAtPlayerEnabled;
            npc.LookAtRadius = actor.LookAtRadius;
            npc.LookAtMode = NpcLookAtMode.NativeLookAt;
            SetVector3Data(npc.DefaultRotationEuler, actor.TransformEditRotationEuler);
            SetVector3Data(npc.DefaultScale, actor.TransformEditScale == Vector3.Zero ? Vector3.One : actor.TransformEditScale);
            this.database.Save();
            this.realNpcSpawn.SetMessage($"已把 Actor {ShortId(actor.RuntimeId)} 的行为/旋转/缩放保存为模板默认值。");
        }
        ImGui.EndDisabled();
    }

    private void DrawSelectedActorActionSequenceEditor(RuntimeActorInstance actor)
    {
        ImGui.Separator();
        if (!ImGui.TreeNode("动作序列 + 头顶气泡"))
            return;

        var enabled = actor.EnableActionSequence;
        if (ImGui.Checkbox("启用动作序列", ref enabled))
        {
            actor.EnableActionSequence = enabled;
            this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
        }

        var loop = actor.ActionSequenceLoop;
        if (ImGui.Checkbox("循环播放", ref loop))
            actor.ActionSequenceLoop = loop;

        var loopDelay = actor.ActionSequenceLoopDelay;
        if (ImGui.InputFloat("循环间隔（秒）", ref loopDelay))
            actor.ActionSequenceLoopDelay = Math.Max(0f, loopDelay);

        ImGui.TextWrapped($"状态：{actor.ActionSequenceStatus}");
        ImGui.TextWrapped($"错误：{(string.IsNullOrWhiteSpace(actor.LastActionSequenceError) ? "无" : actor.LastActionSequenceError)}");

        if (ImGui.Button("添加步骤"))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = $"Step {actor.ActionSequence.Count + 1}", DurationSeconds = 3f });
        ImGui.SameLine();
        if (ImGui.Button("添加 Spawn"))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = "Spawn", Kind = ActorActionStepKind.Spawn, DurationSeconds = 0.1f });
        ImGui.SameLine();
        if (ImGui.Button("添加 Despawn"))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = "Despawn", Kind = ActorActionStepKind.Despawn, DurationSeconds = 1f, HideBubbleOnDespawn = true });
        ImGui.SameLine();
        if (ImGui.Button("从当前默认动作创建步骤"))
        {
            actor.ActionSequence.Add(new ActorActionSequenceStep
            {
                Name = $"Default {actor.DefaultAnimationId}",
                Kind = ActorActionStepKind.Action,
                AnimationId = (ushort)Math.Clamp(actor.DefaultAnimationId, 0, ushort.MaxValue),
                DurationSeconds = 3f,
            });
        }
        ImGui.SameLine();
        if (ImGui.Button("重置序列运行状态"))
            this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);

        for (var i = 0; i < actor.ActionSequence.Count; i++)
        {
            var step = actor.ActionSequence[i];
            ImGui.PushID(step.Id.ToString("N"));
            if (ImGui.TreeNode($"{i + 1}. {step.Name}##ActionStep"))
            {
                EditString("步骤名称", step.Name, 96, value => step.Name = value);
                DrawActorActionStepKindCombo(step);

                if (step.Kind == ActorActionStepKind.Action)
                {
                    var animationStepId = (int)step.AnimationId;
                    if (ImGui.InputInt("动画ID / ActionTimelineId", ref animationStepId))
                        step.AnimationId = (ushort)Math.Clamp(animationStepId, 0, ushort.MaxValue);
                    ImGui.SameLine();
                    this.DrawAnimationPickerButton("##StepAnimationPicker", ActorAnimationPickerRequest.ForStepAnimation(actor.RuntimeId, step.Id, ActorAnimationPickerMode.EmoteActionsOnly));
                }
                else if (step.Kind == ActorActionStepKind.Despawn)
                {
                    ImGui.TextDisabled("Despawn 只隐藏模型，不删除 Actor。循环播放时后续 Spawn 会重新显示。");
                    var hideBubble = step.HideBubbleOnDespawn;
                    if (ImGui.Checkbox("Despawn 时隐藏气泡", ref hideBubble))
                        step.HideBubbleOnDespawn = hideBubble;
                }
                else if (step.Kind == ActorActionStepKind.Spawn)
                {
                    ImGui.TextDisabled("Spawn 只恢复已有 Actor 的显示，不创建新 Actor。");
                }

                var duration = step.DurationSeconds;
                if (ImGui.InputFloat("DurationSeconds", ref duration))
                    step.DurationSeconds = Math.Max(0f, duration);

                if (step.Kind == ActorActionStepKind.Action)
                    this.DrawActorActionStepAnimationOptions(actor, step);

                DrawActorActionStepBubbleOptions(step);

                ImGui.BeginDisabled(i == 0);
                if (ImGui.Button("上移"))
                {
                    (actor.ActionSequence[i - 1], actor.ActionSequence[i]) = (actor.ActionSequence[i], actor.ActionSequence[i - 1]);
                    this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i >= actor.ActionSequence.Count - 1);
                if (ImGui.Button("下移"))
                {
                    (actor.ActionSequence[i + 1], actor.ActionSequence[i]) = (actor.ActionSequence[i], actor.ActionSequence[i + 1]);
                    this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
                if (ImGui.Button("测试当前步骤"))
                    this.realNpcSpawn.TestActionSequenceStep(actor.RuntimeId, step.Id);
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("删除步骤"))
                {
                    actor.ActionSequence.RemoveAt(i);
                    this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
                    ImGui.TreePop();
                    ImGui.PopID();
                    break;
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        ImGui.TreePop();
    }

    private void DrawSelectedActorAppearanceEditor(RuntimeActorInstance actor, CustomNpc? npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Actor 外观");
        if (npc == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "找不到对应 NPC 模板，无法应用模板外观。");
            return;
        }

        ImGui.TextWrapped($"模板外观来源：{npc.Appearance.SourceType}");
        ImGui.TextWrapped($"模板 displayName：{npc.Appearance.DisplayName}");
        ImGui.TextWrapped($"Glamourer Design：{npc.Appearance.GlamourerDesignId}");
        ImGui.TextWrapped($"GameNpc：{npc.Appearance.GameNpcName} / baseId={npc.Appearance.GameNpcBaseId} / modelId={npc.Appearance.GameNpcModelId}");
        ImGui.TextWrapped($"Penumbra：mode={actor.PenumbraMode}, collection={this.GetCollectionDisplayName(actor.PenumbraCollectionId, actor.PenumbraCollectionNameCache)}");
        ImGui.TextWrapped($"Penumbra result：{(string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionResult) ? "未应用" : actor.LastPenumbraCollectionResult)}");
        if (!string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionError))
            ImGui.TextWrapped($"Penumbra error：{actor.LastPenumbraCollectionError}");
        ImGui.TextWrapped($"最后结果：{(string.IsNullOrWhiteSpace(actor.LastAppearanceApplyResult) ? "未应用" : actor.LastAppearanceApplyResult)}");
        ImGui.TextWrapped($"最后错误：{(string.IsNullOrWhiteSpace(actor.LastAppearanceError) ? "无" : actor.LastAppearanceError)}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceVerificationState))
            ImGui.TextWrapped($"外观状态：{actor.LastAppearanceVerificationState}，redraw fallback={actor.LastAppearanceRedrawFallbackCount}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceResidualSlots))
            ImGui.TextWrapped($"玩家装备残留槽位：{actor.LastAppearanceResidualSlots}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearancePresetSummary))
            ImGui.TextWrapped($"Preset 摘要：{actor.LastAppearancePresetSummary}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceClearEquipmentResult))
            ImGui.TextWrapped($"Clear 装备阶段：{actor.LastAppearanceClearEquipmentResult}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceValidationResult))
            ImGui.TextWrapped($"外观验证：{actor.LastAppearanceValidationResult}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceBeforeSummary))
            ImGui.TextWrapped($"Apply 前：{actor.LastAppearanceBeforeSummary}");
        if (!string.IsNullOrWhiteSpace(actor.LastLocalPlayerAppearanceSummary))
            ImGui.TextWrapped($"LocalPlayer：{actor.LastLocalPlayerAppearanceSummary}");
        if (!string.IsNullOrWhiteSpace(actor.LastAppearanceAfterSummary))
            ImGui.TextWrapped($"Apply 后：{actor.LastAppearanceAfterSummary}");

        ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
        if (ImGui.Button("应用 NPC 模板外观"))
            this.realNpcSpawn.EnqueueNpcAppearance(actor.RuntimeId);
        ImGui.SameLine();
        ImGui.BeginDisabled(npc.Appearance.SourceType != CustomNpcAppearanceSourceType.GlamourerDesign);
        if (ImGui.Button("重新应用 Glamourer design"))
            this.realNpcSpawn.EnqueueNpcAppearance(actor.RuntimeId);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(npc.Appearance.SourceType != CustomNpcAppearanceSourceType.GameNpc);
        if (ImGui.Button("重新应用 GameNpc 外观"))
            this.realNpcSpawn.EnqueueNpcAppearance(actor.RuntimeId);
        ImGui.EndDisabled();
        if (ImGui.Button("Debug：打印外观摘要"))
            this.realNpcSpawn.LogActorAppearanceDiagnostics(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("Debug：Clear 装备 + 重新套外观"))
            this.realNpcSpawn.ForceClearAndReapplyAppearance(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("Debug：Penumbra redraw + 重新套外观"))
            this.realNpcSpawn.ForceTargetedRedrawAndReapplyAppearance(actor.RuntimeId);
        ImGui.EndDisabled();
    }

    private static void DrawActorActionStepKindCombo(ActorActionSequenceStep step)
    {
        if (!ImGui.BeginCombo("Kind", step.Kind.ToString()))
            return;

        foreach (var kind in Enum.GetValues<ActorActionStepKind>())
        {
            var selected = step.Kind == kind;
            if (ImGui.Selectable(kind.ToString(), selected))
                step.Kind = kind;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawActorRigControls(RuntimeActorInstance actor)
    {
        var mode = actor.AnimationRigMode;
        if (ImGui.BeginCombo("动画骨架模式", mode.ToString()))
        {
            foreach (var value in Enum.GetValues<ActorAnimationRigMode>())
            {
                var selected = mode == value;
                if (ImGui.Selectable(value.ToString(), selected))
                {
                    actor.AnimationRigMode = value;
                    if (value == ActorAnimationRigMode.Current)
                    {
                        actor.AnimationRigPreset = ActorAnimationRigPreset.Current;
                        actor.AnimationRigStatus = "Current: using the actor's own animation data path.";
                    }
                    else
                    {
                        actor.AnimationRigStatus = "Override selected. 当前仅保存配置；点击应用会检查安全 animation-only 路径，未支持时不会写 native。";
                    }
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        DrawActorRigPresetCombo(actor);
        if (actor.AnimationRigPreset == ActorAnimationRigPreset.Custom)
        {
            var customRace = (int)actor.CustomRigRace;
            if (ImGui.InputInt("Custom Rig Race", ref customRace))
                actor.CustomRigRace = (byte)Math.Clamp(customRace, 0, byte.MaxValue);
            var customSex = (int)actor.CustomRigSex;
            if (ImGui.InputInt("Custom Rig Sex", ref customSex))
                actor.CustomRigSex = (byte)Math.Clamp(customSex, 0, byte.MaxValue);
            var customTribe = (int)actor.CustomRigTribe;
            if (ImGui.InputInt("Custom Rig Tribe/SubRace", ref customTribe))
                actor.CustomRigTribe = (byte)Math.Clamp(customTribe, 0, byte.MaxValue);
        }

        var canApplyRig = actor.IsValid && actor.CharacterObject != null;
        ImGui.BeginDisabled(!canApplyRig);
        if (ImGui.Button("应用动画骨架"))
            this.realNpcSpawn.ApplyActorAnimationRig(actor.RuntimeId);
        if (!canApplyRig && ImGui.IsItemHovered())
            ImGui.SetTooltip("当前 Actor 无效或已删除。");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
        if (ImGui.Button("恢复当前 Actor 原始骨架"))
            this.realNpcSpawn.RestoreActorAnimationRig(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("重新播放当前动画"))
            this.realNpcSpawn.ReapplyActorCurrentAnimation(actor.RuntimeId);
        ImGui.EndDisabled();
    }

    private static void DrawActorRigPresetCombo(RuntimeActorInstance actor)
    {
        var preset = actor.AnimationRigPreset;
        if (!ImGui.BeginCombo("动画骨架 / Rig", preset.ToString()))
            return;

        foreach (var value in Enum.GetValues<ActorAnimationRigPreset>())
        {
            var selected = preset == value;
            if (ImGui.Selectable(value.ToString(), selected))
            {
                actor.AnimationRigPreset = value;
                actor.AnimationRigMode = value == ActorAnimationRigPreset.Current
                    ? ActorAnimationRigMode.Current
                    : ActorAnimationRigMode.Override;
                actor.AnimationRigStatus = value == ActorAnimationRigPreset.Current
                    ? "Current: using the actor's own animation data path."
                    : "Override selected. 当前仅保存配置；点击应用会检查安全 animation-only 路径，未支持时不会写 native。";
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawActorActionStepAnimationOptions(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        var loopAnimation = step.LoopAnimation;
        if (ImGui.Checkbox("LoopAnimation", ref loopAnimation))
            step.LoopAnimation = loopAnimation;
        ImGui.SameLine();
        var stayInPose = step.StayInPose;
        if (ImGui.Checkbox("StayInPose", ref stayInPose))
            step.StayInPose = stayInPose;

        var repeatAfter = step.RepeatAfterSeconds;
        if (ImGui.InputFloat("RepeatAfterSeconds", ref repeatAfter))
            step.RepeatAfterSeconds = Math.Max(0f, repeatAfter);

        var expressionId = (int)step.ExpressionId;
        if (ImGui.InputInt("表情ID / ExpressionTimelineId", ref expressionId))
            step.ExpressionId = (ushort)Math.Clamp(expressionId, 0, ushort.MaxValue);
        ImGui.SameLine();
        this.DrawAnimationPickerButton("##StepExpressionPicker", ActorAnimationPickerRequest.ForStepExpression(actor.RuntimeId, step.Id, ActorAnimationPickerMode.ExpressionCandidates));

        var playExpression = step.PlayExpressionWithAction;
        if (ImGui.Checkbox("随动作播放表情", ref playExpression))
            step.PlayExpressionWithAction = playExpression;
        ImGui.SameLine();
        var loopExpression = step.LoopExpression;
        if (ImGui.Checkbox("LoopExpression", ref loopExpression))
            step.LoopExpression = loopExpression;

        DrawExpressionLayerCombo(step);

        var expressionDelay = step.ExpressionDelaySeconds;
        if (ImGui.InputFloat("ExpressionDelaySeconds", ref expressionDelay))
            step.ExpressionDelaySeconds = Math.Max(0f, expressionDelay);
        var expressionDuration = step.ExpressionDurationSeconds;
        if (ImGui.InputFloat("ExpressionDurationSeconds", ref expressionDuration))
            step.ExpressionDurationSeconds = Math.Max(0f, expressionDuration);
        var expressionWeight = step.ExpressionWeight;
        if (ImGui.SliderFloat("ExpressionWeight", ref expressionWeight, 0f, 1f))
            step.ExpressionWeight = Math.Clamp(expressionWeight, 0f, 1f);

        ImGui.TextDisabled("提示：表情会交给游戏 ActionTimeline slot 自动归位；不是所有 ID 都能与基础动作叠加。");
    }

    private static void DrawExpressionLayerCombo(ActorActionSequenceStep step)
    {
        if (!ImGui.BeginCombo("ExpressionLayer", step.ExpressionLayer.ToString()))
            return;

        foreach (var layer in Enum.GetValues<ActorExpressionLayer>())
        {
            var selected = step.ExpressionLayer == layer;
            if (ImGui.Selectable(layer.ToString(), selected))
                step.ExpressionLayer = layer;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawActorActionStepBubbleOptions(ActorActionSequenceStep step)
    {
        var allowLookAt = step.AllowLookAtDuringStep;
        if (ImGui.Checkbox("此步骤允许 NativeLookAt", ref allowLookAt))
            step.AllowLookAtDuringStep = allowLookAt;

        var showBubble = step.ShowBubbleOnEnter;
        if (ImGui.Checkbox("进入步骤时显示原生气泡", ref showBubble))
            step.ShowBubbleOnEnter = showBubble;
        EditString("BubbleText", step.BubbleText, 240, value => step.BubbleText = value);
        var autoDuration = step.BubbleUseAutoDuration;
        if (ImGui.Checkbox("气泡自动时长", ref autoDuration))
            step.BubbleUseAutoDuration = autoDuration;
        ImGui.SameLine();
        var bubbleDuration = step.BubbleDurationSeconds;
        if (ImGui.InputFloat("BubbleDurationSeconds", ref bubbleDuration))
            step.BubbleDurationSeconds = Math.Max(0f, bubbleDuration);
    }

    private void DrawAnimationPickerButton(string id, ActorAnimationPickerRequest request)
    {
        if (ImGui.SmallButton($"🔎{id}"))
        {
            this.actorAnimationPicker.Open(request);
            this.actionTimelinePickerWindow.IsOpen = true;
        }
    }

    private void DrawActorBatchSpawnControls(CustomNpc npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "从模板批量生成 Actor");
        ImGui.InputInt("生成数量", ref this.actorBatchCount);
        this.actorBatchCount = Math.Clamp(this.actorBatchCount, 1, 50);
        ImGui.InputFloat("Actor 间距 X", ref this.actorBatchOffset.X);
        ImGui.InputFloat("Actor 间距 Y", ref this.actorBatchOffset.Y);
        ImGui.InputFloat("Actor 间距 Z", ref this.actorBatchOffset.Z);
        ImGui.Checkbox("批量起点直接使用玩家当前位置（忽略模板默认偏移）", ref this.actorBatchUsePlayerPosition);
        var basePosition = this.GetActorBatchBasePosition(npc);
        ImGui.TextWrapped($"批量起点：{FormatVector(basePosition)}");

        ImGui.BeginDisabled(!this.realNpcSpawn.CanSpawnRealActor || (this.actorBatchUsePlayerPosition && !this.runtime.PlayerPosition.HasValue));
        if (ImGui.Button("生成多个真实 Actor"))
            this.SpawnMultipleActors(npc);
        ImGui.EndDisabled();
    }

    private Vector3 GetActorBatchBasePosition(CustomNpc npc)
    {
        if (this.actorBatchUsePlayerPosition && this.runtime.PlayerPosition.HasValue)
            return this.runtime.PlayerPosition.Value;

        return (this.runtime.PlayerPosition ?? Vector3.Zero) + ToVector3(npc.DefaultSpawnOffset);
    }

    private void SpawnMultipleActors(CustomNpc npc)
    {
        var basePosition = this.GetActorBatchBasePosition(npc);
        this.realNpcSpawn.QueueSpawnMany(npc, this.actorBatchCount, basePosition, this.actorBatchOffset);
    }

    private void DeleteSelectedActor(RuntimeActorInstance actor)
    {
        var actors = this.realNpcSpawn.Actors.ToList();
        var selectedIndex = actors.FindIndex(item => string.Equals(item.RuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase));
        var deleted = this.realNpcSpawn.Despawn(actor.RuntimeId, DespawnReason.UserRequested);
        var remaining = this.realNpcSpawn.Actors.ToList();
        if (remaining.Count == 0)
        {
            this.selectedActorRuntimeId = string.Empty;
            return;
        }

        var nextIndex = Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, remaining.Count - 1);
        this.selectedActorRuntimeId = remaining[nextIndex].RuntimeId;
        if (!deleted)
            this.realNpcSpawn.SetMessage($"删除 Actor 失败或只完成了本地移除：{actor.LastError}");
    }

    private void DrawLocalLayoutObjects()
    {
        ImGui.TextWrapped("Slot-backed 复制：占用当前地图已有 BgPart slot 作为 carrier，可恢复；不会真正新增 LayoutInstance。");
        ImGui.TextWrapped($"状态：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"模型状态：{this.localLayoutObjects.LastModelOverrideStatus}");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "v9.8 静态稳定版：动态 BgPart / SharedGroup / controller 驱动物体暂不支持，命中风险对象时会拒绝创建。");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "custom mdl 使用 DestroyPrimary -> CreatePrimary；不会调用 SetModel。");
        ImGui.TextWrapped($"active occupied slot count：{this.localLayoutObjects.ActiveOccupiedSlotCount}");
        ImGui.TextWrapped($"duplicate slot count：{this.localLayoutObjects.DuplicateSlotCount}");
        ImGui.TextWrapped($"恢复/清理忙碌状态：{this.localLayoutObjects.IsBusy}");
        ImGui.TextWrapped($"批量创建队列：active={this.localLayoutObjects.IsCreateQueueActive}; pending={this.localLayoutObjects.PendingCreateQueueLength}; current={this.localLayoutObjects.CreateQueueCurrentIndex}/{this.localLayoutObjects.CreateQueueTotalCount}; waiting={this.localLayoutObjects.CreateQueueWaitingStabilizeCount}; success={this.localLayoutObjects.CreateQueueSuccessCount}; failed={this.localLayoutObjects.CreateQueueFailedCount}; reserved={this.localLayoutObjects.ReservedSlotCount}");
        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.CreateQueueCurrentState))
            ImGui.TextWrapped($"当前创建 job：state={this.localLayoutObjects.CreateQueueCurrentState}; slot={this.localLayoutObjects.CreateQueueCurrentSlot}");
        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.CreateQueueLastError))
            ImGui.TextWrapped($"批量创建最后错误：{this.localLayoutObjects.CreateQueueLastError}");

        var unsafeEnabled = this.realNpcSpawn.EnableUnsafeNativeWrites;
        if (ImGui.Checkbox("启用 Unsafe/native 写入", ref unsafeEnabled))
            this.realNpcSpawn.EnableUnsafeNativeWrites = unsafeEnabled;
        if (ImGui.Checkbox("模型和碰撞体一起变化（危险）", ref this.localLayoutFullCollisionMode) && !this.localLayoutFullCollisionMode)
            this.confirmFullLayoutCollisionMode = false;
        if (this.localLayoutFullCollisionMode)
        {
            ImGui.TextColored(new Vector4(1f, 0.25f, 0.20f, 1f), "会移动碰撞体，其他玩家可能看到异常浮空。");
            ImGui.Checkbox("我确认启用危险 FullLayoutWithCollision 模式", ref this.confirmFullLayoutCollisionMode);
        }

        this.DrawBgPartSelectionControls();
        this.DrawLayoutTemplateControls();

        var candidate = this.GetSelectedBgPart();
        var mode = this.localLayoutFullCollisionMode ? LocalLayoutTransformMode.FullLayoutWithCollision : LocalLayoutTransformMode.VisualOnly;
        var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive || !this.realNpcSpawn.EnableUnsafeNativeWrites || candidate == null || !this.runtime.PlayerPosition.HasValue || fullLayoutBlocked);
        if (ImGui.Button(this.localLayoutFullCollisionMode ? "从候选创建本地物件（FullLayoutWithCollision，危险）" : "从候选创建本地物件（VisualOnly，推荐）"))
        {
            var desiredScale = this.layoutCopyUseTemplateScale && candidate != null
                ? candidate.Scale
                : this.layoutCopyDefaultScale;
            var created = this.localLayoutObjects.CreateCopyFromTemplate(
                candidate,
                this.AllBgParts(),
                this.runtime.PlayerPosition!.Value,
                mode,
                CarrierAllocationPolicy.PreferredListThenAnyValid,
                this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.confirmFullLayoutCollisionMode,
                this.layoutCopyDefaultRotationEuler,
                desiredScale);
            if (created != null)
                this.selectedLocalLayoutObjectId = created.Id;
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || this.localLayoutObjects.Instances.Count == 0);
        if (ImGui.Button("恢复全部"))
        {
            this.localLayoutObjects.RestoreAll(
                bgParts: this.AllBgParts(),
                unsafeEnabled: this.realNpcSpawn.EnableUnsafeNativeWrites,
                fullLayoutConfirmed: this.confirmFullLayoutCollisionMode);
            this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("会自动清理动态残留 registry 和重复实例");
        ImGui.SameLine();
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || this.localLayoutObjects.Instances.Count == 0);
        if (ImGui.Button("一键清理重复实例"))
        {
            this.localLayoutObjects.CleanupDuplicateInstances(auto: false);
            if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();
        if (ImGui.CollapsingHeader("高级恢复工具"))
        {
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy);
            if (ImGui.Button("重建 occupied registry"))
                this.localLayoutObjects.RebuildOccupiedSlotRegistryForUi();
            ImGui.SameLine();
            if (ImGui.Button("RestoreAll Dry Run"))
                this.localLayoutObjects.BuildRestorePlanPreview();
            ImGui.SameLine();
            if (ImGui.Button("清理已恢复/无效实例"))
            {
                this.localLayoutObjects.ClearRestoredAndInvalidInstances();
                if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                    this.selectedLocalLayoutObjectId = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.Button("强制清理坏实例"))
            {
                this.localLayoutObjects.ForceClearBadInstances();
                if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                    this.selectedLocalLayoutObjectId = string.Empty;
            }
            ImGui.EndDisabled();
        }
        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.LastRestorePlanPreview) && ImGui.CollapsingHeader("RestoreAll 计划预览"))
            ImGui.TextWrapped(this.localLayoutObjects.LastRestorePlanPreview);

        this.DrawLocalLayoutObjectTable();
        this.DrawSelectedLocalLayoutObjectControls();
    }

    private void DrawLayoutTemplateControls()
    {
        var template = this.GetTemplateBgPart();
        var candidate = this.GetSelectedBgPart();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "复制模板");
        ImGui.TextWrapped(template == null ? "当前模板：无" : $"template resourcePath：{template.ResourcePath}");
        ImGui.TextWrapped(template == null ? "template slotAddress：无" : $"template slotAddress：{template.Address}");
        ImGui.TextWrapped(template == null ? "template transform：无" : $"template transform：pos {FormatVector(template.Position)} | rot {template.Rotation} | scale {FormatVector(template.Scale)}");
        ImGui.TextWrapped(template == null ? "template 是否被排除：未设置模板" : "template 是否被排除：是");
        if (template != null)
        {
            var templateRisk = this.localLayoutObjects.GetCarrierRejectReason(template);
            if (!string.IsNullOrWhiteSpace(templateRisk))
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), $"模板风险：{templateRisk}。模板只作为外观/路径参考，不会占用或修改本体；动态效果不会复制。");
        }
        ImGui.BeginDisabled(candidate == null);
        if (ImGui.Button("设为复制模板") && candidate != null)
        {
            this.templateBgPartAddress = candidate.Address;
            this.layoutBatchCustomMdlPath = candidate.ResourcePath;
            if (!this.layoutUseManualBasePosition)
                this.layoutManualBasePosition = this.runtime.PlayerPosition ?? candidate.Position;
        }
        ImGui.EndDisabled();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "创建复制体");
        ImGui.InputInt("创建数量 N", ref this.layoutCopyCount);
        this.layoutCopyCount = Math.Clamp(this.layoutCopyCount, 1, 100);
        ImGui.InputFloat("间距 X", ref this.layoutCopySpacing);
        ImGui.InputFloat("间距 Y", ref this.layoutCopySpacingY);
        ImGui.InputFloat("间距 Z", ref this.layoutCopySpacingZ);
        DrawEnumCombo("默认 collision 模式", this.layoutCopyDefaultMode, value => this.layoutCopyDefaultMode = value);
        if (this.layoutCopyDefaultMode == LocalLayoutTransformMode.FullLayoutWithCollision)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "FullLayoutWithCollision 会让复制体的 collision 随模型一起移动，需要上方二次确认。");
        var copyRotationDegrees = new Vector3(
            RadiansToDegrees(this.layoutCopyDefaultRotationEuler.X),
            RadiansToDegrees(this.layoutCopyDefaultRotationEuler.Y),
            RadiansToDegrees(this.layoutCopyDefaultRotationEuler.Z));
        if (ImGui.InputFloat("默认 Pitch X (deg)", ref copyRotationDegrees.X))
            this.layoutCopyDefaultRotationEuler.X = DegreesToRadians(copyRotationDegrees.X);
        if (ImGui.InputFloat("默认 Yaw Y (deg)", ref copyRotationDegrees.Y))
            this.layoutCopyDefaultRotationEuler.Y = DegreesToRadians(copyRotationDegrees.Y);
        if (ImGui.InputFloat("默认 Roll Z (deg)", ref copyRotationDegrees.Z))
            this.layoutCopyDefaultRotationEuler.Z = DegreesToRadians(copyRotationDegrees.Z);
        var copyDefaultScale = this.layoutCopyDefaultScale;
        ImGui.Checkbox("使用模板缩放", ref this.layoutCopyUseTemplateScale);
        ImGui.TextWrapped(this.layoutCopyUseTemplateScale
            ? "新复制体会继承模板 scale。"
            : "新复制体默认 scale = X 1, Y 1, Z 1；下面输入框可手动改默认 scale。");
        if (ImGui.InputFloat("默认 Scale X", ref copyDefaultScale.X)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        copyDefaultScale = this.layoutCopyDefaultScale;
        if (ImGui.InputFloat("默认 Scale Y", ref copyDefaultScale.Y)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        copyDefaultScale = this.layoutCopyDefaultScale;
        if (ImGui.InputFloat("默认 Scale Z", ref copyDefaultScale.Z)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        EditString("批量默认 custom mdl path（可留空）", this.layoutBatchCustomMdlPath, 512, value => this.layoutBatchCustomMdlPath = value);
        ImGui.TextWrapped("留空时使用模板 resourcePath；填写 bg/...mdl 或 bgcommon/...mdl 时，每个复制体会独立应用该 mdl。");
        var usePlayerBase = !this.layoutUseManualBasePosition;
        if (ImGui.RadioButton("起始位置：玩家当前位置", usePlayerBase))
            this.layoutUseManualBasePosition = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("起始位置：手动 XYZ", this.layoutUseManualBasePosition))
        {
            this.layoutUseManualBasePosition = true;
            if (this.layoutManualBasePosition == Vector3.Zero)
                this.layoutManualBasePosition = this.runtime.PlayerPosition ?? template?.Position ?? Vector3.Zero;
        }
        if (this.layoutUseManualBasePosition)
        {
            var manual = this.layoutManualBasePosition;
            if (ImGui.InputFloat("起始 X", ref manual.X)) this.layoutManualBasePosition = manual;
            if (ImGui.InputFloat("起始 Y", ref manual.Y)) this.layoutManualBasePosition = manual;
            if (ImGui.InputFloat("起始 Z", ref manual.Z)) this.layoutManualBasePosition = manual;
            if (ImGui.Button("手动起点设为玩家当前位置") && this.runtime.PlayerPosition.HasValue)
                this.layoutManualBasePosition = this.runtime.PlayerPosition.Value;
        }
        this.allowDifferentResourcePathSlots = true;
        ImGui.TextWrapped("Carrier 分配顺序：同模型 -> 优先改动列表 -> 其他可用 BgPart。保护列表永远最高优先级。");
        ImGui.TextWrapped("Floor/Wall/Terrain/SharedGroup 等只作为 warning，不再阻止 AnyValidBgPart fallback；不想被改动的对象请加入 BgPart 保护列表。");
        ImGui.Checkbox("可用不足时尽可能创建", ref this.createAsManyAsPossible);
        var mode = this.layoutCopyDefaultMode;
        var fullLayoutBlocked = mode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.confirmFullLayoutCollisionMode;
        var hasBasePosition = this.layoutUseManualBasePosition || this.runtime.PlayerPosition.HasValue;
        var basePosition = this.layoutUseManualBasePosition
            ? this.layoutManualBasePosition
            : this.runtime.PlayerPosition ?? Vector3.Zero;
        var allBgParts = this.AllBgParts().ToList();
        var availableSlots = allBgParts.Where(slot => !this.localLayoutObjects.IsSlotOccupied(slot.Address)).ToList();
        var hasBatchMdl = !string.IsNullOrWhiteSpace(this.layoutBatchCustomMdlPath);
        var sameResourceAvailable = template == null
            ? 0
            : availableSlots.Count(slot => !string.Equals(slot.Address, template.Address, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase));
        var anySupportedAvailable = template == null
            ? 0
            : availableSlots.Count(slot => !string.Equals(slot.Address, template.Address, StringComparison.OrdinalIgnoreCase)
                && IsSupportedMdlPath(slot.ResourcePath));
        ImGui.TextWrapped(template == null
            ? "同 resourcePath 可用 slot：请先设置模板。"
            : $"模板 resourcePath：{template.ResourcePath}");
        ImGui.TextWrapped($"同 resourcePath 空闲 slot 初筛：{sameResourceAvailable}；bg/bgcommon 空闲 slot 初筛：{anySupportedAvailable}。实际可用数量以 Dry Run 的安全 carrier 统计为准。");
        if (ImGui.Button("CreateMany Dry Run Preview"))
            this.localLayoutObjects.BuildCreateManyDryRunPreview(template, allBgParts, this.layoutCopyCount, this.allowDifferentResourcePathSlots, this.layoutBatchCustomMdlPath, CarrierAllocationPolicy.PreferredListThenAnyValid, this.runtime.PlayerPosition);
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(this.localLayoutObjects.LastAllocationPlanId)
            ? "当前没有可用 AllocationPlan；创建 N 个前请先 Dry Run。"
            : $"当前 AllocationPlan：{this.localLayoutObjects.LastAllocationPlanId}");
        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.LastCreateManyDryRunPreview) && ImGui.CollapsingHeader("CreateMany 分配预览"))
            ImGui.TextWrapped(this.localLayoutObjects.LastCreateManyDryRunPreview);

        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive || !this.realNpcSpawn.EnableUnsafeNativeWrites || template == null || !hasBasePosition || fullLayoutBlocked);
        if (ImGui.Button(mode == LocalLayoutTransformMode.VisualOnly ? "创建 N 个 VisualOnly 复制体" : "创建 N 个 FullLayoutWithCollision 复制体"))
            this.CreateMany(template, allBgParts, this.layoutCopyCount, basePosition, mode);
        ImGui.EndDisabled();
    }

    private void CreateMany(LayoutProbeInstance? template, IReadOnlyList<LayoutProbeInstance> slots, int count, Vector3 basePosition, LocalLayoutTransformMode mode)
    {
        var created = this.localLayoutObjects.CreateManyFromTemplate(
            template,
            slots,
            count,
            basePosition,
            mode,
            new Vector3(this.layoutCopySpacing, this.layoutCopySpacingY, this.layoutCopySpacingZ),
            this.allowDifferentResourcePathSlots,
            this.layoutBatchCustomMdlPath,
            this.AllBgParts(),
            this.realNpcSpawn.EnableUnsafeNativeWrites,
            this.confirmFullLayoutCollisionMode,
            this.layoutCopyDefaultRotationEuler,
            this.layoutCopyUseTemplateScale && template != null ? template.Scale : this.layoutCopyDefaultScale,
            CarrierAllocationPolicy.PreferredListThenAnyValid,
            this.createAsManyAsPossible,
            this.runtime.PlayerPosition);
        var last = created.LastOrDefault();
        if (last != null)
            this.selectedLocalLayoutObjectId = last.Id;
    }

    private void DrawBgPartSelectionControls()
    {
        ImGui.Separator();
        if (ImGui.Button("重新扫描 BgPart"))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);
        ImGui.SameLine();
        if (ImGui.Button("选择最近 BgPart"))
        {
            var nearest = this.FilteredBgParts().FirstOrDefault();
            if (nearest != null)
            {
                this.selectedBgPartAddress = nearest.Address;
                this.layerDump.SelectReusableCandidate(nearest);
            }
        }

        var candidate = this.GetSelectedBgPart();
        ImGui.TextWrapped(candidate == null
            ? "当前选中 BgPart：无"
            : $"当前选中 BgPart：{candidate.ResourcePath} | {candidate.Address} | source={candidate.SourceKind} | parent={candidate.ParentAddress} | child={candidate.ChildIndex} | 距离 {candidate.DistanceToPlayer:F1}y | {FormatVector(candidate.Position)}");
        if (candidate != null)
        {
            var carrierReject = this.localLayoutObjects.GetCarrierRejectReason(candidate, CarrierAllocationPolicy.PreferredListThenAnyValid);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(carrierReject)
                ? "carrier 状态：可作为静态 carrier"
                : $"carrierRejectReason：{carrierReject}");
            var carrierWarning = this.localLayoutObjects.GetCarrierWarningReason(candidate);
            if (!string.IsNullOrWhiteSpace(carrierWarning))
                ImGui.TextWrapped($"carrierWarningReason：{carrierWarning}");
            this.DrawProtectedBgPartControls(candidate);
            this.DrawPreferredModifyBgPartControls(candidate);
        }
        ImGui.InputText("搜索 resourcePath/type", ref this.bgPartSearchText, 256);

        var rows = this.FilteredBgParts().Take(80).ToList();
        if (!ImGui.BeginTable("BgPartSelectionTable", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("distance");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("type");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("visible");
        ImGui.TableSetupColumn("address");
        ImGui.TableSetupColumn("parent shared group");
        ImGui.TableSetupColumn("child");
        ImGui.TableSetupColumn("carrier reject");
        ImGui.TableSetupColumn("carrier warning");
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID(item.Address);
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable("选为候选", string.Equals(this.selectedBgPartAddress, item.Address, StringComparison.OrdinalIgnoreCase)))
            {
                this.selectedBgPartAddress = item.Address;
                this.layerDump.SelectReusableCandidate(item);
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{item.DistanceToPlayer:F1}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(item.SourceKind);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(item.Type);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(item.Visible ? "是" : "否");
            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped(item.Address);
            ImGui.TableSetColumnIndex(7);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(item.ParentAddress) ? "-" : $"{item.ParentAddress} | {item.SharedGroupPath}");
            ImGui.TableSetColumnIndex(8);
            ImGui.TextUnformatted(item.ChildIndex >= 0 ? item.ChildIndex.ToString() : "-");
            ImGui.TableSetColumnIndex(9);
            ImGui.TextWrapped(this.localLayoutObjects.GetCarrierRejectReason(item, CarrierAllocationPolicy.PreferredListThenAnyValid));
            ImGui.TableSetColumnIndex(10);
            ImGui.TextWrapped(this.localLayoutObjects.GetCarrierWarningReason(item));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawLocalLayoutObjectTable()
    {
        if (!ImGui.BeginTable("LocalLayoutObjects", 17, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 320f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("instanceId");
        ImGui.TableSetupColumn("slotAddress");
        ImGui.TableSetupColumn("templateResource");
        ImGui.TableSetupColumn("originalSlotResourcePath");
        ImGui.TableSetupColumn("currentModelPath");
        ImGui.TableSetupColumn("custom mdl path");
        ImGui.TableSetupColumn("state");
        ImGui.TableSetupColumn("applyMdlStatus");
        ImGui.TableSetupColumn("restoreStatus");
        ImGui.TableSetupColumn("lastError");
        ImGui.TableSetupColumn("mode");
        ImGui.TableSetupColumn("restored");
        ImGui.TableSetupColumn("position");
        ImGui.TableSetupColumn("scale");
        ImGui.TableSetupColumn("应用");
        ImGui.TableSetupColumn("删除/恢复");
        ImGui.TableHeadersRow();
        var deleteId = string.Empty;
        foreach (var instance in this.localLayoutObjects.Instances)
        {
            ImGui.TableNextRow();
            ImGui.PushID(instance.Id);
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable("选中", string.Equals(this.selectedLocalLayoutObjectId, instance.Id, StringComparison.Ordinal)))
                this.selectedLocalLayoutObjectId = instance.Id;
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(instance.InstanceId);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(instance.OccupiedSlotAddress);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(instance.TemplateResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(instance.OriginalSlotResourcePath);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(instance.CurrentModelPath);
            ImGui.TableSetColumnIndex(6);
            var rowCustomPath = instance.CustomModelPath;
            if (ImGui.InputText("##rowCustomMdl", ref rowCustomPath, 320))
                instance.CustomModelPath = rowCustomPath;
            ImGui.TableSetColumnIndex(7);
            ImGui.TextWrapped(instance.InstanceState);
            ImGui.TableSetColumnIndex(8);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(instance.ApplyMdlStatus) ? "未应用" : instance.ApplyMdlStatus);
            ImGui.TableSetColumnIndex(9);
            ImGui.TextWrapped(instance.RestoreStatus);
            ImGui.TableSetColumnIndex(10);
            ImGui.TextWrapped(FirstNonEmpty(instance.ApplyMdlError, instance.LastError, instance.LastModelOverrideError));
            ImGui.TableSetColumnIndex(11);
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || instance.IsRestored || instance.IsInvalid || instance.IsDuplicate || !this.realNpcSpawn.EnableUnsafeNativeWrites);
            DrawEnumCombo("##rowCollisionMode", instance.TransformMode, value =>
            {
                if (!value.Equals(instance.TransformMode))
                {
                    this.localLayoutObjects.ChangeCollisionMode(
                        instance.Id,
                        value,
                        this.FilteredBgParts(),
                        this.realNpcSpawn.EnableUnsafeNativeWrites,
                        this.confirmFullLayoutCollisionMode);
                }
            });
            ImGui.EndDisabled();
            ImGui.TableSetColumnIndex(12);
            ImGui.TextUnformatted(instance.IsRestored ? "是" : "否");
            ImGui.TableSetColumnIndex(13);
            ImGui.TextWrapped(FormatVector(instance.CurrentPosition));
            ImGui.TableSetColumnIndex(14);
            ImGui.TextWrapped(FormatVector(instance.CurrentScale));
            ImGui.TableSetColumnIndex(15);
            var fullLayoutNeedsConfirmation = instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.confirmFullLayoutCollisionMode;
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid || instance.IsDuplicate || instance.IsRenderInvalid || fullLayoutNeedsConfirmation);
            if (ImGui.Button("应用 mdl"))
                this.localLayoutObjects.ApplyMdlPath(instance.Id, instance.CustomModelPath, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
            ImGui.EndDisabled();
            ImGui.TableSetColumnIndex(16);
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid);
            if (ImGui.Button("恢复原 mdl/transform"))
                this.localLayoutObjects.RestoreModelAndTransform(instance.Id, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("删除"))
                deleteId = instance.Id;
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (!string.IsNullOrWhiteSpace(deleteId))
        {
            this.localLayoutObjects.Delete(deleteId);
            if (string.Equals(this.selectedLocalLayoutObjectId, deleteId, StringComparison.Ordinal))
                this.selectedLocalLayoutObjectId = string.Empty;
        }
    }

    private void DrawProtectedBgPartControls(LayoutProbeInstance candidate)
    {
        var registry = this.localLayoutObjects.ProtectedBgParts;
        if (registry == null)
            return;

        var isProtected = registry.IsProtected(candidate, out var protectedReason);
        ImGui.TextWrapped(isProtected
            ? $"保护状态：已保护 | {protectedReason}"
            : "保护状态：未保护");

        if (ImGui.Button("保护当前选中 BgPart slot"))
            registry.ProtectSlot(candidate, "User protected from LocalQuestReborn UI");
        ImGui.SameLine();
        if (ImGui.Button("取消保护当前 slot"))
            registry.UnprotectSlot(candidate);
        if (ImGui.Button("保护当前 resourcePath 全部同款"))
            registry.ProtectResourcePath(candidate.ResourcePath, currentTerritoryOnly: true, "User protected resourcePath from LocalQuestReborn UI");
        ImGui.SameLine();
        if (ImGui.Button("取消保护当前 resourcePath"))
            registry.UnprotectResourcePath(candidate.ResourcePath);

        ImGui.TextDisabled("完整保护列表、搜索、移除和清空在顶部页签：BgPart 保护列表。");
    }

    private void DrawBgPartProtectionTab()
    {
        var registry = this.localLayoutObjects.ProtectedBgParts;
        if (registry == null)
        {
            ImGui.TextWrapped("BgPart 保护列表服务不可用。");
            return;
        }

        ImGui.TextWrapped("这里集中管理不会被 slot-backed 复制占用、recreate、改 mdl 或移动 transform 的 BgPart。被保护对象仍可作为只读模板 source。");
        ImGui.TextWrapped($"protected slots：{registry.ProtectedSlots.Count}; protected resourcePaths：{registry.ProtectedResourcePaths.Count}");
        ImGui.InputText("搜索保护项", ref this.protectedBgPartSearchText, 256);
        ImGui.SameLine();
        if (ImGui.Button("重新扫描 BgPart"))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);

        var candidate = this.GetSelectedBgPart();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "当前选中 BgPart 快捷保护");
        if (candidate == null)
        {
            ImGui.TextWrapped("当前没有选中 BgPart。可以在“本地场景物体”或“BgPart Slot Pool”里选一个。");
        }
        else
        {
            ImGui.TextWrapped($"{candidate.ResourcePath} | {candidate.Address} | source={candidate.SourceKind} | child={candidate.ChildIndex} | pos={FormatVector(candidate.Position)}");
            this.DrawProtectedBgPartControls(candidate);
        }

        ImGui.Separator();
        ImGui.BeginDisabled(registry.ProtectedSlots.Count == 0 && registry.ProtectedResourcePaths.Count == 0);
        if (ImGui.Button("清空保护列表"))
            registry.Clear();
        ImGui.EndDisabled();

        this.DrawProtectedResourcePathTable(registry);
        this.DrawProtectedSlotTable(registry);
    }

    private void DrawProtectedResourcePathTable(ProtectedBgPartRegistry registry)
    {
        var rows = registry.ProtectedResourcePaths
            .Where(item => this.MatchesProtectedBgPartSearch(item.ResourcePath, item.Note, item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped($"resourcePath 保护：显示 {rows.Count}/{registry.ProtectedResourcePaths.Count}");
        if (!ImGui.BeginTable("ProtectedBgPartResourcePaths", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn("scope");
        ImGui.TableSetupColumn("territory");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("note");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        ProtectedBgPartResourcePath? removeItem = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"protected-resource-{i}-{item.ResourcePath}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.AppliesToCurrentTerritoryOnly ? "当前地图" : "全部地图");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.AppliesToCurrentTerritoryOnly ? item.TerritoryType.ToString() : "all");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(4);
            if (ImGui.Button("移除"))
                removeItem = item;
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeItem != null)
            registry.RemoveResourcePathEntry(removeItem);
    }

    private void DrawProtectedSlotTable(ProtectedBgPartRegistry registry)
    {
        var rows = registry.ProtectedSlots
            .Where(item => this.MatchesProtectedBgPartSearch(
                item.ResourcePath,
                item.Note,
                item.SourceType,
                item.LayoutInstanceAddress,
                item.SharedGroupPath,
                item.StableKey,
                item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped($"slot 保护：显示 {rows.Count}/{registry.ProtectedSlots.Count}");
        if (!ImGui.BeginTable("ProtectedBgPartSlots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 260f)))
            return;

        ImGui.TableSetupColumn("territory");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("position");
        ImGui.TableSetupColumn("shared group / child");
        ImGui.TableSetupColumn("address / stableKey");
        ImGui.TableSetupColumn("note");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        ProtectedBgPartSlot? removeItem = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"protected-slot-{i}-{item.LayoutInstanceAddress}-{item.StableKey}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.TerritoryType == 0 ? "all" : item.TerritoryType.ToString());
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(item.SourceType);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(FormatVector(ToVector3(item.OriginalPosition)));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(item.SharedGroupPath) ? $"child={item.ChildIndex}" : $"{item.SharedGroupPath} | child={item.ChildIndex}");
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped($"{item.LayoutInstanceAddress} | {item.StableKey}");
            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(7);
            if (ImGui.Button("移除"))
                removeItem = item;
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeItem != null)
            registry.RemoveSlotEntry(removeItem);
    }

    private bool MatchesProtectedBgPartSearch(params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(this.protectedBgPartSearchText))
            return true;

        var query = this.protectedBgPartSearchText.Trim();
        return values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawPreferredModifyBgPartControls(LayoutProbeInstance candidate)
    {
        var registry = this.localLayoutObjects.PreferredModifyBgParts;
        if (registry == null)
            return;

        var isPreferred = registry.IsPreferred(candidate, out var preferredReason);
        ImGui.TextWrapped(isPreferred
            ? $"优先改动状态：已加入 | {preferredReason}"
            : "优先改动状态：未加入");

        if (ImGui.Button("加入优先改动列表：当前 slot"))
            registry.ProtectSlot(candidate, "User preferred slot from LocalQuestReborn UI");
        ImGui.SameLine();
        if (ImGui.Button("从优先改动列表移除：当前 slot"))
            registry.UnprotectSlot(candidate);
        if (ImGui.Button("加入优先改动列表：当前 resourcePath"))
            registry.ProtectResourcePath(candidate.ResourcePath, currentTerritoryOnly: true, "User preferred resourcePath from LocalQuestReborn UI");
        ImGui.SameLine();
        if (ImGui.Button("从优先改动列表移除：当前 resourcePath"))
            registry.UnprotectResourcePath(candidate.ResourcePath);

        ImGui.TextDisabled("完整优先改动列表在顶部页签：BgPart 优先改动列表。");
    }

    private void DrawBgPartPreferredModifyTab()
    {
        var registry = this.localLayoutObjects.PreferredModifyBgParts;
        if (registry == null)
        {
            ImGui.TextWrapped("BgPart 优先改动列表服务不可用。");
            return;
        }

        ImGui.TextWrapped("优先改动列表会在同模型 carrier 不足时优先被选为 carrier。保护列表优先级更高：同一 BgPart 如果被保护，仍然不会被改动。");
        ImGui.TextWrapped($"preferred slots：{registry.PreferredSlots.Count}; preferred resourcePaths：{registry.PreferredResourcePaths.Count}");
        ImGui.InputText("搜索优先改动项", ref this.preferredModifyBgPartSearchText, 256);
        ImGui.SameLine();
        if (ImGui.Button("重新扫描 BgPart"))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);

        var candidate = this.GetSelectedBgPart();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "当前选中 BgPart 快捷加入");
        if (candidate == null)
        {
            ImGui.TextWrapped("当前没有选中 BgPart。可以在“本地场景物体”或“BgPart Slot Pool”里选一个。");
        }
        else
        {
            ImGui.TextWrapped($"{candidate.ResourcePath} | {candidate.Address} | source={candidate.SourceKind} | child={candidate.ChildIndex} | pos={FormatVector(candidate.Position)}");
            this.DrawPreferredModifyBgPartControls(candidate);
        }

        ImGui.Separator();
        ImGui.BeginDisabled(registry.PreferredSlots.Count == 0 && registry.PreferredResourcePaths.Count == 0);
        if (ImGui.Button("清空优先改动列表"))
            registry.Clear();
        ImGui.EndDisabled();

        this.DrawPreferredModifyResourcePathTable(registry);
        this.DrawPreferredModifySlotTable(registry);
    }

    private void DrawPreferredModifyResourcePathTable(PreferredModifyBgPartRegistry registry)
    {
        var rows = registry.PreferredResourcePaths
            .Where(item => this.MatchesPreferredModifySearch(item.ResourcePath, item.Note, item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped($"resourcePath 优先改动：显示 {rows.Count}/{registry.PreferredResourcePaths.Count}");
        if (!ImGui.BeginTable("PreferredModifyBgPartResourcePaths", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn("scope");
        ImGui.TableSetupColumn("territory");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("note");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        PreferredModifyBgPartResourcePath? removeItem = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"preferred-resource-{i}-{item.ResourcePath}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.AppliesToCurrentTerritoryOnly ? "当前地图" : "全部地图");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.AppliesToCurrentTerritoryOnly ? item.TerritoryType.ToString() : "all");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(4);
            if (ImGui.Button("移除"))
                removeItem = item;
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeItem != null)
            registry.RemoveResourcePathEntry(removeItem);
    }

    private void DrawPreferredModifySlotTable(PreferredModifyBgPartRegistry registry)
    {
        var rows = registry.PreferredSlots
            .Where(item => this.MatchesPreferredModifySearch(
                item.ResourcePath,
                item.Note,
                item.SourceType,
                item.LayoutInstanceAddress,
                item.SharedGroupPath,
                item.StableKey,
                item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped($"slot 优先改动：显示 {rows.Count}/{registry.PreferredSlots.Count}");
        if (!ImGui.BeginTable("PreferredModifyBgPartSlots", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 260f)))
            return;

        ImGui.TableSetupColumn("territory");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("position");
        ImGui.TableSetupColumn("shared group / child");
        ImGui.TableSetupColumn("address / stableKey");
        ImGui.TableSetupColumn("note");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        PreferredModifyBgPartSlot? removeItem = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"preferred-slot-{i}-{item.LayoutInstanceAddress}-{item.StableKey}");
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.TerritoryType == 0 ? "all" : item.TerritoryType.ToString());
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(item.SourceType);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(FormatVector(ToVector3(item.OriginalPosition)));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(item.SharedGroupPath) ? $"child={item.ChildIndex}" : $"{item.SharedGroupPath} | child={item.ChildIndex}");
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped($"{item.LayoutInstanceAddress} | {item.StableKey}");
            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(7);
            if (ImGui.Button("移除"))
                removeItem = item;
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeItem != null)
            registry.RemoveSlotEntry(removeItem);
    }

    private bool MatchesPreferredModifySearch(params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(this.preferredModifyBgPartSearchText))
            return true;

        var query = this.preferredModifyBgPartSearchText.Trim();
        return values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawSelectedLocalLayoutObjectControls()
    {
        var selected = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);
        if (selected == null)
            return;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), $"选中实例：{selected.Id}");
        ImGui.TextWrapped($"template slot：{selected.TemplateSourceSlotAddress}");
        ImGui.TextWrapped($"template resource：{selected.TemplateResourcePath}");
        ImGui.TextWrapped($"template transform：{selected.TemplateTransform}");
        ImGui.TextWrapped($"occupied slot：{selected.OccupiedSlotAddress}");
        ImGui.TextWrapped($"source kind：{selected.SourceKind}");
        if (string.Equals(selected.SourceKind, "SharedGroup", StringComparison.Ordinal))
            ImGui.TextWrapped($"SharedGroup parent：{selected.SourceSharedGroupPath} | {selected.SourceParentAddress} | child #{selected.SourceChildIndex}");
        ImGui.TextWrapped($"original model：{selected.OriginalModelResourcePath}");
        ImGui.TextWrapped($"current model：{selected.CurrentResourcePath}");
        ImGui.TextWrapped($"model apply status：{(string.IsNullOrWhiteSpace(selected.ModelApplyStatus) ? selected.ApplyMdlStatus : selected.ModelApplyStatus)}");
        ImGui.TextWrapped($"instance state：{selected.InstanceState}");
        ImGui.TextWrapped($"last operation：{(string.IsNullOrWhiteSpace(selected.LastOperation) ? "无" : selected.LastOperation)}");
        ImGui.TextWrapped($"pending recreate：{selected.PendingRecreate}");
        ImGui.TextWrapped($"pending visual transform：{selected.PendingVisualTransform}，等待帧：{selected.PendingVisualTransformFrameWait}");
        ImGui.TextWrapped($"stabilize attempts：{selected.PendingRecreateStabilizeAttempts}/{selected.PendingRecreateStabilizeMaxAttempts}");
        ImGui.TextWrapped($"pending result：{selected.PendingVisualTransformResult}");
        ImGui.TextWrapped($"restore status：{(string.IsNullOrWhiteSpace(selected.RestoreStatus) ? "Pending" : selected.RestoreStatus)}");
        ImGui.TextWrapped($"restore step：{(string.IsNullOrWhiteSpace(selected.RestoreStep) ? "无" : selected.RestoreStep)}");
        ImGui.TextWrapped($"restore error：{(string.IsNullOrWhiteSpace(selected.RestoreError) ? "无" : selected.RestoreError)}");
        ImGui.TextWrapped($"after restore path：{(string.IsNullOrWhiteSpace(selected.AfterRestorePath) ? "未记录" : selected.AfterRestorePath)}");
        ImGui.TextWrapped($"after restore position：{(string.IsNullOrWhiteSpace(selected.AfterRestorePosition) ? "未记录" : selected.AfterRestorePosition)}");
        ImGui.TextWrapped($"after restore visible：{(string.IsNullOrWhiteSpace(selected.AfterRestoreVisible) ? "未记录" : selected.AfterRestoreVisible)}");
        ImGui.TextWrapped($"restore debug：{(string.IsNullOrWhiteSpace(selected.RestoreDebugInfo) ? "未记录" : selected.RestoreDebugInfo)}");
        ImGui.TextWrapped($"snapshot original path：{(string.IsNullOrWhiteSpace(selected.OriginalSlotSnapshot?.OriginalResourcePath) ? "缺失" : selected.OriginalSlotSnapshot!.OriginalResourcePath)}");
        ImGui.TextWrapped($"complex risk：{(string.IsNullOrWhiteSpace(selected.ComplexModelRisk) ? "StaticOk" : selected.ComplexModelRisk)}");
        if (!string.IsNullOrWhiteSpace(selected.ComplexModelRiskReason))
            ImGui.TextWrapped($"risk reason：{selected.ComplexModelRiskReason}");
        ImGui.TextWrapped($"is restoring：{selected.IsRestoring}");
        ImGui.TextWrapped($"original visible：{selected.OriginalVisible}；current visible：{selected.Visible}");
        if (!string.IsNullOrWhiteSpace(selected.CarrierRejectReason))
            ImGui.TextWrapped($"carrier reject reason：{selected.CarrierRejectReason}");
        if (!string.IsNullOrWhiteSpace(selected.CarrierWarningReason))
            ImGui.TextWrapped($"carrier warning reason：{selected.CarrierWarningReason}");
        ImGui.TextWrapped($"readback：{selected.LastReadback}");
        ImGui.TextWrapped($"错误：{(string.IsNullOrWhiteSpace(selected.LastError) ? "无" : selected.LastError)}");

        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || selected.IsRestored || selected.IsInvalid || selected.IsDuplicate || !this.realNpcSpawn.EnableUnsafeNativeWrites);
        DrawEnumCombo("复制体 collision 模式", selected.TransformMode, value =>
        {
            if (!value.Equals(selected.TransformMode))
            {
                this.localLayoutObjects.ChangeCollisionMode(
                    selected.Id,
                    value,
                    this.FilteredBgParts(),
                    this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.confirmFullLayoutCollisionMode);
            }
        });
        ImGui.EndDisabled();
        var selectedWriteMode = selected.TransformMode;
        var fullLayoutNeedsConfirmation = selectedWriteMode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.confirmFullLayoutCollisionMode;
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), selectedWriteMode == LocalLayoutTransformMode.VisualOnly
            ? "当前写入路径：Graphics.Scene.Object"
            : "当前写入路径：LayoutInstance transform");
        if (fullLayoutNeedsConfirmation)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "危险模式需要二次确认。");

        var editPosition = selected.CurrentPosition;
        if (ImGui.InputFloat("Position X", ref editPosition.X)) selected.CurrentPosition = editPosition;
        if (ImGui.InputFloat("Position Y", ref editPosition.Y)) selected.CurrentPosition = editPosition;
        if (ImGui.InputFloat("Position Z", ref editPosition.Z)) selected.CurrentPosition = editPosition;
        var rotationDegrees = new Vector3(RadiansToDegrees(selected.CurrentRotationEuler.X), RadiansToDegrees(selected.CurrentRotationEuler.Y), RadiansToDegrees(selected.CurrentRotationEuler.Z));
        if (ImGui.InputFloat("Pitch X (deg)", ref rotationDegrees.X)) selected.CurrentRotationEuler = new Vector3(DegreesToRadians(rotationDegrees.X), selected.CurrentRotationEuler.Y, selected.CurrentRotationEuler.Z);
        if (ImGui.InputFloat("Yaw Y (deg)", ref rotationDegrees.Y)) selected.CurrentRotationEuler = new Vector3(selected.CurrentRotationEuler.X, DegreesToRadians(rotationDegrees.Y), selected.CurrentRotationEuler.Z);
        if (ImGui.InputFloat("Roll Z (deg)", ref rotationDegrees.Z)) selected.CurrentRotationEuler = new Vector3(selected.CurrentRotationEuler.X, selected.CurrentRotationEuler.Y, DegreesToRadians(rotationDegrees.Z));
        var editScale = selected.CurrentScale;
        if (ImGui.InputFloat("Scale X", ref editScale.X)) selected.CurrentScale = Vector3.Max(editScale, new Vector3(0.01f));
        if (ImGui.InputFloat("Scale Y", ref editScale.Y)) selected.CurrentScale = Vector3.Max(editScale, new Vector3(0.01f));
        if (ImGui.InputFloat("Scale Z", ref editScale.Z)) selected.CurrentScale = Vector3.Max(editScale, new Vector3(0.01f));
        EditString("custom mdl path", selected.CustomModelPath, 512, value => selected.CustomModelPath = value);
        ImGui.TextWrapped($"render invalid：{selected.IsRenderInvalid}");
        ImGui.TextWrapped($"transform disabled reason：{selected.TransformWriteDisabledReason}");
        ImGui.TextWrapped($"transform skipped reason：{(string.IsNullOrWhiteSpace(selected.LastTransformWriteSkippedReason) ? "无" : selected.LastTransformWriteSkippedReason)}");
        ImGui.TextWrapped($"applied transform position：{(string.IsNullOrWhiteSpace(selected.AppliedTransformPosition) ? "未写入" : selected.AppliedTransformPosition)}");
        ImGui.TextWrapped($"readback immediate：{(string.IsNullOrWhiteSpace(selected.TransformReadbackImmediate) ? "未记录" : selected.TransformReadbackImmediate)}");
        ImGui.TextWrapped($"model result：{selected.LastModelOverrideResult}");
        ImGui.TextWrapped($"model error：{(string.IsNullOrWhiteSpace(selected.LastModelOverrideError) ? "无" : selected.LastModelOverrideError)}");
        ImGui.TextWrapped($"collision resolve：{selected.CollisionSourceResolveResult}");
        ImGui.TextWrapped($"collision source：{selected.CollisionSourceResourcePath} | {selected.CollisionSourceBgPartAddress}");
        ImGui.TextWrapped($"collision type：{selected.CollisionSourceColliderType} | mesh=0x{selected.CollisionSourceMeshPathCrc:X8} | analytic=0x{selected.CollisionSourceAnalyticShapeDataCrc:X8}");
        ImGui.TextWrapped($"collision applied：{selected.CollisionApplied}");
        ImGui.TextWrapped($"collision error：{(string.IsNullOrWhiteSpace(selected.CollisionError) ? "无" : selected.CollisionError)}");
        ImGui.TextWrapped($"mdl category：{(string.IsNullOrWhiteSpace(selected.ModelResourceCategoryReadback) ? "未读取" : selected.ModelResourceCategoryReadback)}");
        ImGui.TextWrapped("正式 mdl 替换使用 DestroyPrimary -> CreatePrimary；不会调用 SetModel。FullLayout 模式会自动查找 target mdl 对应 BgPart collision source。");
        ImGui.TextWrapped("支持 bg/...mdl 与 bgcommon/...mdl；其他资源类型暂不支持。");
        ImGui.TextWrapped("动态 BgPart / SharedGroup / controller 驱动物体当前版本会被拒绝，避免残留和 native 崩溃。");
        if (!string.IsNullOrWhiteSpace(selected.GraphicsSafetyDump))
            ImGui.TextWrapped($"Graphics 安全状态：{selected.GraphicsSafetyDump}");

        var disabled = this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || selected.IsDuplicate || selected.IsRestored || selected.IsRenderInvalid || fullLayoutNeedsConfirmation;
        if (selected.IsRenderInvalid)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "当前实例 render 已失效，不能再写 Graphics.Scene.Object transform。");
        ImGui.BeginDisabled(disabled);
        if (ImGui.Button("应用 transform")) this.localLayoutObjects.ApplyVisualTransform(selected.Id, selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale);
        ImGui.SameLine();
        if (ImGui.Button("移动到玩家当前位置") && this.runtime.PlayerPosition.HasValue) this.localLayoutObjects.MoveToPlayer(selected.Id, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("把模型放到玩家脚下") && this.runtime.PlayerPosition.HasValue) this.localLayoutObjects.MoveToPlayer(selected.Id, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("恢复 transform")) this.localLayoutObjects.RestoreTransformOnly(selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("应用 mdl path"))
            this.localLayoutObjects.ApplyMdlPath(selected.Id, selected.CustomModelPath, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
        ImGui.SameLine();
        if (ImGui.Button("恢复原 mdl / transform"))
            this.localLayoutObjects.RestoreModelAndTransform(selected.Id, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
        if (ImGui.Button("X+1")) this.localLayoutObjects.MoveX(selected.Id, 1f);
        ImGui.SameLine();
        if (ImGui.Button("X-1")) this.localLayoutObjects.MoveX(selected.Id, -1f);
        ImGui.SameLine();
        if (ImGui.Button("Y+1")) this.localLayoutObjects.MoveY(selected.Id, 1f);
        ImGui.SameLine();
        if (ImGui.Button("Y-1")) this.localLayoutObjects.MoveY(selected.Id, -1f);
        ImGui.SameLine();
        if (ImGui.Button("Z+1")) this.localLayoutObjects.MoveZ(selected.Id, 1f);
        ImGui.SameLine();
        if (ImGui.Button("Z-1")) this.localLayoutObjects.MoveZ(selected.Id, -1f);
        if (ImGui.Button("重置 rotation")) this.localLayoutObjects.ResetRotation(selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("重置 scale")) this.localLayoutObjects.ResetScale(selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("读取当前模型 path / Dump modelResourceHandle")) this.localLayoutObjects.RefreshModel(selected.Id);
        if (ImGui.Button("删除实例"))
        {
            this.localLayoutObjects.Delete(selected.Id);
            this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy);
        if (ImGui.Button("强制从列表移除选中实例（不写 native）"))
        {
            this.localLayoutObjects.ForceRemoveInstance(selected.Id);
            this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();
    }

    private void DrawLocalLights()
    {
        ImGui.TextWrapped("LocalLights 使用 Graphics.Scene.Light / Render.Light 创建插件自有本地灯光；不走 BgPart carrier，不占用场景物体 slot。当前仍是 Debug-first 路线，请先用 PointLight 验证 GPose 外可见性。");
        ImGui.TextWrapped($"状态：{this.localLights.LastStatus}");
        ImGui.TextWrapped($"灯光数量：{this.localLights.Instances.Count} | native 队列：{this.localLights.PendingOperationCount}");

        var unsafeEnabled = this.realNpcSpawn.EnableUnsafeNativeWrites;
        if (!unsafeEnabled)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "Unsafe/native 写入未开启，不能创建或修改 native SceneLight。");

        if (!unsafeEnabled)
            ImGui.BeginDisabled();

        if (ImGui.Button("Debug Spawn PointLight At Player"))
        {
            var instance = this.localLights.CreateDebugPointAt(this.runtime.PlayerPosition ?? Vector3.Zero);
            this.selectedLocalLightId = instance.Id;
        }

        ImGui.SameLine();
        if (ImGui.Button("创建点光"))
        {
            var instance = this.localLights.Create(LocalLightKind.Point, "Point Light", this.runtime.PlayerPosition ?? Vector3.Zero, Vector3.Zero, Vector3.One);
            this.selectedLocalLightId = instance.Id;
        }

        ImGui.SameLine();
        if (ImGui.Button("创建聚焦光"))
        {
            var instance = this.localLights.Create(LocalLightKind.Spot, "Spot Light", this.runtime.PlayerPosition ?? Vector3.Zero, Vector3.Zero, Vector3.One);
            this.selectedLocalLightId = instance.Id;
        }

        ImGui.SameLine();
        if (ImGui.Button("创建面光"))
        {
            var instance = this.localLights.Create(LocalLightKind.Area, "Area Light", this.runtime.PlayerPosition ?? Vector3.Zero, Vector3.Zero, Vector3.One);
            this.selectedLocalLightId = instance.Id;
        }

        if (!unsafeEnabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("删除全部灯光"))
        {
            this.localLights.RequestDeleteAll();
            this.selectedLocalLightId = string.Empty;
        }

        ImGui.Separator();

        var lights = this.localLights.Instances;
        if (lights.Count == 0)
        {
            ImGui.TextDisabled("还没有本地灯光。先点击 Debug Spawn PointLight At Player。");
            return;
        }

        if (!lights.Any(item => string.Equals(item.Id, this.selectedLocalLightId, StringComparison.OrdinalIgnoreCase)))
            this.selectedLocalLightId = lights[0].Id;

        var leftWidth = Math.Min(360f, Math.Max(260f, ImGui.GetContentRegionAvail().X * 0.35f));
        if (ImGui.BeginChild("LocalLightsListPanel", new Vector2(leftWidth, 0f), true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            foreach (var light in lights)
            {
                var label = $"{light.Name} | {light.LightKind} | {(light.IsNativeCreated ? "native" : "no native")}##{light.Id}";
                var selected = string.Equals(light.Id, this.selectedLocalLightId, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(label, selected))
                    this.selectedLocalLightId = light.Id;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        var selectedLight = this.localLights.GetById(this.selectedLocalLightId);
        if (selectedLight == null)
        {
            ImGui.TextDisabled("未选择灯光。");
            return;
        }

        if (ImGui.BeginChild("LocalLightsEditPanel", Vector2.Zero, true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "灯光实例");
            ImGui.TextWrapped($"Id：{selectedLight.Id}");
            ImGui.TextWrapped($"NativeSceneLight：0x{selectedLight.NativeSceneLight:X}");
            ImGui.TextWrapped($"NativeRenderLight：0x{selectedLight.NativeRenderLight:X}");
            ImGui.TextWrapped($"Last operation：{selectedLight.LastOperation}");
            if (!string.IsNullOrWhiteSpace(selectedLight.LastError))
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), $"Last error：{selectedLight.LastError}");
            if (!string.IsNullOrWhiteSpace(selectedLight.LastReadback))
                ImGui.TextWrapped($"Readback：{selectedLight.LastReadback}");

            EditString("名称##LocalLightName", selectedLight.Name, 128, value => selectedLight.Name = value);

            var enabled = selectedLight.Enabled;
            if (ImGui.Checkbox("启用 native light", ref enabled))
                this.localLights.RequestSetEnabled(selectedLight.Id, enabled);

            var hidden = selectedLight.Hidden;
            if (ImGui.Checkbox("隐藏", ref hidden))
            {
                selectedLight.Hidden = hidden;
                this.localLights.RequestApply(selectedLight.Id);
            }

            DrawLocalLightKindCombo(selectedLight);
            DrawLocalLightFalloffCombo(selectedLight);

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Transform");
            var position = selectedLight.Position;
            if (ImGui.InputFloat3("Position X/Y/Z", ref position))
                selectedLight.Position = position;
            var rotationDegrees = RadiansVectorToDegrees(selectedLight.Rotation);
            if (ImGui.InputFloat3("Rotation Pitch/Yaw/Roll (deg)", ref rotationDegrees))
                selectedLight.Rotation = DegreesVectorToRadians(rotationDegrees);
            var scale = selectedLight.Scale;
            if (ImGui.InputFloat3("Scale X/Y/Z", ref scale))
                selectedLight.Scale = scale;

            if (ImGui.Button("移动到玩家当前位置"))
            {
                selectedLight.Position = this.runtime.PlayerPosition ?? selectedLight.Position;
                this.localLights.RequestApply(selectedLight.Id);
            }

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Light");
            var color = selectedLight.ColorRgb;
            if (ImGui.ColorEdit3("Color RGB", ref color))
                selectedLight.ColorRgb = color;
            var intensity = selectedLight.Intensity;
            if (ImGui.InputFloat("Intensity", ref intensity))
                selectedLight.Intensity = intensity;
            var range = selectedLight.Range;
            if (ImGui.InputFloat("Range", ref range))
                selectedLight.Range = range;
            var falloff = selectedLight.Falloff;
            if (ImGui.InputFloat("Falloff", ref falloff))
                selectedLight.Falloff = falloff;
            var spotAngle = selectedLight.LightAngle;
            if (ImGui.InputFloat("Spot Angle", ref spotAngle))
                selectedLight.LightAngle = spotAngle;
            var falloffAngle = selectedLight.FalloffAngle;
            if (ImGui.InputFloat("Falloff Angle", ref falloffAngle))
                selectedLight.FalloffAngle = falloffAngle;
            var area = new Vector2(selectedLight.AreaAngleX, selectedLight.AreaAngleY);
            if (ImGui.InputFloat2("Area X/Y", ref area))
            {
                selectedLight.AreaAngleX = area.X;
                selectedLight.AreaAngleY = area.Y;
            }

            if (ImGui.TreeNode("阴影/高级参数（默认关闭）"))
            {
                var specular = selectedLight.EnableSpecular;
                if (ImGui.Checkbox("Specular highlights", ref specular))
                    selectedLight.EnableSpecular = specular;
                var shadows = selectedLight.EnableDynamicShadows;
                if (ImGui.Checkbox("Dynamic shadows（高风险/性能开销）", ref shadows))
                    selectedLight.EnableDynamicShadows = shadows;
                ImGui.TreePop();
            }

            ImGui.Separator();
            if (!unsafeEnabled)
                ImGui.BeginDisabled();

            if (ImGui.Button("应用参数"))
                this.localLights.RequestApply(selectedLight.Id);
            ImGui.SameLine();
            if (ImGui.Button("删除选中"))
            {
                this.localLights.RequestDelete(selectedLight.Id);
                this.selectedLocalLightId = string.Empty;
            }

            if (!unsafeEnabled)
                ImGui.EndDisabled();

            if (ImGui.Button("人工确认：GPose 外可见"))
                this.localLights.MarkVisibleResult(selectedLight.Id, visible: true);
            ImGui.SameLine();
            if (ImGui.Button("人工确认：仍不可见"))
                this.localLights.MarkVisibleResult(selectedLight.Id, visible: false);
            ImGui.TextWrapped($"人工确认：visible={selectedLight.ManuallyConfirmedVisible}; notVisible={selectedLight.ManuallyConfirmedNotVisible}");
        }
        ImGui.EndChild();
    }

    private static void DrawLocalLightKindCombo(LocalLightInstance light)
    {
        if (!ImGui.BeginCombo("Light Kind", light.LightKind.ToString()))
            return;

        foreach (var kind in Enum.GetValues<LocalLightKind>())
        {
            var selected = light.LightKind == kind;
            if (ImGui.Selectable(kind.ToString(), selected))
                light.LightKind = kind;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawLocalLightFalloffCombo(LocalLightInstance light)
    {
        if (!ImGui.BeginCombo("Falloff Type", light.FalloffType.ToString()))
            return;

        foreach (var falloff in Enum.GetValues<LocalLightFalloffType>())
        {
            var selected = light.FalloffType == falloff;
            if (ImGui.Selectable(falloff.ToString(), selected))
                light.FalloffType = falloff;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawBgPartPool()
    {
        ImGui.TextWrapped("按 resourcePath 分组的 BgPart slot 库存池。");
        if (ImGui.Button("重新扫描 BgPart"))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);
        ImGui.InputText("搜索 resourcePath", ref this.bgPartSearchText, 256);

        var groups = this.FilteredBgParts()
            .GroupBy(item => item.ResourcePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.DistanceToPlayer))
            .Take(80)
            .ToList();

        if (!ImGui.BeginTable("BgPartPool", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 420f)))
            return;
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("total");
        ImGui.TableSetupColumn("available");
        ImGui.TableSetupColumn("occupied");
        ImGui.TableSetupColumn("visible");
        ImGui.TableSetupColumn("nearest");
        ImGui.TableSetupColumn("Protected");
        ImGui.TableSetupColumn("PreferredModify");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();
        foreach (var group in groups)
        {
            var slots = group.ToList();
            var available = slots.Where(slot => !this.localLayoutObjects.IsSlotOccupied(slot.Address)).ToList();
            ImGui.TableNextRow();
            ImGui.PushID(group.Key);
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(group.Key);
            ImGui.TableSetColumnIndex(1);
            var loadedCount = slots.Count(slot => !string.Equals(slot.SourceKind, "SharedGroup", StringComparison.Ordinal));
            var sharedCount = slots.Count(slot => string.Equals(slot.SourceKind, "SharedGroup", StringComparison.Ordinal));
            ImGui.TextWrapped($"LoadedLayout={loadedCount}; SharedGroup={sharedCount}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(slots.Count.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(available.Count.ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted((slots.Count - available.Count).ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(slots.Count(slot => slot.Visible).ToString());
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted($"{slots.Min(slot => slot.DistanceToPlayer):F1}");
            ImGui.TableSetColumnIndex(7);
            var protectedCount = this.localLayoutObjects.ProtectedBgParts == null
                ? 0
                : slots.Count(slot => this.localLayoutObjects.ProtectedBgParts.IsProtected(slot, out _));
            ImGui.TextUnformatted(protectedCount > 0 ? $"是 ({protectedCount})" : "否");
            ImGui.TableSetColumnIndex(8);
            var preferredCount = this.localLayoutObjects.PreferredModifyBgParts == null
                ? 0
                : slots.Count(slot => this.localLayoutObjects.PreferredModifyBgParts.IsPreferred(slot, out _));
            ImGui.TextUnformatted(preferredCount > 0 ? $"是 ({preferredCount})" : "否");
            ImGui.TableSetColumnIndex(9);
            var templateSlot = slots.OrderBy(slot => slot.Address).FirstOrDefault();
            ImGui.BeginDisabled(templateSlot == null);
            if (ImGui.Button("设为模板"))
            {
                this.templateBgPartAddress = templateSlot!.Address;
                this.layoutBatchCustomMdlPath = templateSlot.ResourcePath;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(templateSlot == null || !this.runtime.PlayerPosition.HasValue || !this.realNpcSpawn.EnableUnsafeNativeWrites || this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive);
            if (ImGui.Button("创建 1 个复制体"))
            {
                var created = this.localLayoutObjects.CreateCopyFromTemplate(
                    templateSlot,
                    this.AllBgParts(),
                    this.runtime.PlayerPosition!.Value,
                    LocalLayoutTransformMode.VisualOnly,
                    CarrierAllocationPolicy.PreferredListThenAnyValid,
                    this.realNpcSpawn.EnableUnsafeNativeWrites,
                    fullLayoutConfirmed: true,
                    defaultRotationEuler: this.layoutCopyDefaultRotationEuler,
                    defaultScale: this.layoutCopyUseTemplateScale && templateSlot != null ? templateSlot.Scale : this.layoutCopyDefaultScale);
                if (created != null) this.selectedLocalLayoutObjectId = created.Id;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawDebug()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "维护 / 诊断");
        ImGui.TextWrapped($"Brio IPC：{this.realNpcSpawn.BrioIpcProbeMessage}");
        ImGui.TextWrapped($"Glamourer / Penumbra：{this.realNpcSpawn.GlamourerIpcProbeMessage}");
        ImGui.TextWrapped($"Layout status：{this.layoutProbe.LastStatus}");
        ImGui.TextWrapped($"Layer status：{this.layerDump.LastStatus}");
        ImGui.TextWrapped($"Local object status：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"Create queue：active={this.localLayoutObjects.IsCreateQueueActive}; pending={this.localLayoutObjects.PendingCreateQueueLength}; success={this.localLayoutObjects.CreateQueueSuccessCount}; failed={this.localLayoutObjects.CreateQueueFailedCount}; reserved={this.localLayoutObjects.ReservedSlotCount}");
        ImGui.Separator();

        var candidate = this.GetSelectedBgPart();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "当前选中 BgPart readback");
        if (candidate == null)
        {
            ImGui.TextWrapped("当前选中 BgPart：无");
        }
        else
        {
            ImGui.TextWrapped($"resourcePath：{candidate.ResourcePath}");
            ImGui.TextWrapped($"address：{candidate.Address}");
            ImGui.TextWrapped($"source：{candidate.SourceKind}; parent={candidate.ParentAddress}; child={candidate.ChildIndex}");
            ImGui.TextWrapped($"position：{FormatVector(candidate.Position)}; visible={candidate.Visible}; distance={candidate.DistanceToPlayer:F1}y");
            ImGui.TextWrapped($"carrier reject：{this.localLayoutObjects.GetCarrierRejectReason(candidate, CarrierAllocationPolicy.PreferredListThenAnyValid)}");
            ImGui.TextWrapped($"carrier warning：{this.localLayoutObjects.GetCarrierWarningReason(candidate)}");
            if (this.localLayoutObjects.ProtectedBgParts?.IsProtected(candidate, out var protectedReason) == true)
                ImGui.TextWrapped($"protected：{protectedReason}");
            if (this.localLayoutObjects.PreferredModifyBgParts?.IsPreferred(candidate, out var preferredReason) == true)
                ImGui.TextWrapped($"preferred modify：{preferredReason}");
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "本地场景物体维护");
        ImGui.TextWrapped($"active occupied slot count：{this.localLayoutObjects.ActiveOccupiedSlotCount}");
        ImGui.TextWrapped($"duplicate slot count：{this.localLayoutObjects.DuplicateSlotCount}");
        ImGui.TextWrapped($"restore / cleanup busy：{this.localLayoutObjects.IsBusy}");
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy);
        if (ImGui.Button("RestoreAll Dry Run"))
            this.localLayoutObjects.BuildRestorePlanPreview();
        ImGui.SameLine();
        if (ImGui.Button("Rebuild occupied registry"))
            this.localLayoutObjects.RebuildOccupiedSlotRegistryForUi();
        ImGui.SameLine();
        if (ImGui.Button("Force remove failed / invalid"))
        {
            this.localLayoutObjects.ForceClearBadInstances();
            if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear restored / invalid"))
        {
            this.localLayoutObjects.ClearRestoredAndInvalidInstances();
            if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.LastRestorePlanPreview) && ImGui.CollapsingHeader("RestoreAll 计划预览"))
            ImGui.TextWrapped(this.localLayoutObjects.LastRestorePlanPreview);

        if (!string.IsNullOrWhiteSpace(this.localLayoutObjects.LastCreateManyDryRunPreview) && ImGui.CollapsingHeader("CreateMany 分配预览"))
            ImGui.TextWrapped(this.localLayoutObjects.LastCreateManyDryRunPreview);
    }

    private void DrawStandaloneBgObjectDebug()
    {
        if (!ImGui.CollapsingHeader("已隐藏的独立对象旧实验（不在 Debug 页调用）"))
            return;

        ImGui.TextWrapped("实验目标：直接调用 Graphics.Scene.BgObject.Create(modelPath, poolName, null)，不占用现有 BgPart slot，不修改 Layout/Layer 容器。");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "CreateOnly：创建后不写 transform、不 UpdateRender、不 NotifyTransformChanged。先延迟只读验证，再手动单字段写入。");

        var candidate = this.GetSelectedBgPart();
        ImGui.TextWrapped(candidate == null
            ? "当前选中 BgPart：无"
            : $"当前选中 BgPart：{candidate.ResourcePath} | {candidate.Address}");
        ImGui.BeginDisabled(candidate == null);
        if (ImGui.Button("Dump 现有 BgPart Scene.Object / scene links"))
            this.standaloneBgObjectProbe.ProbeExistingBgPart(candidate);
        ImGui.EndDisabled();
        if (ImGui.CollapsingHeader("现有 BgPart probe 结果"))
            ImGui.TextWrapped(this.standaloneBgObjectProbe.LastProbeResult);

        ImGui.Separator();
        EditString("Standalone mdl path", this.standaloneModelPath, 512, value => this.standaloneModelPath = value);
        ImGui.TextWrapped("支持 bg/...mdl 与 bgcommon/...mdl；Standalone 不使用 ResourceCategory 参数，路径由 BgObject.Create/SetModel 内部处理。");
        var poolNames = new[]
        {
            StandaloneBgObjectProbeService.PluginPoolName,
            StandaloneBgObjectProbeService.LayoutBgPartsPoolName,
        };
        var poolNamePreview = poolNames[Math.Clamp(this.standalonePoolNameIndex, 0, poolNames.Length - 1)];
        if (ImGui.BeginCombo("poolName", poolNamePreview))
        {
            for (var i = 0; i < poolNames.Length; i++)
            {
                if (ImGui.Selectable(poolNames[i], i == this.standalonePoolNameIndex))
                    this.standalonePoolNameIndex = i;
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("真实 CreatePrimary 使用 Client.LayoutEngine.Layer.BgPartsLayoutInstance；可和插件自定义 poolName 做状态对比。");

        if (this.standalonePosition == Vector3.Zero && this.runtime.PlayerPosition.HasValue)
            this.standalonePosition = this.runtime.PlayerPosition.Value;

        var position = this.standalonePosition;
        if (ImGui.InputFloat("Standalone Position X", ref position.X)) this.standalonePosition = position;
        position = this.standalonePosition;
        if (ImGui.InputFloat("Standalone Position Y", ref position.Y)) this.standalonePosition = position;
        position = this.standalonePosition;
        if (ImGui.InputFloat("Standalone Position Z", ref position.Z)) this.standalonePosition = position;
        if (ImGui.Button("Standalone 位置设为玩家当前位置") && this.runtime.PlayerPosition.HasValue)
            this.standalonePosition = this.runtime.PlayerPosition.Value;

        var rotationDegrees = this.standaloneRotationDegrees;
        if (ImGui.InputFloat("Standalone Pitch X (deg)", ref rotationDegrees.X)) this.standaloneRotationDegrees = rotationDegrees;
        rotationDegrees = this.standaloneRotationDegrees;
        if (ImGui.InputFloat("Standalone Yaw Y (deg)", ref rotationDegrees.Y)) this.standaloneRotationDegrees = rotationDegrees;
        rotationDegrees = this.standaloneRotationDegrees;
        if (ImGui.InputFloat("Standalone Roll Z (deg)", ref rotationDegrees.Z)) this.standaloneRotationDegrees = rotationDegrees;

        var scale = this.standaloneScale;
        if (ImGui.InputFloat("Standalone Scale X", ref scale.X)) this.standaloneScale = Vector3.Max(scale, new Vector3(0.01f));
        scale = this.standaloneScale;
        if (ImGui.InputFloat("Standalone Scale Y", ref scale.Y)) this.standaloneScale = Vector3.Max(scale, new Vector3(0.01f));
        scale = this.standaloneScale;
        if (ImGui.InputFloat("Standalone Scale Z", ref scale.Z)) this.standaloneScale = Vector3.Max(scale, new Vector3(0.01f));

        ImGui.Checkbox("我确认这是独立对象高风险旧实验", ref this.confirmStandaloneBgObjectExperiment);
        var canCreate = this.realNpcSpawn.EnableUnsafeNativeWrites && this.confirmStandaloneBgObjectExperiment;
        ImGui.BeginDisabled(!canCreate);
        if (ImGui.Button("CreateOnly 独立对象旧实验"))
        {
            var created = this.standaloneBgObjectProbe.Create(
                this.standaloneModelPath,
                poolNamePreview,
                this.standalonePosition,
                new Vector3(
                    DegreesToRadians(this.standaloneRotationDegrees.X),
                    DegreesToRadians(this.standaloneRotationDegrees.Y),
                    DegreesToRadians(this.standaloneRotationDegrees.Z)),
                this.standaloneScale,
                this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.confirmStandaloneBgObjectExperiment);
            if (created != null)
                this.selectedStandaloneObjectId = created.Id;
        }
        ImGui.EndDisabled();
        if (!canCreate)
            ImGui.TextDisabled("创建按钮需要 Unsafe/native 写入 + 二次确认。");

        ImGui.TextWrapped($"Standalone 状态：{this.standaloneBgObjectProbe.LastCreateResult}");
        if (!string.IsNullOrWhiteSpace(this.standaloneBgObjectProbe.LastError))
            ImGui.TextWrapped($"Standalone 错误：{this.standaloneBgObjectProbe.LastError}");

        this.DrawStandaloneObjectList();
        this.DrawSelectedStandaloneObjectControls();
    }

    private void DrawStandaloneObjectList()
    {
        var instances = this.standaloneBgObjectProbe.Instances;
        ImGui.TextWrapped($"Standalone owned object count：{instances.Count}");
        if (instances.Count == 0)
            return;

        if (!ImGui.BeginTable("StandaloneBgObjectTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("id");
        ImGui.TableSetupColumn("address");
        ImGui.TableSetupColumn("visible");
        ImGui.TableSetupColumn("state");
        ImGui.TableSetupColumn("model");
        ImGui.TableSetupColumn("last error");
        ImGui.TableHeadersRow();
        foreach (var instance in instances)
        {
            ImGui.TableNextRow();
            ImGui.PushID(instance.Id);
            ImGui.TableSetColumnIndex(0);
            if (ImGui.RadioButton("##selectStandalone", string.Equals(this.selectedStandaloneObjectId, instance.Id, StringComparison.Ordinal)))
                this.selectedStandaloneObjectId = instance.Id;
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(ShortId(instance.Id));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(instance.ObjectAddress);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(instance.IsVisible.ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(instance.State.ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(instance.ModelResourcePathReadback) ? instance.ModelPath : instance.ModelResourcePathReadback);
            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(instance.LastError) ? "无" : instance.LastError);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSelectedStandaloneObjectControls()
    {
        var selected = string.IsNullOrWhiteSpace(this.selectedStandaloneObjectId)
            ? null
            : this.standaloneBgObjectProbe.GetById(this.selectedStandaloneObjectId);
        if (selected == null)
            return;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "选中 Standalone 对象");
        ImGui.TextWrapped($"id：{selected.Id}");
        ImGui.TextWrapped($"address：{selected.ObjectAddress}");
        ImGui.TextWrapped($"poolName：{selected.PoolName}");
        ImGui.TextWrapped($"modelHandle：{selected.ModelResourceHandleAddress}; loadState={selected.LoadStateReadback}; path={selected.ModelResourcePathReadback}");
        ImGui.TextWrapped($"state：{selected.State}; valid={selected.IsValid}; vtable={selected.VTableReadback}");
        ImGui.TextWrapped($"validation：{selected.ValidationStatus}");
        ImGui.TextWrapped($"activation：step={selected.ActivationStep}; result={selected.ActivationResult}; exception={(string.IsNullOrWhiteSpace(selected.ActivationException) ? "无" : selected.ActivationException)}");
        ImGui.TextWrapped($"scene attach：step={selected.SceneAttachStep}; result={selected.SceneAttachResult}; exception={(string.IsNullOrWhiteSpace(selected.SceneAttachException) ? "无" : selected.SceneAttachException)}");
        ImGui.TextWrapped($"bounds：{selected.BoundsReadback}");
        ImGui.TextWrapped($"transform：{selected.TransformReadback}");
        ImGui.TextWrapped($"scene links：{selected.SceneLinkReadback}");
        ImGui.TextWrapped($"attachState：{selected.AttachState}; scanHit={selected.ParentChildScanHit}; scanCount={selected.ParentChildScanCount}; truncated={selected.ParentChildScanTruncated}; elapsed={selected.ParentChildScanElapsedMs:F2}ms");
        if (!string.IsNullOrWhiteSpace(selected.ParentChildScanStatus))
            ImGui.TextWrapped($"scan：{selected.ParentChildScanStatus}");

        var pos = selected.Position;
        if (ImGui.InputFloat("Selected Standalone Position X", ref pos.X)) selected.Position = pos;
        pos = selected.Position;
        if (ImGui.InputFloat("Selected Standalone Position Y", ref pos.Y)) selected.Position = pos;
        pos = selected.Position;
        if (ImGui.InputFloat("Selected Standalone Position Z", ref pos.Z)) selected.Position = pos;

        var rotDegrees = new Vector3(
            RadiansToDegrees(selected.RotationEuler.X),
            RadiansToDegrees(selected.RotationEuler.Y),
            RadiansToDegrees(selected.RotationEuler.Z));
        if (ImGui.InputFloat("Selected Standalone Pitch X (deg)", ref rotDegrees.X)) selected.RotationEuler = new Vector3(DegreesToRadians(rotDegrees.X), selected.RotationEuler.Y, selected.RotationEuler.Z);
        rotDegrees = new Vector3(RadiansToDegrees(selected.RotationEuler.X), RadiansToDegrees(selected.RotationEuler.Y), RadiansToDegrees(selected.RotationEuler.Z));
        if (ImGui.InputFloat("Selected Standalone Yaw Y (deg)", ref rotDegrees.Y)) selected.RotationEuler = new Vector3(selected.RotationEuler.X, DegreesToRadians(rotDegrees.Y), selected.RotationEuler.Z);
        rotDegrees = new Vector3(RadiansToDegrees(selected.RotationEuler.X), RadiansToDegrees(selected.RotationEuler.Y), RadiansToDegrees(selected.RotationEuler.Z));
        if (ImGui.InputFloat("Selected Standalone Roll Z (deg)", ref rotDegrees.Z)) selected.RotationEuler = new Vector3(selected.RotationEuler.X, selected.RotationEuler.Y, DegreesToRadians(rotDegrees.Z));

        var scale = selected.Scale;
        if (ImGui.InputFloat("Selected Standalone Scale X", ref scale.X)) selected.Scale = Vector3.Max(scale, new Vector3(0.01f));
        scale = selected.Scale;
        if (ImGui.InputFloat("Selected Standalone Scale Y", ref scale.Y)) selected.Scale = Vector3.Max(scale, new Vector3(0.01f));
        scale = selected.Scale;
        if (ImGui.InputFloat("Selected Standalone Scale Z", ref scale.Z)) selected.Scale = Vector3.Max(scale, new Vector3(0.01f));

        if (ImGui.Button("Dump Standalone object（只读）"))
            this.standaloneBgObjectProbe.Dump(selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("Validate Standalone object（只读）"))
            this.standaloneBgObjectProbe.Validate(selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("对比 Standalone 与当前选中真实 BgPart render 状态"))
            this.standaloneBgObjectProbe.CompareWithBgPart(selected.Id, this.GetSelectedBgPart());
        ImGui.SameLine();
        if (ImGui.Button("Standalone vs 真实 BgPart scene attach 对比"))
            this.standaloneBgObjectProbe.CompareSceneAttachWithBgPart(selected.Id, this.GetSelectedBgPart());
        ImGui.SameLine();
        if (ImGui.Button("Raw +0x00~+0xA0 / offset 对比（只读）"))
            this.standaloneBgObjectProbe.DumpRawObjectLayoutComparison(selected.Id, this.GetSelectedBgPart());
        ImGui.SameLine();
        if (ImGui.Button("Standalone active/render list 对比（只读）"))
            this.standaloneRenderListProbe.Compare(selected, this.GetSelectedBgPart());
        ImGui.SameLine();
        if (ImGui.Button("展开完整 parent child chain（限 2048）"))
            this.standaloneBgObjectProbe.FullParentChildScan(selected.Id);
        ImGui.TextWrapped($"active/render list probe：{this.standaloneRenderListProbe.LastStatus}");
        if (ImGui.CollapsingHeader("Standalone dump"))
            ImGui.TextWrapped(selected.LastDump);
        if (ImGui.CollapsingHeader("Raw object layout 缓存"))
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(selected.RawObjectLayoutDump) ? "尚未执行 raw object layout dump。" : selected.RawObjectLayoutDump);
        if (ImGui.CollapsingHeader("Standalone active/render list 只读结果"))
            ImGui.TextWrapped(this.standaloneRenderListProbe.LastDump);
        if (ImGui.CollapsingHeader("完整 parent child chain 缓存"))
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(selected.FullParentChildScanDump) ? "尚未执行完整扫描。" : selected.FullParentChildScanDump);

        this.DrawStandaloneActivationButtons(selected);
        this.DrawStandaloneSceneAttachButtons(selected);

        ImGui.BeginDisabled(!selected.CanWritePosition || !this.realNpcSpawn.EnableUnsafeNativeWrites || !this.confirmStandaloneBgObjectExperiment);
        if (ImGui.Button("Standalone 尝试写 Position（高风险，只写单字段）"))
            this.standaloneBgObjectProbe.TryWritePosition(selected.Id, selected.Position, this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmStandaloneBgObjectExperiment);
        ImGui.SameLine();
        if (ImGui.Button("Position 目标设为玩家当前位置") && this.runtime.PlayerPosition.HasValue)
            selected.Position = this.runtime.PlayerPosition.Value;
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!selected.CanWriteRotationScale || !this.realNpcSpawn.EnableUnsafeNativeWrites || !this.confirmStandaloneBgObjectExperiment);
        if (ImGui.Button("Standalone 尝试写 Rotation/Scale（需 Position 成功后）"))
            this.standaloneBgObjectProbe.TryWriteRotationScale(selected.Id, selected.RotationEuler, selected.Scale, this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmStandaloneBgObjectExperiment);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!selected.OwnedByPlugin);
        if (ImGui.Button("隐藏/移除 Standalone（安全分支）"))
            this.standaloneBgObjectProbe.HideOrRemove(selected.Id, this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmStandaloneBgObjectExperiment);
        ImGui.SameLine();
        if (ImGui.Button("全部 Standalone 标记移除/隐藏"))
            this.standaloneBgObjectProbe.HideAll(this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmStandaloneBgObjectExperiment);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("仅从列表移除此记录"))
        {
            this.standaloneBgObjectProbe.ForceRemove(selected.Id);
            this.selectedStandaloneObjectId = string.Empty;
        }

        if (ImGui.Button("人工确认：可见"))
        {
            selected.ManualVisibleConfirmed = true;
            selected.ManuallyVisible = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("人工确认：仍不可见"))
            selected.ManuallyStillInvisible = true;
        ImGui.SameLine();
        if (ImGui.Button("人工确认：模型异常"))
            selected.ManuallyModelAbnormal = true;
        ImGui.SameLine();
        if (ImGui.Button("人工确认：游戏不稳定"))
            selected.ManuallyGameUnstable = true;
        ImGui.SameLine();
        if (ImGui.Button("人工确认：原地图未受影响"))
            selected.ManualOriginalMapUnaffectedConfirmed = true;
        ImGui.SameLine();
        if (ImGui.Button("人工确认：已隐藏/不可见"))
            selected.ManualHiddenConfirmed = true;
        ImGui.TextWrapped($"人工确认：visible={selected.ManuallyVisible}; stillInvisible={selected.ManuallyStillInvisible}; abnormal={selected.ManuallyModelAbnormal}; unstable={selected.ManuallyGameUnstable}; mapUnaffected={selected.ManualOriginalMapUnaffectedConfirmed}; hidden={selected.ManualHiddenConfirmed}");
    }

    private void DrawStandaloneActivationButtons(StandaloneObjectInstance selected)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Bounds dump / render 状态只读取证");
        ImGui.TextWrapped("Render activation 写入实验已暂停；当前只保留 ComputeSphereBounds bounds dump，不调用 UpdateRender/UpdateCulling/NotifyTransformChanged。");
        var canActivate = selected.State is StandaloneObjectState.ValidatedReadOnly or StandaloneObjectState.PositionWriteSucceeded;
        ImGui.BeginDisabled(!canActivate || !this.realNpcSpawn.EnableUnsafeNativeWrites || !this.confirmStandaloneBgObjectExperiment);
        if (ImGui.Button("ComputeSphereBounds bounds dump（只读）"))
            this.standaloneBgObjectProbe.ExecuteActivationStep(selected.Id, "ComputeSphereBounds", this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmStandaloneBgObjectExperiment);
        ImGui.EndDisabled();
        if (!canActivate)
            ImGui.TextDisabled("activation 需要 state=ValidatedReadOnly 或 PositionWriteSucceeded。");
    }

    private void DrawStandaloneSceneAttachButtons(StandaloneObjectInstance selected)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.45f, 0.25f, 1f), "Scene attach 写入实验已暂停");
        ImGui.TextWrapped("独立对象 scene attach 写入实验已暂停：场景链写入后 objectFlags 异常，疑似调用签名或入口不安全。");
        ImGui.TextWrapped("已禁用：场景挂载、world callback、挂载后 update chain，以及任何 parent/child/prev/next 写入。");
        ImGui.TextWrapped("保留只读：CreateOnly、Dump、Validate、Position 单字段写入、Bounds dump、Standalone vs 真实 BgPart 对比、raw offset dump。");
        if (!string.IsNullOrWhiteSpace(selected.SceneAttachException))
            ImGui.TextWrapped($"最近一次 scene attach 拦截/错误：{selected.SceneAttachException}");
    }

    private void DrawBgPartCollisionSourceProbeDebug()
    {
        if (!ImGui.CollapsingHeader("BgPart collision source / path table 取证（只读）"))
            return;

        var candidate = this.GetSelectedBgPart();
        var selectedInstance = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);

        ImGui.TextWrapped("只读：读取 CollisionMeshPathCrc / AnalyticShapeDataCrc / GetPrimaryPath / GetSecondaryPath / LayoutManager.CrcToPath。不会调用 CreateSecondary/DestroySecondary，也不会写 CRC。");
        ImGui.TextWrapped(candidate == null
            ? "当前 BgPart 候选：无"
            : $"当前 BgPart 候选：{candidate.ResourcePath} | {candidate.Address}");

        if (string.IsNullOrWhiteSpace(this.collisionProbeTargetMdlPath))
            this.collisionProbeTargetMdlPath = selectedInstance?.CustomModelPath ?? candidate?.ResourcePath ?? string.Empty;

        EditString("target mdl path（用于推断同名 .pcb）", this.collisionProbeTargetMdlPath, 512, value => this.collisionProbeTargetMdlPath = value);
        if (selectedInstance != null)
        {
            if (ImGui.Button("使用选中实例 custom mdl"))
                this.collisionProbeTargetMdlPath = selectedInstance.CustomModelPath;
            ImGui.SameLine();
        }

        ImGui.BeginDisabled(candidate == null);
        if (ImGui.Button("Dump 当前 BgPart collision source / path table"))
            this.bgPartCollisionSourceProbe.Probe(candidate, this.FilteredBgParts(), this.collisionProbeTargetMdlPath);
        ImGui.EndDisabled();

        ImGui.TextWrapped($"状态：{this.bgPartCollisionSourceProbe.LastStatus}");
        if (ImGui.CollapsingHeader("当前 BgPart collision source"))
            ImGui.TextWrapped(this.bgPartCollisionSourceProbe.SelectedDump);
        if (ImGui.CollapsingHeader("target collision 候选"))
            ImGui.TextWrapped(this.bgPartCollisionSourceProbe.TargetCandidateDump);
        if (ImGui.CollapsingHeader("多个 BgPart 对比"))
            ImGui.TextWrapped(this.bgPartCollisionSourceProbe.ComparisonDump);
        if (ImGui.CollapsingHeader("Layout path table 预览"))
            ImGui.TextWrapped(this.bgPartCollisionSourceProbe.PathTableDump);

        var selected = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);
        if (selected != null && ImGui.CollapsingHeader("当前选中实例 readback / recreate / collision 摘要"))
        {
            ImGui.TextWrapped($"实例：{selected.Id}");
            ImGui.TextWrapped($"render invalid：{selected.IsRenderInvalid}");
            ImGui.TextWrapped($"last error：{(string.IsNullOrWhiteSpace(selected.LastError) ? "无" : selected.LastError)}");
            ImGui.TextWrapped($"recreate result：{selected.RecreateLastResult}");
            ImGui.TextWrapped($"recreate error：{(string.IsNullOrWhiteSpace(selected.RecreateLastError) ? "无" : selected.RecreateLastError)}");
            ImGui.TextWrapped($"collision resolve：{selected.CollisionSourceResolveResult}");
            ImGui.TextWrapped($"collision source：{selected.CollisionSourceResourcePath} | {selected.CollisionSourceBgPartAddress}");
            ImGui.TextWrapped($"collision after：type={selected.CollisionAfterColliderType}, mesh=0x{selected.CollisionAfterMeshPathCrc:X8}, analytic=0x{selected.CollisionAfterAnalyticShapeDataCrc:X8}, collider={selected.CollisionAfterColliderAddress}");
            ImGui.TextWrapped($"collision error：{(string.IsNullOrWhiteSpace(selected.CollisionError) ? "无" : selected.CollisionError)}");
        }
    }

    private IEnumerable<LayoutProbeInstance> FilteredBgParts()
    {
        var query = this.layoutProbe.Instances.Where(instance => string.Equals(instance.Type, "BgPart", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(this.bgPartSearchText))
        {
            query = query.Where(instance =>
                instance.ResourcePath.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.Type.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.Address.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.SourceKind.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.SharedGroupPath.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.ParentAddress.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.ParentKey.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.DebugInfo.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase));
        }
        return query.OrderBy(instance => instance.DistanceToPlayer);
    }

    private IEnumerable<LayoutProbeInstance> AllBgParts()
        => this.layoutProbe.Instances
            .Where(instance => string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
            .OrderBy(instance => instance.DistanceToPlayer);

    private LayoutProbeInstance? GetSelectedBgPart()
    {
        if (!string.IsNullOrWhiteSpace(this.selectedBgPartAddress))
        {
            var selected = this.layoutProbe.Instances.FirstOrDefault(instance => string.Equals(instance.Address, this.selectedBgPartAddress, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
                return selected;
        }
        return this.layerDump.ReusableCandidate;
    }

    private LayoutProbeInstance? GetTemplateBgPart()
        => string.IsNullOrWhiteSpace(this.templateBgPartAddress)
            ? null
            : this.layoutProbe.Instances.FirstOrDefault(instance => string.Equals(instance.Address, this.templateBgPartAddress, StringComparison.OrdinalIgnoreCase));

    private void CreateNpcAtPlayer()
    {
        var position = this.runtime.PlayerPosition ?? Vector3.Zero;
        var npc = new CustomNpc
        {
            Id = $"local-npc-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}",
            Name = "本地 NPC",
            TerritoryType = (ushort)Math.Clamp((int)this.runtime.TerritoryType, 0, ushort.MaxValue),
            LegacyDefaultTerritoryType = (ushort)Math.Clamp((int)this.runtime.TerritoryType, 0, ushort.MaxValue),
            Position = new Vector3Data { X = position.X, Y = position.Y, Z = position.Z },
            DefaultSpawnOffset = new Vector3Data(),
            DefaultScale = new Vector3Data { X = 1f, Y = 1f, Z = 1f },
            InteractRadius = 6f,
        };
        this.database.Npcs.Add(npc);
        this.database.Save();
        this.selectedNpcId = npc.Id;
    }

    private void EnsureSelectedNpc()
    {
        if (this.database.Npcs.Any(npc => string.Equals(npc.Id, this.selectedNpcId, StringComparison.OrdinalIgnoreCase)))
            return;
        this.selectedNpcId = this.database.Npcs.FirstOrDefault()?.Id ?? string.Empty;
    }

    private CustomNpc? GetSelectedNpc()
        => this.database.Npcs.FirstOrDefault(npc => string.Equals(npc.Id, this.selectedNpcId, StringComparison.OrdinalIgnoreCase));

    private string SelectedNpcLabel()
    {
        var npc = this.GetSelectedNpc();
        return npc == null ? "未选择 NPC" : $"{npc.Name} ({npc.Id})";
    }

    private static void EditString(string label, string current, int maxLength, Action<string> setter)
    {
        var value = current ?? string.Empty;
        if (ImGui.InputText(label, ref value, maxLength))
            setter(value);
    }

    private static void EditVector3Data(string label, Vector3Data data)
    {
        var x = data.X;
        var y = data.Y;
        var z = data.Z;
        if (ImGui.InputFloat($"{label} X", ref x)) data.X = x;
        if (ImGui.InputFloat($"{label} Y", ref y)) data.Y = y;
        if (ImGui.InputFloat($"{label} Z", ref z)) data.Z = z;
    }

    private static void EditVector3DataDegrees(string label, Vector3Data data)
    {
        var x = RadiansToDegrees(data.X);
        var y = RadiansToDegrees(data.Y);
        var z = RadiansToDegrees(data.Z);
        if (ImGui.InputFloat($"{label} Pitch X (deg)", ref x)) data.X = DegreesToRadians(x);
        if (ImGui.InputFloat($"{label} Yaw Y (deg)", ref y)) data.Y = DegreesToRadians(y);
        if (ImGui.InputFloat($"{label} Roll Z (deg)", ref z)) data.Z = DegreesToRadians(z);
    }

    private static void EnsureNpcDefaultScale(CustomNpc npc)
    {
        if (npc.DefaultScale.X == 0f)
            npc.DefaultScale.X = 1f;
        if (npc.DefaultScale.Y == 0f)
            npc.DefaultScale.Y = 1f;
        if (npc.DefaultScale.Z == 0f)
            npc.DefaultScale.Z = 1f;
    }

    private static void DrawEnumCombo<TEnum>(string label, TEnum value, Action<TEnum> setter)
        where TEnum : struct, Enum
    {
        if (!ImGui.BeginCombo(label, value.ToString()))
            return;

        foreach (var option in Enum.GetValues<TEnum>())
        {
            var selected = EqualityComparer<TEnum>.Default.Equals(value, option);
            if (ImGui.Selectable(option.ToString(), selected))
                setter(option);
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static Vector3 ToVector3(Vector3Data data)
        => new(data.X, data.Y, data.Z);

    private static void SetVector3Data(Vector3Data data, Vector3 vector)
    {
        data.X = vector.X;
        data.Y = vector.Y;
        data.Z = vector.Z;
    }

    private static string ShortId(string id)
        => string.IsNullOrWhiteSpace(id) ? "无" : id[..Math.Min(8, id.Length)];

    private static string FormatVector(Vector3? vector)
        => vector.HasValue ? FormatVector(vector.Value) : "不可用";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsSupportedMdlPath(string path)
        => path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase);

    private static Vector3 RadiansVectorToDegrees(Vector3 radians)
        => new(RadiansToDegrees(radians.X), RadiansToDegrees(radians.Y), RadiansToDegrees(radians.Z));

    private static Vector3 DegreesVectorToRadians(Vector3 degrees)
        => new(DegreesToRadians(degrees.X), DegreesToRadians(degrees.Y), DegreesToRadians(degrees.Z));

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;
}

