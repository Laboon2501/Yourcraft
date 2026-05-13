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
    private readonly BgPartCollisionSourceProbeService bgPartCollisionSourceProbe;
    private readonly AnimatedBgPartControllerProbeService animatedBgPartControllerProbe;
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly Action reloadAction;

    private string selectedNpcId = string.Empty;
    private string selectedActorRuntimeId = string.Empty;
    private string selectedLocalLayoutObjectId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string templateBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
    private string glamourerSearchText = string.Empty;
    private string gameNpcSearchText = string.Empty;
    private int actorBatchCount = 3;
    private Vector3 actorBatchOffset = new(1.5f, 0f, 0f);
    private bool actorBatchUsePlayerPosition;
    private bool localLayoutFullCollisionMode;
    private bool confirmFullLayoutCollisionMode;
    private bool allowDifferentResourcePathSlots;
    private int layoutCopyCount = 1;
    private float layoutCopySpacing = 2f;
    private float layoutCopySpacingY;
    private float layoutCopySpacingZ;
    private LocalLayoutTransformMode layoutCopyDefaultMode = LocalLayoutTransformMode.VisualOnly;
    private Vector3 layoutCopyDefaultRotationEuler;
    private Vector3 layoutCopyDefaultScale = Vector3.One;
    private bool layoutUseManualBasePosition;
    private Vector3 layoutManualBasePosition;
    private string layoutBatchCustomMdlPath = string.Empty;
    private string collisionProbeTargetMdlPath = string.Empty;

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
        BgPartVisualTransformProbeService bgPartVisualProbe,
        RotationMatrixExperimentService rotationMatrixExperiment,
        BgPartVisualRescueService bgPartVisualRescue,
        VisualOnlyRotationDeepProbeService visualOnlyRotationDeepProbe,
        DrawObjectUpdateDirtyProbeService drawObjectUpdateDirtyProbe,
        GraphicsSceneObjectTransformService graphicsSceneObjectTransform,
        BgPartCollisionSourceProbeService bgPartCollisionSourceProbe,
        AnimatedBgPartControllerProbeService animatedBgPartControllerProbe,
        MeddleStyleSceneProbeService meddleSceneProbe,
        GameNpcCatalogService gameNpcCatalog,
        GameNpcAppearanceResolver gameNpcAppearanceResolver,
        GlamourerDesignCatalogService glamourerDesignCatalog,
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
        this.bgPartCollisionSourceProbe = bgPartCollisionSourceProbe;
        this.animatedBgPartControllerProbe = animatedBgPartControllerProbe;
        this.gameNpcCatalog = gameNpcCatalog;
        this.glamourerDesignCatalog = glamourerDesignCatalog;
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

        if (ImGui.BeginTabItem("BgPart Slot Pool"))
        {
            this.DrawBgPartPool();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug"))
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
        ImGui.TextWrapped($"外观队列：长度 {this.realNpcSpawn.AppearanceQueueLength}，当前 {this.realNpcSpawn.AppearanceQueueCurrentActor}");
        ImGui.TextWrapped($"本地场景物体：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"模型 override：{this.localLayoutObjects.LastModelOverrideStatus}");
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
                EditString("Penumbra Collection", appearance.PenumbraCollectionName, 256, value => appearance.PenumbraCollectionName = value);
                ImGui.TextWrapped("Penumbra collection 会在后续 redraw/apply 管线里使用。");
                break;
        }

        EditString("notes/debugInfo", appearance.Notes, 512, value => appearance.Notes = value);
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

        var selectedActor = string.IsNullOrWhiteSpace(this.selectedActorRuntimeId) ? null : this.realNpcSpawn.GetActor(this.selectedActorRuntimeId);
        if (selectedActor == null)
            return;
        var npc = this.database.GetNpcById(selectedActor.NpcId);
        ImGui.Separator();
        ImGui.TextWrapped($"选中 Actor：{selectedActor.RuntimeId}");
        ImGui.TextWrapped($"模板 NPC：{selectedActor.TemplateNpcId} / {selectedActor.NpcId}");
        ImGui.TextWrapped($"显示名：{selectedActor.DisplayName}");
        ImGui.TextWrapped($"生成地图：{selectedActor.SpawnedTerritoryType} {selectedActor.SpawnedTerritoryName}");
        ImGui.TextWrapped($"当前外观来源：{selectedActor.AppearanceSourceType}");
        ImGui.TextWrapped($"最后外观：{selectedActor.LastAppearanceApplyResult}");
        ImGui.TextWrapped($"错误：{selectedActor.LastError}");
        if (ImGui.Button("删除此 Actor"))
        {
            this.DeleteSelectedActor(selectedActor);
            return;
        }

        this.DrawSelectedActorTransformEditor(selectedActor, npc);
        this.DrawSelectedActorBehaviorEditor(selectedActor, npc);
        this.DrawSelectedActorAppearanceEditor(selectedActor, npc);
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
        ImGui.TextWrapped($"最后结果：{(string.IsNullOrWhiteSpace(actor.LastAppearanceApplyResult) ? "未应用" : actor.LastAppearanceApplyResult)}");
        ImGui.TextWrapped($"最后错误：{(string.IsNullOrWhiteSpace(actor.LastAppearanceError) ? "无" : actor.LastAppearanceError)}");

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
        ImGui.EndDisabled();
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
        RuntimeActorInstance? lastActor = null;
        var spawned = 0;
        for (var index = 0; index < this.actorBatchCount; index++)
        {
            var actor = this.realNpcSpawn.SpawnNew(npc);
            if (actor == null)
                continue;

            var targetPosition = basePosition + this.actorBatchOffset * index;
            this.realNpcSpawn.ApplyActorTransform(actor.RuntimeId, targetPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
            lastActor = actor;
            spawned++;
        }

        if (lastActor != null)
            this.selectedActorRuntimeId = lastActor.RuntimeId;
        this.realNpcSpawn.SetMessage($"批量生成完成：请求 {this.actorBatchCount}，成功 {spawned}。");
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
        ImGui.TextWrapped("正式功能：复用当前地图已有 BgPart slot。VisualOnly 写 Graphics.Scene.Object transform，不移动 collision。");
        ImGui.TextWrapped($"状态：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"模型状态：{this.localLayoutObjects.LastModelOverrideStatus}");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "v9.8 静态稳定版：动态 BgPart / SharedGroup / controller 驱动物体暂不支持，命中风险对象时会拒绝创建。");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "custom mdl 使用 DestroyPrimary -> CreatePrimary；不会调用 SetModel。");
        ImGui.TextWrapped($"active occupied slot count：{this.localLayoutObjects.ActiveOccupiedSlotCount}");
        ImGui.TextWrapped($"duplicate slot count：{this.localLayoutObjects.DuplicateSlotCount}");

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
        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || candidate == null || !this.runtime.PlayerPosition.HasValue || fullLayoutBlocked);
        if (ImGui.Button(this.localLayoutFullCollisionMode ? "从候选创建本地物件（FullLayoutWithCollision，危险）" : "从候选创建本地物件（VisualOnly，推荐）"))
        {
            var created = this.localLayoutObjects.CreateFromCandidate(candidate, this.runtime.PlayerPosition!.Value, mode);
            if (created != null)
                this.selectedLocalLayoutObjectId = created.Id;
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || this.localLayoutObjects.Instances.Count == 0);
        if (ImGui.Button("恢复全部"))
        {
            this.localLayoutObjects.RestoreAll(
                bgParts: this.FilteredBgParts(),
                unsafeEnabled: this.realNpcSpawn.EnableUnsafeNativeWrites,
                fullLayoutConfirmed: this.confirmFullLayoutCollisionMode);
            this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("会自动清理动态残留 registry 和重复实例");
        ImGui.SameLine();
        ImGui.BeginDisabled(this.localLayoutObjects.Instances.Count == 0);
        if (ImGui.Button("一键清理重复实例"))
        {
            this.localLayoutObjects.CleanupDuplicateInstances(auto: false);
            if (!string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId) && this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId) == null)
                this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();

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
        if (ImGui.InputFloat("默认 Scale X", ref copyDefaultScale.X)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        copyDefaultScale = this.layoutCopyDefaultScale;
        if (ImGui.InputFloat("默认 Scale Y", ref copyDefaultScale.Y)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        copyDefaultScale = this.layoutCopyDefaultScale;
        if (ImGui.InputFloat("默认 Scale Z", ref copyDefaultScale.Z)) this.layoutCopyDefaultScale = Vector3.Max(copyDefaultScale, new Vector3(0.01f));
        EditString("批量默认 custom mdl path（可留空）", this.layoutBatchCustomMdlPath, 512, value => this.layoutBatchCustomMdlPath = value);
        ImGui.TextWrapped("留空时只占用同 resourcePath slot；填写 bg/...mdl 或 bgcommon/...mdl 时，可使用任意 bg/bgcommon 可用 slot，并在创建后逐个应用该 mdl。");
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
        var allowDifferent = this.allowDifferentResourcePathSlots;
        if (ImGui.Checkbox("允许不同 bg/bgcommon slot（未填写 custom mdl 时不建议）", ref allowDifferent))
            this.allowDifferentResourcePathSlots = allowDifferent;
        if (this.allowDifferentResourcePathSlots)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "警告：未填写 custom mdl path 时，使用不同 resourcePath slot 不是复制模板，只是移动不同物体。");
        var mode = this.layoutCopyDefaultMode;
        var fullLayoutBlocked = mode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.confirmFullLayoutCollisionMode;
        var hasBasePosition = this.layoutUseManualBasePosition || this.runtime.PlayerPosition.HasValue;
        var basePosition = this.layoutUseManualBasePosition
            ? this.layoutManualBasePosition
            : this.runtime.PlayerPosition ?? Vector3.Zero;
        var availableNearest = this.FilteredBgParts().Where(slot => !this.localLayoutObjects.IsSlotOccupied(slot.Address)).OrderBy(slot => slot.DistanceToPlayer).ToList();
        var availableFarthest = availableNearest.OrderByDescending(slot => slot.DistanceToPlayer).ToList();
        var availableInvisible = availableNearest.Where(slot => !slot.Visible).Concat(availableNearest.Where(slot => slot.Visible)).ToList();
        var hasBatchMdl = !string.IsNullOrWhiteSpace(this.layoutBatchCustomMdlPath);
        var sameResourceAvailable = template == null
            ? 0
            : availableNearest.Count(slot => !string.Equals(slot.Address, template.Address, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase));
        var anySupportedAvailable = template == null
            ? 0
            : availableNearest.Count(slot => !string.Equals(slot.Address, template.Address, StringComparison.OrdinalIgnoreCase)
                && IsSupportedMdlPath(slot.ResourcePath));
        var plannedAvailable = hasBatchMdl || this.allowDifferentResourcePathSlots ? anySupportedAvailable : sameResourceAvailable;
        ImGui.TextWrapped(template == null
            ? "同 resourcePath 可用 slot：请先设置模板。"
            : $"模板 resourcePath：{template.ResourcePath}");
        ImGui.TextWrapped($"同 resourcePath 可用 slot 数：{sameResourceAvailable}；bg/bgcommon 可用 slot 数：{anySupportedAvailable}；当前将创建：{Math.Min(this.layoutCopyCount, plannedAvailable)}");

        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || template == null || !hasBasePosition || fullLayoutBlocked);
        if (ImGui.Button(mode == LocalLayoutTransformMode.VisualOnly ? "创建 N 个 VisualOnly 复制体" : "创建 N 个 FullLayoutWithCollision 复制体"))
            this.CreateMany(template, availableNearest, this.layoutCopyCount, basePosition, mode);
        if (ImGui.Button("从最远可用 slot 分配"))
            this.CreateMany(template, availableFarthest, this.layoutCopyCount, basePosition, mode);
        ImGui.SameLine();
        if (ImGui.Button("从不可见 slot 分配"))
            this.CreateMany(template, availableInvisible, this.layoutCopyCount, basePosition, mode);
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
            this.FilteredBgParts(),
            this.realNpcSpawn.EnableUnsafeNativeWrites,
            this.confirmFullLayoutCollisionMode,
            this.layoutCopyDefaultRotationEuler,
            this.layoutCopyDefaultScale);
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
            var carrierReject = this.localLayoutObjects.GetCarrierRejectReason(candidate);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(carrierReject)
                ? "carrier 状态：可作为静态 carrier"
                : $"carrierRejectReason：{carrierReject}");
        }
        ImGui.InputText("搜索 resourcePath/type", ref this.bgPartSearchText, 256);

        var rows = this.FilteredBgParts().Take(80).ToList();
        if (!ImGui.BeginTable("BgPartSelectionTable", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
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
            ImGui.TextWrapped(this.localLayoutObjects.GetCarrierRejectReason(item));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawLocalLayoutObjectTable()
    {
        if (!ImGui.BeginTable("LocalLayoutObjects", 16, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 320f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("instanceId");
        ImGui.TableSetupColumn("slotAddress");
        ImGui.TableSetupColumn("templateResource");
        ImGui.TableSetupColumn("originalSlotResourcePath");
        ImGui.TableSetupColumn("currentModelPath");
        ImGui.TableSetupColumn("custom mdl path");
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
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(instance.ApplyMdlStatus) ? "未应用" : instance.ApplyMdlStatus);
            ImGui.TableSetColumnIndex(8);
            ImGui.TextWrapped(instance.RestoreStatus);
            ImGui.TableSetColumnIndex(9);
            ImGui.TextWrapped(FirstNonEmpty(instance.ApplyMdlError, instance.LastError, instance.LastModelOverrideError));
            ImGui.TableSetColumnIndex(10);
            ImGui.BeginDisabled(instance.IsRestored || instance.IsInvalid || instance.IsDuplicate || !this.realNpcSpawn.EnableUnsafeNativeWrites);
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
            ImGui.TableSetColumnIndex(11);
            ImGui.TextUnformatted(instance.IsRestored ? "是" : "否");
            ImGui.TableSetColumnIndex(12);
            ImGui.TextWrapped(FormatVector(instance.CurrentPosition));
            ImGui.TableSetColumnIndex(13);
            ImGui.TextWrapped(FormatVector(instance.CurrentScale));
            ImGui.TableSetColumnIndex(14);
            var fullLayoutNeedsConfirmation = instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.confirmFullLayoutCollisionMode;
            ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid || instance.IsDuplicate || instance.IsRenderInvalid || fullLayoutNeedsConfirmation);
            if (ImGui.Button("应用 mdl"))
                this.localLayoutObjects.ApplyMdlPath(instance.Id, instance.CustomModelPath, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid);
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
        ImGui.TextWrapped($"pending recreate：{selected.PendingRecreate}");
        ImGui.TextWrapped($"pending visual transform：{selected.PendingVisualTransform}，等待帧：{selected.PendingVisualTransformFrameWait}");
        ImGui.TextWrapped($"pending result：{selected.PendingVisualTransformResult}");
        ImGui.TextWrapped($"restore status：{(string.IsNullOrWhiteSpace(selected.RestoreStatus) ? "Pending" : selected.RestoreStatus)}");
        ImGui.TextWrapped($"restore step：{(string.IsNullOrWhiteSpace(selected.RestoreStep) ? "无" : selected.RestoreStep)}");
        ImGui.TextWrapped($"restore error：{(string.IsNullOrWhiteSpace(selected.RestoreError) ? "无" : selected.RestoreError)}");
        ImGui.TextWrapped($"after restore path：{(string.IsNullOrWhiteSpace(selected.AfterRestorePath) ? "未记录" : selected.AfterRestorePath)}");
        ImGui.TextWrapped($"after restore position：{(string.IsNullOrWhiteSpace(selected.AfterRestorePosition) ? "未记录" : selected.AfterRestorePosition)}");
        ImGui.TextWrapped($"after restore visible：{(string.IsNullOrWhiteSpace(selected.AfterRestoreVisible) ? "未记录" : selected.AfterRestoreVisible)}");
        ImGui.TextWrapped($"complex risk：{(string.IsNullOrWhiteSpace(selected.ComplexModelRisk) ? "StaticOk" : selected.ComplexModelRisk)}");
        if (!string.IsNullOrWhiteSpace(selected.ComplexModelRiskReason))
            ImGui.TextWrapped($"risk reason：{selected.ComplexModelRiskReason}");
        ImGui.TextWrapped($"is restoring：{selected.IsRestoring}");
        ImGui.TextWrapped($"original visible：{selected.OriginalVisible}；current visible：{selected.Visible}");
        if (!string.IsNullOrWhiteSpace(selected.CarrierRejectReason))
            ImGui.TextWrapped($"carrier reject reason：{selected.CarrierRejectReason}");
        ImGui.TextWrapped($"readback：{selected.LastReadback}");
        ImGui.TextWrapped($"错误：{(string.IsNullOrWhiteSpace(selected.LastError) ? "无" : selected.LastError)}");

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

        var disabled = !this.realNpcSpawn.EnableUnsafeNativeWrites || selected.IsDuplicate || selected.IsRestored || selected.IsRenderInvalid || fullLayoutNeedsConfirmation;
        if (selected.IsRenderInvalid)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "当前实例 render 已失效，不能再写 Graphics.Scene.Object transform。");
        ImGui.BeginDisabled(disabled);
        if (ImGui.Button("应用 transform")) this.localLayoutObjects.ApplyVisualTransform(selected.Id, selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale);
        ImGui.SameLine();
        if (ImGui.Button("移动到玩家当前位置") && this.runtime.PlayerPosition.HasValue) this.localLayoutObjects.MoveToPlayer(selected.Id, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("把模型放到玩家脚下") && this.runtime.PlayerPosition.HasValue) this.localLayoutObjects.MoveToPlayer(selected.Id, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("恢复原始 transform")) this.localLayoutObjects.RestoreOriginal(selected.Id);
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

        if (!ImGui.BeginTable("BgPartPool", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 420f)))
            return;
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("total");
        ImGui.TableSetupColumn("available");
        ImGui.TableSetupColumn("occupied");
        ImGui.TableSetupColumn("visible");
        ImGui.TableSetupColumn("nearest");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();
        foreach (var group in groups)
        {
            var slots = group.ToList();
            var available = slots.Where(slot => !this.localLayoutObjects.IsSlotOccupied(slot.Address)).OrderBy(slot => slot.DistanceToPlayer).ToList();
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
            ImGui.BeginDisabled(available.Count == 0 || !this.runtime.PlayerPosition.HasValue || !this.realNpcSpawn.EnableUnsafeNativeWrites);
            if (ImGui.Button("占用最近 slot"))
            {
                var created = this.localLayoutObjects.CreateFromCandidate(available.FirstOrDefault(), this.runtime.PlayerPosition!.Value, LocalLayoutTransformMode.VisualOnly);
                if (created != null) this.selectedLocalLayoutObjectId = created.Id;
            }
            ImGui.SameLine();
            if (ImGui.Button("占用最远 slot"))
            {
                var created = this.localLayoutObjects.CreateFromCandidate(available.OrderByDescending(slot => slot.DistanceToPlayer).FirstOrDefault(), this.runtime.PlayerPosition!.Value, LocalLayoutTransformMode.VisualOnly);
                if (created != null) this.selectedLocalLayoutObjectId = created.Id;
            }
            ImGui.SameLine();
            if (ImGui.Button("占用不可见 slot"))
            {
                var created = this.localLayoutObjects.CreateFromCandidate(available.FirstOrDefault(slot => !slot.Visible) ?? available.FirstOrDefault(), this.runtime.PlayerPosition!.Value, LocalLayoutTransformMode.VisualOnly);
                if (created != null) this.selectedLocalLayoutObjectId = created.Id;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawDebug()
    {
        ImGui.TextWrapped($"Layout status：{this.layoutProbe.LastStatus}");
        ImGui.TextWrapped($"Layer status：{this.layerDump.LastStatus}");
        ImGui.TextWrapped($"Local object status：{this.localLayoutObjects.LastStatus}");
        this.DrawBgPartCollisionSourceProbeDebug();
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

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;
}

