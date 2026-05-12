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
    private readonly Action reloadAction;

    private string selectedLocalLayoutObjectId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
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
        this.reloadAction = reloadAction;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 620),
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

        ImGui.Separator();
        if (!ImGui.BeginTabBar("LqrMainTabs"))
            return;

        if (ImGui.BeginTabItem("运行调试"))
        {
            this.DrawRuntimeDebug();
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
        ImGui.TextWrapped($"Quest/NPC 文件：{this.database.QuestFilePath}");
        ImGui.TextWrapped($"使用开发路径：{this.database.IsUsingDevelopmentQuestPath}");
        ImGui.TextWrapped($"当前地图 territory：{this.runtime.TerritoryType}");
        ImGui.TextWrapped($"玩家位置：{FormatVector(this.runtime.PlayerPosition)}");
        ImGui.TextWrapped($"Unsafe/native 写入：{this.realNpcSpawn.EnableUnsafeNativeWrites}");
        ImGui.TextWrapped($"Brio：{this.realNpcSpawn.BrioAssemblyStatus}");
        ImGui.TextWrapped($"Glamourer/Penumbra：{this.realNpcSpawn.GlamourerIpcProbeMessage}");
        ImGui.TextWrapped($"本地场景物体状态：{this.localLayoutObjects.LastStatus}");
    }

    private void DrawLocalLayoutObjects()
    {
        ImGui.TextWrapped("正式功能：复用当前地图已有 BgPart slot。VisualOnly 只写 Graphics.Scene.Object transform，不移动 collision。");
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
        ImGui.SameLine();
        ImGui.TextDisabled("会自动清理重复实例");
        ImGui.SameLine();
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

        if (!ImGui.BeginTable("BgPartPool", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 420f)))
            return;
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn("total");
        ImGui.TableSetupColumn("available");
        ImGui.TableSetupColumn("occupied");
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
            ImGui.TextUnformatted($"{slots.Min(slot => slot.DistanceToPlayer):F1}");
            ImGui.TableSetColumnIndex(5);
            ImGui.BeginDisabled(available.Count == 0 || !this.runtime.PlayerPosition.HasValue || !this.realNpcSpawn.EnableUnsafeNativeWrites);
            if (ImGui.Button("占用最近 slot"))
            {
                var created = this.localLayoutObjects.CreateFromCandidate(available.FirstOrDefault(), this.runtime.PlayerPosition!.Value, LocalLayoutTransformMode.VisualOnly);
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

    private static string FormatVector(Vector3? vector)
        => vector.HasValue ? FormatVector(vector.Value) : "不可用";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;
}
