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
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly Action reloadAction;

    private string selectedNpcId = string.Empty;
    private string selectedActorRuntimeId = string.Empty;
    private string selectedLocalLayoutObjectId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
    private string glamourerSearchText = string.Empty;
    private string gameNpcSearchText = string.Empty;
    private bool localLayoutFullCollisionMode;
    private bool confirmFullLayoutCollisionMode;

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
        this.gameNpcCatalog = gameNpcCatalog;
        this.glamourerDesignCatalog = glamourerDesignCatalog;
        this.reloadAction = reloadAction;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 660),
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
        ImGui.TextWrapped($"Glamourer：{this.realNpcSpawn.GlamourerIpcProbeMessage}");
        ImGui.TextWrapped($"Glamourer ApplyDesign：{this.realNpcSpawn.GlamourerIpcBridgeMessage}");
        ImGui.TextWrapped($"外观队列：长度 {this.realNpcSpawn.AppearanceQueueLength}，当前 {this.realNpcSpawn.AppearanceQueueCurrentActor}");
        ImGui.TextWrapped($"外观队列状态：{this.realNpcSpawn.AppearanceQueueStatus}");
        ImGui.TextWrapped($"Actor 有效性：{this.realNpcSpawn.ActorValidityMonitorStatus}");
        ImGui.TextWrapped($"GPose：当前={this.realNpcSpawn.CurrentIsGposing}，上一帧={this.realNpcSpawn.PreviousFrameIsGposing}，重建={this.realNpcSpawn.LastGposeRebuildResult}");
        ImGui.Separator();
        ImGui.TextWrapped($"本地场景物体状态：{this.localLayoutObjects.LastStatus}");
        ImGui.TextWrapped($"占用 slot：{this.localLayoutObjects.ActiveOccupiedSlotCount}，重复 slot：{this.localLayoutObjects.DuplicateSlotCount}");
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
            foreach (var npc in this.database.Npcs)
            {
                var selected = string.Equals(this.selectedNpcId, npc.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{npc.Name} ({npc.Id})", selected))
                    this.selectedNpcId = npc.Id;
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
        var autoPlayDefaultAnimation = currentNpc.AutoPlayDefaultAnimation;
        if (ImGui.Checkbox("生成后自动播放默认动画", ref autoPlayDefaultAnimation))
            currentNpc.AutoPlayDefaultAnimation = autoPlayDefaultAnimation;
        var lookAtPlayerEnabled = currentNpc.LookAtPlayerEnabled;
        if (ImGui.Checkbox("靠近时看向玩家", ref lookAtPlayerEnabled))
            currentNpc.LookAtPlayerEnabled = lookAtPlayerEnabled;
        var lookRadius = currentNpc.LookAtRadius;
        if (ImGui.InputFloat("看向半径", ref lookRadius))
            currentNpc.LookAtRadius = Math.Max(0.1f, lookRadius);
        DrawEnumCombo("看向模式", currentNpc.LookAtMode, value => currentNpc.LookAtMode = value);
        var respawnAfterGpose = currentNpc.RespawnAfterGpose;
        if (ImGui.Checkbox("退出 GPose 后自动重建", ref respawnAfterGpose))
            currentNpc.RespawnAfterGpose = respawnAfterGpose;

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

        this.DrawNpcAppearanceEditor(currentNpc);

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "操作");
        ImGui.TextWrapped($"当前已生成 Actor 数量：{this.realNpcSpawn.GetActorCountForNpc(currentNpc.Id)}");
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
        ImGui.SameLine();
        if (ImGui.Button("删除此 NPC 的全部 Actor"))
            this.realNpcSpawn.DespawnAllForNpc(currentNpc.Id);
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
        if (ImGui.BeginCombo("选中配置 NPC", this.SelectedNpcLabel()))
        {
            foreach (var npc in this.database.Npcs)
            {
                var selected = string.Equals(this.selectedNpcId, npc.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{npc.Name} ({npc.Id})", selected))
                    this.selectedNpcId = npc.Id;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(selectedNpc == null || !this.realNpcSpawn.CanSpawnRealActor);
        if (ImGui.Button("生成选中 NPC 的唯一 Actor") && selectedNpc != null)
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
        if (ImGui.Button("删除选中 NPC 的全部 Actor") && selectedNpc != null)
            this.realNpcSpawn.DespawnAllForNpc(selectedNpc.Id);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("删除全部 Actor"))
            this.realNpcSpawn.DespawnAll();
        ImGui.SameLine();
        if (ImGui.Button("刷新有效性"))
            this.realNpcSpawn.RefreshActors();

        ImGui.TextWrapped($"Actor 数量：{this.realNpcSpawn.Actors.Count} | 队列长度：{this.realNpcSpawn.AppearanceQueueLength} | {this.realNpcSpawn.AppearanceQueueStatus}");
        ImGui.TextWrapped($"SpawnIntent 数量：{this.realNpcSpawn.SpawnIntentCount}");
        if (ImGui.CollapsingHeader("SpawnIntent 状态"))
        {
            foreach (var intent in this.realNpcSpawn.SpawnIntents)
                ImGui.TextWrapped($"{intent.NpcId} | shouldBeSpawned={intent.ShouldBeSpawned} | suppressed={intent.SuppressedUntilUserSpawn} | reason={intent.LastDespawnReason}");
        }

        if (!ImGui.BeginTable("RuntimeActors", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 320f)))
            return;
        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("runtimeId");
        ImGui.TableSetupColumn("npcId");
        ImGui.TableSetupColumn("npcName");
        ImGui.TableSetupColumn("objectIndex");
        ImGui.TableSetupColumn("address");
        ImGui.TableSetupColumn("source");
        ImGui.TableSetupColumn("valid");
        ImGui.TableSetupColumn("position");
        ImGui.TableHeadersRow();
        foreach (var actor in this.realNpcSpawn.Actors)
        {
            ImGui.TableNextRow();
            ImGui.PushID(actor.RuntimeId);
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable("选中", string.Equals(this.selectedActorRuntimeId, actor.RuntimeId, StringComparison.Ordinal)))
            {
                this.selectedActorRuntimeId = actor.RuntimeId;
                this.realNpcSpawn.SelectActorForPlayerLookAt(actor.RuntimeId);
            }
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
            ImGui.TextWrapped(actor.SpawnSource);
            ImGui.TableSetColumnIndex(7);
            ImGui.TextUnformatted(actor.IsValid ? "有效" : "失效");
            ImGui.TableSetColumnIndex(8);
            ImGui.TextWrapped(FormatVector(actor.LastKnownPosition));
            ImGui.PopID();
        }
        ImGui.EndTable();

        this.DrawSelectedActorControls();
    }

    private void DrawSelectedActorControls()
    {
        var actor = string.IsNullOrWhiteSpace(this.selectedActorRuntimeId) ? null : this.realNpcSpawn.GetActor(this.selectedActorRuntimeId);
        if (actor == null)
            return;

        var npc = this.database.GetNpcById(actor.NpcId);
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), $"选中 Actor：{ShortId(actor.RuntimeId)}");
        ImGui.TextWrapped($"NPC：{actor.NpcName} ({actor.NpcId})");
        ImGui.TextWrapped($"外观来源：{npc?.Appearance.SourceType.ToString() ?? "NPC 配置不存在"} | {npc?.Appearance.DisplayName}");
        ImGui.TextWrapped($"最后外观结果：{actor.LastAppearanceApplyResult}");
        ImGui.TextWrapped($"最后外观错误：{(string.IsNullOrWhiteSpace(actor.LastAppearanceError) ? "无" : actor.LastAppearanceError)}");
        ImGui.TextWrapped($"最后动画：{actor.LastAnimationResult} / {actor.LastAnimationError}");
        ImGui.TextWrapped($"最后错误：{(string.IsNullOrWhiteSpace(actor.LastError) ? "无" : actor.LastError)}");

        var unsafeEnabled = this.realNpcSpawn.EnableUnsafeNativeWrites;
        if (ImGui.Checkbox("启用 Unsafe/native 写入", ref unsafeEnabled))
            this.realNpcSpawn.EnableUnsafeNativeWrites = unsafeEnabled;
        var lookAtEnabled = this.realNpcSpawn.PlayerHeadLookAtSelectedActorEnabled;
        if (ImGui.Checkbox("选中 Actor 后玩家头部看向 Actor", ref lookAtEnabled))
            this.realNpcSpawn.PlayerHeadLookAtSelectedActorEnabled = lookAtEnabled;
        ImGui.TextWrapped($"玩家看向 Actor：{this.realNpcSpawn.IsPlayerLookingAtSelectedActor} | {this.realNpcSpawn.LastPlayerLookAtResult} | {this.realNpcSpawn.LastPlayerLookAtError}");

        ImGui.BeginDisabled(!actor.IsValid || npc == null);
        if (ImGui.Button("应用 NPC 外观"))
            this.realNpcSpawn.EnqueueNpcAppearance(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("重新生成并应用外观"))
        {
            var newActor = this.realNpcSpawn.RegenerateAndApplyAppearance(actor.RuntimeId);
            if (newActor != null)
                this.selectedActorRuntimeId = newActor.RuntimeId;
        }
        ImGui.SameLine();
        if (ImGui.Button("保存当前位置到 NPC 配置"))
            this.realNpcSpawn.SaveActorPositionToNpc(actor.RuntimeId);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!actor.IsValid || !this.realNpcSpawn.EnableUnsafeNativeWrites);
        if (ImGui.Button("移到玩家当前位置") && this.runtime.PlayerPosition.HasValue)
            this.realNpcSpawn.MoveActor(actor.RuntimeId, this.runtime.PlayerPosition.Value);
        ImGui.SameLine();
        if (ImGui.Button("移到 NPC 配置坐标") && npc != null)
            this.realNpcSpawn.MoveActor(actor.RuntimeId, ToVector3(npc.Position));
        ImGui.SameLine();
        if (ImGui.Button("播放默认动画") && npc != null)
            this.realNpcSpawn.PlayAnimation(actor.RuntimeId, npc.DefaultAnimationId);
        ImGui.SameLine();
        if (ImGui.Button("停止动画/恢复 idle"))
            this.realNpcSpawn.StopAnimation(actor.RuntimeId);
        ImGui.EndDisabled();

        if (ImGui.Button("让玩家看向选中 Actor"))
            this.realNpcSpawn.PlayerLookAtSelectedActorNow();
        ImGui.SameLine();
        if (ImGui.Button("停止玩家看向"))
            this.realNpcSpawn.StopPlayerLookAt();
        ImGui.SameLine();
        if (ImGui.Button("重建此 Actor"))
            this.realNpcSpawn.RespawnActor(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button("删除此 Actor"))
        {
            this.realNpcSpawn.Despawn(actor.RuntimeId, DespawnReason.UserRequested);
            this.selectedActorRuntimeId = string.Empty;
        }
    }

    private void DrawLocalLayoutObjects()
    {
        ImGui.TextWrapped("正式功能：复用当前地图已有 BgPart slot。VisualOnly 写 Graphics.Scene.Object transform，不移动 collision。");
        ImGui.TextWrapped($"状态：{this.localLayoutObjects.LastStatus}");
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
        if (fullLayoutBlocked)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), "危险模式需要二次确认。");

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
            this.localLayoutObjects.CleanupDuplicateInstances(auto: false);
        ImGui.EndDisabled();

        this.DrawLocalLayoutObjectTable();
        this.DrawSelectedLocalLayoutObjectControls();
    }

    private void DrawBgPartSelectionControls()
    {
        ImGui.Separator();
        if (ImGui.Button("重新扫描 BgPart"))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);
        ImGui.SameLine();
        if (ImGui.Button("选择最近 BgPart"))
        {
            var nearest = this.layoutProbe.Instances
                .Where(instance => string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
                .OrderBy(instance => instance.DistanceToPlayer)
                .FirstOrDefault();
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
        if (!ImGui.BeginTable("LocalLayoutObjects", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 240f)))
            return;

        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("instanceId");
        ImGui.TableSetupColumn("slotAddress");
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("mode");
        ImGui.TableSetupColumn("collision moved");
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
            ImGui.TextWrapped(instance.Id);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(instance.OccupiedSlotAddress);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(instance.SourceResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(instance.TransformMode.ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(instance.HasCollisionMoved ? "是" : "否");
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(instance.IsRestored ? "是" : "否");
            ImGui.TableSetColumnIndex(7);
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
        ImGui.TextWrapped($"resourcePath：{selected.SourceResourcePath}");
        ImGui.TextWrapped($"slot address：{selected.OccupiedSlotAddress}");
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

        var disabled = !this.realNpcSpawn.EnableUnsafeNativeWrites || selected.IsDuplicate || selected.IsRestored || fullLayoutNeedsConfirmation;
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
        if (ImGui.CollapsingHeader("IPC 详情"))
        {
            ImGui.TextWrapped($"Brio IPC 可用：{this.realNpcSpawn.IsBrioIpcAvailable}");
            ImGui.TextWrapped($"Brio IPC 版本：{this.realNpcSpawn.BrioIpcVersion?.ToString() ?? "未知"}");
            ImGui.TextWrapped($"Glamourer 版本：{this.realNpcSpawn.GlamourerVersion}");
            ImGui.TextWrapped($"Glamourer ApplyDesign 注册：{this.realNpcSpawn.IsGlamourerApplyDesignRegistered}");
            ImGui.TextWrapped($"Glamourer 签名：{this.realNpcSpawn.SelectedGlamourerApplyDesign?.Signature ?? "未绑定"}");
            ImGui.TextWrapped($"最后参数：{this.realNpcSpawn.GlamourerIpcBridgeLastInvocationParameters}");
            ImGui.TextWrapped($"最后错误：{this.realNpcSpawn.GlamourerIpcBridgeLastError}");
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
