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
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly Action reloadAction;

    private string selectedNpcId = string.Empty;
    private string selectedActorRuntimeId = string.Empty;
    private string selectedLocalLayoutObjectId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string templateBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
    private bool localLayoutFullCollisionMode;
    private bool confirmFullLayoutCollisionMode;
    private bool allowDifferentResourcePathSlots;
    private bool enableBgPartRecreateExperiment;
    private bool confirmBgPartRecreateExperiment;
    private bool confirmBgPartCollisionSourceExperiment;
    private int layoutCopyCount = 1;
    private float layoutCopySpacing = 2f;
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
        this.EnsureSelectedNpc();
        if (ImGui.BeginCombo("选择 NPC", this.SelectedNpcLabel()))
        {
            foreach (var npc in this.database.Npcs)
            {
                var selected = string.Equals(this.selectedNpcId, npc.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{npc.Name} ({npc.Id})", selected))
                    this.selectedNpcId = npc.Id;
            }
            ImGui.EndCombo();
        }

        var currentNpc = this.GetSelectedNpc();
        if (currentNpc == null)
            return;

        ImGui.TextWrapped($"NPC ID：{currentNpc.Id}");
        EditString("名称", currentNpc.Name, 128, value => currentNpc.Name = value);
        var territory = (int)currentNpc.TerritoryType;
        if (ImGui.InputInt("地图编号 territoryType", ref territory))
            currentNpc.TerritoryType = (ushort)Math.Clamp(territory, 0, ushort.MaxValue);
        EditVector3Data("坐标", currentNpc.Position);
        var radius = currentNpc.InteractRadius;
        if (ImGui.InputFloat("交互半径", ref radius))
            currentNpc.InteractRadius = Math.Max(0.1f, radius);

        var defaultAnimation = (int)Math.Min(currentNpc.DefaultAnimationId, int.MaxValue);
        if (ImGui.InputInt("默认动画 ID", ref defaultAnimation))
            currentNpc.DefaultAnimationId = (uint)Math.Max(0, defaultAnimation);
        var autoPlay = currentNpc.AutoPlayDefaultAnimation;
        if (ImGui.Checkbox("生成后自动播放默认动画", ref autoPlay))
            currentNpc.AutoPlayDefaultAnimation = autoPlay;
        var respawn = currentNpc.RespawnAfterGpose;
        if (ImGui.Checkbox("退出 GPose 后自动重建", ref respawn))
            currentNpc.RespawnAfterGpose = respawn;

        DrawEnumCombo("外观 sourceType", currentNpc.Appearance.SourceType, value => currentNpc.Appearance.SourceType = value);
        EditString("displayName", currentNpc.Appearance.DisplayName, 160, value => currentNpc.Appearance.DisplayName = value);
        EditString("Glamourer design GUID", currentNpc.Appearance.GlamourerDesignId, 160, value => currentNpc.Appearance.GlamourerDesignId = value);
        EditString("GameNpc 名称", currentNpc.Appearance.GameNpcName, 160, value => currentNpc.Appearance.GameNpcName = value);
        DrawEnumCombo("GameNpc kind", currentNpc.Appearance.GameNpcKind, value => currentNpc.Appearance.GameNpcKind = value);
        var gameNpcBaseId = (int)Math.Min(currentNpc.Appearance.GameNpcBaseId, int.MaxValue);
        if (ImGui.InputInt("GameNpc baseId", ref gameNpcBaseId))
            currentNpc.Appearance.GameNpcBaseId = (uint)Math.Max(0, gameNpcBaseId);
        var gameNpcModelId = (int)Math.Min(currentNpc.Appearance.GameNpcModelId, int.MaxValue);
        if (ImGui.InputInt("GameNpc modelId", ref gameNpcModelId))
            currentNpc.Appearance.GameNpcModelId = (uint)Math.Max(0, gameNpcModelId);

        if (ImGui.Button("移动配置坐标到玩家当前位置") && this.runtime.PlayerPosition.HasValue)
        {
            SetVector3Data(currentNpc.Position, this.runtime.PlayerPosition.Value);
            currentNpc.TerritoryType = (ushort)Math.Clamp((int)this.runtime.TerritoryType, 0, ushort.MaxValue);
            this.database.Save();
        }
        ImGui.SameLine();
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

        ImGui.BeginDisabled(!this.realNpcSpawn.CanSpawnRealActor);
        if (ImGui.Button("生成唯一真实 Actor 并应用此 NPC 外观"))
        {
            var actor = this.realNpcSpawn.SpawnUnique(currentNpc);
            if (actor != null)
                this.selectedActorRuntimeId = actor.RuntimeId;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("对已生成 Actor 应用此 NPC 外观"))
            this.realNpcSpawn.ApplyNpcAppearanceForNpc(currentNpc.Id);
    }

    private void DrawActorInstances()
    {
        this.EnsureSelectedNpc();
        var selectedNpc = this.GetSelectedNpc();
        if (ImGui.Button("刷新有效性"))
            this.realNpcSpawn.RefreshActors();
        ImGui.SameLine();
        ImGui.BeginDisabled(selectedNpc == null || !this.realNpcSpawn.CanSpawnRealActor);
        if (ImGui.Button("生成选中 NPC 的唯一 Actor") && selectedNpc != null)
        {
            var actor = this.realNpcSpawn.SpawnUnique(selectedNpc);
            if (actor != null)
                this.selectedActorRuntimeId = actor.RuntimeId;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("删除全部 Actor"))
            this.realNpcSpawn.DespawnAll();

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
        ImGui.TextWrapped($"最后外观：{selectedActor.LastAppearanceApplyResult}");
        ImGui.TextWrapped($"错误：{selectedActor.LastError}");
        ImGui.BeginDisabled(!selectedActor.IsValid);
        if (ImGui.Button("应用 NPC 外观"))
            this.realNpcSpawn.EnqueueNpcAppearance(selectedActor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("移到玩家当前位置") && this.runtime.PlayerPosition.HasValue)
            this.realNpcSpawn.MoveActor(selectedActor.RuntimeId, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("移到 NPC 配置坐标") && npc != null)
            this.realNpcSpawn.MoveActor(selectedActor.RuntimeId, ToVector3(npc.Position));
        ImGui.SameLine();
        if (ImGui.Button("播放默认动画") && npc != null)
            this.realNpcSpawn.PlayAnimation(selectedActor.RuntimeId, npc.DefaultAnimationId);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("删除此 Actor"))
        {
            this.realNpcSpawn.Despawn(selectedActor.RuntimeId, DespawnReason.UserRequested);
            this.selectedActorRuntimeId = string.Empty;
        }
    }

    private void DrawLocalLayoutObjects()
    {
        ImGui.TextWrapped("正式功能：复用当前地图已有 BgPart slot。VisualOnly 写 Graphics.Scene.Object transform，不移动 collision。");
        ImGui.TextWrapped($"状态：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"模型状态：{this.localLayoutObjects.LastModelOverrideStatus}");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "custom mdl / SetModel 当前为高风险实验，已禁用自动调用。创建、删除、恢复全部不会调用 SetModel。");
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
            this.localLayoutObjects.RestoreAll();
            this.selectedLocalLayoutObjectId = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("会自动清理重复实例");
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
        ImGui.TextWrapped(template == null ? "当前模板：无" : $"当前模板：{template.ResourcePath} | {template.Address} | {FormatVector(template.Position)}");
        ImGui.BeginDisabled(candidate == null);
        if (ImGui.Button("设为复制模板") && candidate != null)
            this.templateBgPartAddress = candidate.Address;
        ImGui.EndDisabled();
        ImGui.InputInt("创建数量 N", ref this.layoutCopyCount);
        this.layoutCopyCount = Math.Clamp(this.layoutCopyCount, 1, 100);
        ImGui.InputFloat("横向间距", ref this.layoutCopySpacing);
        var allowDifferent = this.allowDifferentResourcePathSlots;
        if (ImGui.Checkbox("允许不同 resourcePath slot（不是复制模板，默认关闭）", ref allowDifferent))
            this.allowDifferentResourcePathSlots = allowDifferent;
        if (this.allowDifferentResourcePathSlots)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "警告：这不是复制模板，只是移动不同物体。SetModel per-instance 未跑通前不建议使用。");
        var mode = this.localLayoutFullCollisionMode ? LocalLayoutTransformMode.FullLayoutWithCollision : LocalLayoutTransformMode.VisualOnly;
        var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;
        var basePosition = this.runtime.PlayerPosition ?? template?.Position ?? Vector3.Zero;
        var availableNearest = this.FilteredBgParts().Where(slot => !this.localLayoutObjects.IsSlotOccupied(slot.Address)).OrderBy(slot => slot.DistanceToPlayer).ToList();
        var availableFarthest = availableNearest.OrderByDescending(slot => slot.DistanceToPlayer).ToList();
        var availableInvisible = availableNearest.Where(slot => !slot.Visible).Concat(availableNearest.Where(slot => slot.Visible)).ToList();
        var sameResourceAvailable = template == null
            ? 0
            : availableNearest.Count(slot => string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase));
        ImGui.TextWrapped(template == null
            ? "同 resourcePath 可用 slot：请先设置模板。"
            : $"模板 resourcePath：{template.ResourcePath}");
        ImGui.TextWrapped($"同 resourcePath 可用 slot 数：{sameResourceAvailable}；当前将创建：{Math.Min(this.layoutCopyCount, sameResourceAvailable)}");

        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites || template == null || !this.runtime.PlayerPosition.HasValue || fullLayoutBlocked);
        if (ImGui.Button("创建 N 个 VisualOnly 实例"))
            this.CreateMany(template, availableNearest, this.layoutCopyCount, basePosition, LocalLayoutTransformMode.VisualOnly);
        ImGui.SameLine();
        if (ImGui.Button("创建 N 个 FullLayoutWithCollision 实例"))
            this.CreateMany(template, availableNearest, this.layoutCopyCount, basePosition, LocalLayoutTransformMode.FullLayoutWithCollision);
        if (ImGui.Button("从最远可用 slot 分配"))
            this.CreateMany(template, availableFarthest, this.layoutCopyCount, basePosition, mode);
        ImGui.SameLine();
        if (ImGui.Button("从不可见 slot 分配"))
            this.CreateMany(template, availableInvisible, this.layoutCopyCount, basePosition, mode);
        ImGui.EndDisabled();
    }

    private void CreateMany(LayoutProbeInstance? template, IReadOnlyList<LayoutProbeInstance> slots, int count, Vector3 basePosition, LocalLayoutTransformMode mode)
    {
        var created = this.localLayoutObjects.CreateManyFromTemplate(template, slots, count, basePosition, mode, new Vector3(this.layoutCopySpacing, 0f, 0f), this.allowDifferentResourcePathSlots);
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
            : $"当前选中 BgPart：{candidate.ResourcePath} | {candidate.Address} | 距离 {candidate.DistanceToPlayer:F1}y | {FormatVector(candidate.Position)}");
        ImGui.InputText("搜索 resourcePath/type", ref this.bgPartSearchText, 256);

        var rows = this.FilteredBgParts().Take(80).ToList();
        if (!ImGui.BeginTable("BgPartSelectionTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("distance");
        ImGui.TableSetupColumn("type");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("visible");
        ImGui.TableSetupColumn("address");
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
            ImGui.TextUnformatted(item.Type);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(item.Visible ? "是" : "否");
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(item.Address);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawLocalLayoutObjectTable()
    {
        if (!ImGui.BeginTable("LocalLayoutObjects", 10, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 260f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("instanceId");
        ImGui.TableSetupColumn("slotAddress");
        ImGui.TableSetupColumn("template");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("customModel");
        ImGui.TableSetupColumn("mode");
        ImGui.TableSetupColumn("model");
        ImGui.TableSetupColumn("restored");
        ImGui.TableSetupColumn("position");
        ImGui.TableHeadersRow();
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
            ImGui.TextWrapped(instance.TemplateSourceSlotAddress);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(instance.CurrentResourcePath);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(instance.CustomModelPath);
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(instance.TransformMode.ToString());
            ImGui.TableSetColumnIndex(7);
            ImGui.TextUnformatted(instance.ModelOverrideApplied ? "override" : "original");
            ImGui.TableSetColumnIndex(8);
            ImGui.TextUnformatted(instance.IsRestored ? "是" : "否");
            ImGui.TableSetColumnIndex(9);
            ImGui.TextWrapped(FormatVector(instance.CurrentPosition));
            ImGui.PopID();
        }
        ImGui.EndTable();
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
        ImGui.TextWrapped($"occupied slot：{selected.OccupiedSlotAddress}");
        ImGui.TextWrapped($"original model：{selected.OriginalModelResourcePath}");
        ImGui.TextWrapped($"current model：{selected.CurrentResourcePath}");
        ImGui.TextWrapped($"readback：{selected.LastReadback}");
        ImGui.TextWrapped($"错误：{(string.IsNullOrWhiteSpace(selected.LastError) ? "无" : selected.LastError)}");

        var selectedWriteMode = this.localLayoutFullCollisionMode ? LocalLayoutTransformMode.FullLayoutWithCollision : LocalLayoutTransformMode.VisualOnly;
        selected.TransformMode = selectedWriteMode;
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
        ImGui.TextWrapped($"model result：{selected.LastModelOverrideResult}");
        ImGui.TextWrapped($"model error：{(string.IsNullOrWhiteSpace(selected.LastModelOverrideError) ? "无" : selected.LastModelOverrideError)}");
        ImGui.TextWrapped($"before model path：{selected.BeforeModelPath}");
        ImGui.TextWrapped($"target model path：{selected.TargetModelPath}");
        ImGui.TextWrapped($"after model path：{selected.AfterModelPath}");
        ImGui.TextWrapped($"modelResourceHandle：{selected.ModelResourceHandleAddress}");
        ImGui.TextWrapped($"modelResourceHandle vtable/type：{selected.ModelResourceHandleVTable} / {selected.ModelResourceHandleType}");
        ImGui.TextWrapped($"fileType / loadState / id：{selected.ModelResourceHandleFileType} / {selected.ModelResourceHandleLoadState} / {selected.ModelResourceHandleId}");
        ImGui.TextWrapped($"ResourceCategory 读回：{selected.ModelResourceCategoryReadback}");
        ImGui.TextWrapped($"实际 category：{selected.ModelResourceCategoryGuess}");
        ImGui.TextWrapped($"是否可信：{selected.ModelResourceCategoryConfidence}");
        ImGui.TextWrapped($"SetModel 签名：{selected.SetModelSignatureReadback}");
        ImGui.TextWrapped($"ResourceHandle dump：{selected.ModelResourceHandleDump}");
        ImGui.TextWrapped($"SetModel 返回值：{selected.SetModelReturnValue}");
        ImGui.TextWrapped($"visible：{selected.ModelVisibilityReadback}");
        ImGui.TextWrapped($"transform readback：{selected.ModelTransformReadback}");
        ImGui.TextWrapped($"last exception：{(string.IsNullOrWhiteSpace(selected.LastSetModelException) ? "无" : selected.LastSetModelException)}");
        ImGui.TextWrapped($"人工确认：{(string.IsNullOrWhiteSpace(selected.ManualVisualConfirmation) ? "未记录" : selected.ManualVisualConfirmation)}");
        const string setModelPausedMessage = "SetModel 直接调用已暂停：ResourceCategory / 调用签名未确认，会崩溃。";
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), setModelPausedMessage);
        ImGui.TextWrapped("当前只保留 mdl path 输入框、modelResourceHandle 只读 Dump 和结果记录；创建、删除、恢复全部、批量复制都不会调用 SetModel。");

        var disabled = !this.realNpcSpawn.EnableUnsafeNativeWrites || selected.IsDuplicate || selected.IsRestored || selected.IsRenderInvalid || fullLayoutNeedsConfirmation;
        if (selected.IsRenderInvalid)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "当前实例 render 已失效，不能再写 Graphics.Scene.Object transform。");
        ImGui.BeginDisabled(disabled);
        if (ImGui.Button("应用 transform")) this.localLayoutObjects.ApplyVisualTransform(selected.Id, selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale);
        ImGui.SameLine();
        if (ImGui.Button("移动到玩家当前位置") && this.runtime.PlayerPosition.HasValue) this.localLayoutObjects.MoveToPlayer(selected.Id, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("恢复原始 transform")) this.localLayoutObjects.RestoreOriginal(selected.Id);
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
        ImGui.SameLine();
        ImGui.BeginDisabled(true);
        ImGui.Button("单实例 SetModel 实验（已暂停）");
        ImGui.SameLine();
        ImGui.Button("单实例恢复模型实验（已暂停）");
        ImGui.EndDisabled();
        ImGui.TextWrapped(setModelPausedMessage);
        if (ImGui.Button("人工确认：外观已变化")) selected.ManualVisualConfirmation = "外观已变化";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：只影响当前实例")) selected.ManualVisualConfirmation = "只影响当前实例";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：未变化/异常")) selected.ManualVisualConfirmation = "未变化/异常";
        if (ImGui.Button("Dump current model resource")) this.localLayoutObjects.SaveCurrentTransform(selected.Id);
        ImGui.SameLine();
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

        if (!ImGui.BeginTable("BgPartPool", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 420f)))
            return;
        ImGui.TableSetupColumn("resourcePath");
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
            ImGui.TextUnformatted(slots.Count.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(available.Count.ToString());
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted((slots.Count - available.Count).ToString());
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(slots.Count(slot => slot.Visible).ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted($"{slots.Min(slot => slot.DistanceToPlayer):F1}");
            ImGui.TableSetColumnIndex(6);
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
        this.DrawSingleSetModelRetryDebug();
        this.DrawBgPartRecreateExperimentDebug();
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

        this.DrawBgPartCollisionSourceExperimentDebug(candidate);
    }

    private void DrawBgPartCollisionSourceExperimentDebug(LayoutProbeInstance? candidate)
    {
        var selected = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "FullLayout target collision source 单实例实验（高风险）");
        ImGui.TextWrapped("用途：把目标 BgPart 的 collision source 字段复制到当前本地实例，然后执行 DestroySecondary -> CreateSecondary。VisualOnly 禁止使用。");
        ImGui.TextWrapped($"服务状态：{this.localLayoutObjects.LastCollisionExperimentStatus}");

        if (selected == null)
        {
            ImGui.TextWrapped("请先在“本地场景物体”页选中一个 LocalLayoutObjectInstance。");
            return;
        }

        ImGui.TextWrapped($"当前实例：{selected.Id}");
        ImGui.TextWrapped($"实例 mode：{selected.TransformMode}");
        ImGui.TextWrapped($"source resourcePath：{selected.CollisionSourceResourcePath}");
        ImGui.TextWrapped($"source address：{selected.CollisionSourceBgPartAddress}");
        ImGui.TextWrapped($"source ColliderType：{selected.CollisionSourceColliderType}");
        ImGui.TextWrapped($"source CollisionMeshPathCrc：0x{selected.CollisionSourceMeshPathCrc:X8}");
        ImGui.TextWrapped($"source AnalyticShapeDataCrc：0x{selected.CollisionSourceAnalyticShapeDataCrc:X8}");
        ImGui.TextWrapped($"source material low/high：id 0x{selected.CollisionSourceMaterialIdLow:X8}/0x{selected.CollisionSourceMaterialIdHigh:X8}, mask 0x{selected.CollisionSourceMaterialMaskLow:X8}/0x{selected.CollisionSourceMaterialMaskHigh:X8}");
        ImGui.TextWrapped($"source secondary path：{selected.CollisionSourceSecondaryPath}");
        ImGui.TextWrapped($"snapshot ColliderType：{selected.CollisionSnapshotColliderType}");
        ImGui.TextWrapped($"snapshot mesh/analytic：0x{selected.CollisionSnapshotMeshPathCrc:X8} / 0x{selected.CollisionSnapshotAnalyticShapeDataCrc:X8}");
        ImGui.TextWrapped($"snapshot collider：{selected.CollisionSnapshotColliderAddress}");
        ImGui.TextWrapped($"after ColliderType：{selected.CollisionAfterColliderType}");
        ImGui.TextWrapped($"after mesh/analytic：0x{selected.CollisionAfterMeshPathCrc:X8} / 0x{selected.CollisionAfterAnalyticShapeDataCrc:X8}");
        ImGui.TextWrapped($"after collider：{selected.CollisionAfterColliderAddress}");
        ImGui.TextWrapped($"after secondary path：{selected.CollisionAfterSecondaryPath}");
        ImGui.TextWrapped($"result：{selected.CollisionExperimentLastResult}");
        ImGui.TextWrapped($"error：{(string.IsNullOrWhiteSpace(selected.CollisionExperimentLastError) ? "无" : selected.CollisionExperimentLastError)}");

        ImGui.BeginDisabled(candidate == null);
        if (ImGui.Button("选择当前 BgPart 为 collision source") && candidate != null)
            this.localLayoutObjects.CaptureCollisionSource(selected.Id, candidate);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("保存当前实例 collision 快照"))
            this.localLayoutObjects.SaveCollisionSnapshot(selected.Id);

        var confirm = this.confirmBgPartCollisionSourceExperiment;
        if (ImGui.Checkbox("我确认只对当前单实例执行 DestroySecondary -> CreateSecondary collision 实验", ref confirm))
            this.confirmBgPartCollisionSourceExperiment = confirm;

        var applyBlockReason = this.localLayoutObjects.GetApplyCollisionSourceBlockReason(
            selected.Id,
            this.realNpcSpawn.EnableUnsafeNativeWrites,
            this.confirmFullLayoutCollisionMode,
            this.confirmBgPartCollisionSourceExperiment);
        if (!string.IsNullOrWhiteSpace(applyBlockReason))
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), $"应用按钮禁用原因：{applyBlockReason}");

        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(applyBlockReason));
        if (ImGui.Button("应用 source collision 到当前实例（高风险）"))
            this.localLayoutObjects.ApplyCollisionSource(
                selected.Id,
                this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.confirmFullLayoutCollisionMode,
                this.confirmBgPartCollisionSourceExperiment);
        ImGui.EndDisabled();

        var restoreBlockReason = this.localLayoutObjects.GetRestoreCollisionSourceBlockReason(
            selected.Id,
            this.realNpcSpawn.EnableUnsafeNativeWrites,
            this.confirmFullLayoutCollisionMode,
            this.confirmBgPartCollisionSourceExperiment);
        ImGui.SameLine();
        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(restoreBlockReason));
        if (ImGui.Button("恢复原 collision source（高风险）"))
            this.localLayoutObjects.RestoreCollisionSource(
                selected.Id,
                this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.confirmFullLayoutCollisionMode,
                this.confirmBgPartCollisionSourceExperiment);
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(restoreBlockReason))
            ImGui.TextDisabled($"恢复按钮状态：{restoreBlockReason}");

        if (ImGui.Button("人工确认：碰撞变成 target 碰撞")) selected.CollisionExperimentManualConfirmation = "碰撞变成 target 碰撞";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：只影响当前实例")) selected.CollisionExperimentManualConfirmation = "只影响当前实例";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：不稳定/异常")) selected.CollisionExperimentManualConfirmation = "不稳定/异常";
        ImGui.TextWrapped($"人工记录：{(string.IsNullOrWhiteSpace(selected.CollisionExperimentManualConfirmation) ? "未记录" : selected.CollisionExperimentManualConfirmation)}");
    }

    private void DrawSingleSetModelRetryDebug()
    {
        if (!ImGui.CollapsingHeader("单实例 SetModel 重试（Debug，高风险）"))
            return;

        var selected = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);

        if (selected == null)
        {
            ImGui.TextWrapped("请先在“本地场景物体”页选中一个 LocalLayoutObjectInstance。");
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "SetModel 直接调用仍为高风险调试项；创建、删除、恢复全部、批量复制都不会调用 SetModel。");
        ImGui.TextWrapped($"实例：{selected.Id}");
        ImGui.TextWrapped($"GraphicsObject：{selected.GraphicsObjectAddress}");
        ImGui.TextWrapped($"before：{selected.BeforeModelPath}");
        ImGui.TextWrapped($"target：{selected.CustomModelPath}");
        ImGui.TextWrapped($"after：{selected.AfterModelPath}");
        ImGui.TextWrapped($"category：{selected.ModelResourceCategoryReadback}");
        ImGui.TextWrapped($"loadState：{selected.ModelResourceHandleLoadState}");
        ImGui.TextWrapped($"SetModel 返回值：{selected.SetModelReturnValue}");
        ImGui.TextWrapped($"visible：{selected.ModelVisibilityReadback}");
        ImGui.TextWrapped($"transform：{selected.ModelTransformReadback}");
        ImGui.TextWrapped($"last exception：{(string.IsNullOrWhiteSpace(selected.LastSetModelException) ? "无" : selected.LastSetModelException)}");
        ImGui.TextWrapped($"结果：{selected.LastModelOverrideResult}");
        ImGui.TextWrapped($"错误：{(string.IsNullOrWhiteSpace(selected.LastModelOverrideError) ? "无" : selected.LastModelOverrideError)}");
        ImGui.TextWrapped($"bounds：{selected.ModelBoundsReadback}");
        ImGui.TextWrapped($"before dump：{selected.BeforeModelResourceHandleDump}");
        ImGui.TextWrapped($"after dump：{selected.AfterModelResourceHandleDump}");
        ImGui.TextWrapped($"pointer diff：{selected.ModelPointerDiff}");

        EditString("Debug target mdl path", selected.CustomModelPath, 512, value => selected.CustomModelPath = value);
        if (ImGui.Button("刷新只读 ResourceHandle / category"))
            this.localLayoutObjects.RefreshModel(selected.Id);

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "CleanupRender 会导致模型消失并使实例 transform 写入不安全，已禁用。");
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "UpdateCulling 必须在 bounds 已计算后调用，单独按钮已禁用。");
        this.DrawModelRefreshStepButton(selected.Id, "UpdateMaterials()", "UpdateMaterials");
        ImGui.SameLine();
        this.DrawModelRefreshStepButton(selected.Id, "UpdateRender()", "UpdateRender");
        ImGui.SameLine();
        this.DrawModelRefreshStepButton(selected.Id, "UpdateTransforms(true)", "UpdateTransformsTrue");
        this.DrawModelRefreshStepButton(selected.Id, "NotifyTransformChanged()", "NotifyTransformChanged");
        ImGui.SameLine();
        this.DrawModelRefreshStepButton(selected.Id, "IsTransformChanged=true", "SetIsTransformChangedTrue");
        this.DrawModelRefreshStepButton(selected.Id, "ComputeSphereBounds()", "ComputeSphereBounds");
        ImGui.SameLine();
        this.DrawModelRefreshStepButton(selected.Id, "ComputeSphereBounds -> UpdateCulling", "ComputeSphereBoundsThenUpdateCulling");
    }

    private void DrawBgPartRecreateExperimentDebug()
    {
        if (!ImGui.CollapsingHeader("BgPart recreate 单实例实验（Debug，默认禁用，高风险）"))
            return;

        var selected = string.IsNullOrWhiteSpace(this.selectedLocalLayoutObjectId)
            ? null
            : this.localLayoutObjects.GetById(this.selectedLocalLayoutObjectId);

        if (selected == null)
        {
            ImGui.TextWrapped("请先在“本地场景物体”页选中一个 LocalLayoutObjectInstance。");
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "此入口只用于单实例取证实验，不会接入创建、删除、恢复全部或批量流程。");
        ImGui.TextWrapped("调用链：DestroyPrimary -> CreatePrimary(originalTransform, &targetPathPointer)。DestroyPrimary 内部会走 CleanupRender，失败时可能需要切图/重载地图恢复。");
        ImGui.TextWrapped("禁止项：不调用 SetGraphics，不直接调用 BgObject.Create，不 memcpy，不批量，不碰 Collider。");
        ImGui.TextWrapped($"Last recreate status：{this.localLayoutObjects.LastRecreateExperimentStatus}");
        ImGui.TextWrapped($"实例：{selected.Id}");
        ImGui.TextWrapped($"slot：{selected.OccupiedSlotAddress}");
        ImGui.TextWrapped($"GraphicsObject：{selected.GraphicsObjectAddress}");
        ImGui.TextWrapped($"当前 path：{selected.CurrentResourcePath}");
        ImGui.TextWrapped($"target path：{selected.CustomModelPath}");
        ImGui.TextWrapped($"transformMode：{selected.TransformMode}");
        ImGui.TextWrapped($"collision mode：{(selected.TransformMode == LocalLayoutTransformMode.VisualOnly ? "VisualOnly，不移动 collider" : "FullLayoutWithCollision，允许模型和 collision 一起变化")}");
        ImGui.TextWrapped($"render invalid：{selected.IsRenderInvalid}");
        if (selected.IsRenderInvalid)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "当前实例 render 已失效。transform 写入和 RestoreAll 写入会被保护跳过；请切图/重载地图恢复。");

        EditString("Recreate target bg/...mdl", selected.CustomModelPath, 512, value => selected.CustomModelPath = value);

        var enabled = this.enableBgPartRecreateExperiment;
        if (ImGui.Checkbox("启用 BgPart recreate 高风险实验（默认关闭）", ref enabled))
        {
            this.enableBgPartRecreateExperiment = enabled;
            if (!enabled)
                this.confirmBgPartRecreateExperiment = false;
        }

        var confirm = this.confirmBgPartRecreateExperiment;
        if (ImGui.Checkbox("我确认只对当前单实例执行 DestroyPrimary -> CreatePrimary，可能导致模型消失或需要切图恢复", ref confirm))
            this.confirmBgPartRecreateExperiment = confirm;

        if (selected.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision)
        {
            var confirmCollision = this.confirmFullLayoutCollisionMode;
            if (ImGui.Checkbox("我确认 FullLayoutWithCollision recreate 允许模型和碰撞体一起变化", ref confirmCollision))
                this.confirmFullLayoutCollisionMode = confirmCollision;
        }

        var blockReason = this.localLayoutObjects.GetRecreateExperimentBlockReason(
            selected.Id,
            selected.CustomModelPath,
            this.realNpcSpawn.EnableUnsafeNativeWrites,
            this.enableBgPartRecreateExperiment,
            this.confirmBgPartRecreateExperiment);
        if (string.IsNullOrWhiteSpace(blockReason)
            && selected.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision
            && !this.confirmFullLayoutCollisionMode)
        {
            blockReason = "FullLayoutWithCollision recreate 需要勾选“我确认启用危险碰撞模式”。";
        }

        if (!string.IsNullOrWhiteSpace(blockReason))
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), $"按钮禁用原因：{blockReason}");

        ImGui.BeginDisabled(!string.IsNullOrWhiteSpace(blockReason));
        if (ImGui.Button("保存当前实例 recreate 快照"))
            this.localLayoutObjects.SaveRecreateSnapshot(selected.Id, selected.CustomModelPath);
        ImGui.SameLine();
        if (ImGui.Button("DestroyPrimary -> CreatePrimary(target)"))
            this.localLayoutObjects.ExecuteRecreateExperiment(
                selected.Id,
                selected.CustomModelPath,
                this.realNpcSpawn.EnableUnsafeNativeWrites,
                this.enableBgPartRecreateExperiment,
                this.confirmBgPartRecreateExperiment);
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextWrapped("快照 / readback：");
        ImGui.TextWrapped($"snapshot GraphicsObject：{selected.RecreateSnapshotGraphicsObject}");
        ImGui.TextWrapped($"snapshot IndexInPool：{selected.RecreateSnapshotIndexInPool}");
        ImGui.TextWrapped($"snapshot transformMode：{selected.RecreateSnapshotTransformMode}");
        ImGui.TextWrapped($"snapshot transform：{selected.RecreateSnapshotTransform}");
        ImGui.TextWrapped($"snapshot collider：{selected.RecreateSnapshotColliderAddress}");
        ImGui.TextWrapped($"original path：{selected.RecreateSnapshotOriginalPath}");
        ImGui.TextWrapped($"target path：{selected.RecreateSnapshotTargetPath}");
        ImGui.TextWrapped($"snapshot ModelResourceHandle：{selected.RecreateSnapshotModelResourceHandle}");
        ImGui.TextWrapped($"stable UTF8 buffer：{selected.RecreatePinnedPathAddress}");
        ImGui.TextWrapped($"stable char** storage：{selected.RecreatePathPointerAddress}");
        ImGui.TextWrapped($"after GraphicsObject：{selected.RecreateAfterGraphicsObject}");
        ImGui.TextWrapped($"after ModelResourceHandle：{selected.RecreateAfterModelResourceHandle}");
        ImGui.TextWrapped($"after visible：{selected.RecreateAfterVisible}");
        ImGui.TextWrapped($"after transform：{selected.RecreateAfterTransform}");
        ImGui.TextWrapped($"after collider：{selected.RecreateAfterColliderAddress}");
        ImGui.TextWrapped($"layout restore：{selected.RecreateLayoutRestoreResult}");
        ImGui.TextWrapped($"visual reapply：{selected.RecreateVisualReapplyResult}");
        ImGui.TextWrapped($"collision mode result：{selected.RecreateCollisionModeResult}");
        ImGui.TextWrapped($"result：{selected.RecreateLastResult}");
        ImGui.TextWrapped($"error：{(string.IsNullOrWhiteSpace(selected.RecreateLastError) ? "无" : selected.RecreateLastError)}");

        if (ImGui.Button("人工确认：外观已变化")) selected.RecreateManualConfirmation = "外观已变化";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：仍未变化")) selected.RecreateManualConfirmation = "仍未变化";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：模型消失")) selected.RecreateManualConfirmation = "模型消失";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：脚下仍无碰撞")) selected.RecreateManualConfirmation = "脚下仍无碰撞";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：脚下出现碰撞")) selected.RecreateManualConfirmation = "脚下出现碰撞";
        ImGui.SameLine();
        if (ImGui.Button("人工确认：游戏不稳定")) selected.RecreateManualConfirmation = "游戏不稳定";
        ImGui.TextWrapped($"人工记录：{(string.IsNullOrWhiteSpace(selected.RecreateManualConfirmation) ? "未记录" : selected.RecreateManualConfirmation)}");
    }

    private void DrawModelRefreshStepButton(string instanceId, string label, string stepName)
    {
        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites);
        if (ImGui.Button(label))
            this.localLayoutObjects.ExecuteModelRefreshStep(instanceId, stepName);
        ImGui.EndDisabled();
    }
    private IEnumerable<LayoutProbeInstance> FilteredBgParts()
    {
        var query = this.layoutProbe.Instances.Where(instance => string.Equals(instance.Type, "BgPart", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(this.bgPartSearchText))
        {
            query = query.Where(instance =>
                instance.ResourcePath.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.Type.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase) ||
                instance.Address.Contains(this.bgPartSearchText, StringComparison.OrdinalIgnoreCase));
        }
        return query.OrderBy(instance => instance.DistanceToPlayer);
    }

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
            Position = new Vector3Data { X = position.X, Y = position.Y, Z = position.Z },
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

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;
}

