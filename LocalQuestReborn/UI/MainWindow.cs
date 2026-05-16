using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using System.Globalization;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace LocalQuestReborn.UI;

public sealed class MainWindow : Window
{
    private static readonly JsonSerializerOptions MapPresetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Configuration configuration;
    private readonly QuestDatabase database;
    private readonly QuestRuntimeService runtime;
    private readonly IDataManager dataManager;
    private readonly RealNpcSpawnService realNpcSpawn;
    private readonly LayoutProbeService layoutProbe;
    private readonly LayerDumpService layerDump;
    private readonly LocalLayoutObjectService localLayoutObjects;
    private readonly LocalLightNativeService localLights;
    private readonly SceneEditorService sceneEditor;
    private readonly SceneEditorSelectionService sceneEditorSelection;
    private readonly BgPartCollisionSourceProbeService bgPartCollisionSourceProbe;
    private readonly AnimatedBgPartControllerProbeService animatedBgPartControllerProbe;
    private readonly StandaloneBgObjectProbeService standaloneBgObjectProbe;
    private readonly StandaloneRenderListProbeService standaloneRenderListProbe;
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly ActorAnimationPickerService actorAnimationPicker;
    private readonly ActorLipSyncPresetService lipSyncPresets;
    private readonly ActionTimelinePickerWindow actionTimelinePickerWindow;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly Action reloadAction;
    private readonly Action saveConfiguration;
    private readonly Func<bool> isGposing;
    private readonly TransformEditState sceneEditorTransformEdit = new();

    private string sceneEditorGlamourerDesignName = string.Empty;
    private string sceneEditorGlamourerDesignStatus = "No native actor design save attempted.";
    private Vector2 deleteAllActorsConfirmPopupPosition;
    private Vector2 confirmationPopupPosition;
    private string pendingNativeRestoreRecordId = string.Empty;
    private ProtectedBgPartResourcePath? pendingProtectedResourcePathRemove;
    private ProtectedBgPartSlot? pendingProtectedSlotRemove;
    private PreferredModifyBgPartResourcePath? pendingPreferredResourcePathRemove;
    private PreferredModifyBgPartSlot? pendingPreferredSlotRemove;
    private uint selectedMapPresetTerritory;
    private string mapPresetImportPath = string.Empty;
    private string mapPresetStatus = string.Empty;
    private MapModificationPreset? pendingImportPreset;
    private string pendingImportPath = string.Empty;
    private List<string> pendingImportConflicts = [];
    private bool pendingImportConfirmChecked;
    private readonly Dictionary<uint, string> territoryNameCache = [];

    private string selectedNpcId = string.Empty;
    private string selectedActorRuntimeId = string.Empty;
    private string selectedLocalLayoutObjectId = string.Empty;
    private string lastWorldTransformReadLocalLayoutObjectId = string.Empty;
    private string selectedLocalLightId = string.Empty;
    private string selectedBgPartAddress = string.Empty;
    private string templateBgPartAddress = string.Empty;
    private string bgPartSearchText = string.Empty;
    private string protectedBgPartSearchText = string.Empty;
    private string preferredModifyBgPartSearchText = string.Empty;
    private string glamourerSearchText = string.Empty;
    private string gameNpcSearchText = string.Empty;
    private string cachedGlamourerSearchText = "\0";
    private string cachedGameNpcSearchText = "\0";
    private DateTime nextActorSourceSearchRefreshAt = DateTime.MinValue;
    private DateTime nextActorRuntimeSnapshotRefreshAt = DateTime.MinValue;
    private IReadOnlyList<GlamourerDesignEntry> cachedGlamourerDesignResults = [];
    private IReadOnlyList<GameNpcCatalogEntry> cachedGameNpcResults = [];
    private IReadOnlyList<RuntimeActorInstance> cachedActorRuntimeSnapshot = [];
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
        IDataManager dataManager,
        ExperimentalNpcService experimentalNpc,
        RealNpcSpawnService realNpcSpawn,
        PropRuntimeService propRuntime,
        LayoutProbeService layoutProbe,
        LayoutInstanceTransformService layoutTransform,
        LayoutInstanceCloneService layoutClone,
        LayerDumpService layerDump,
        LocalLayoutObjectService localLayoutObjects,
        LocalLightNativeService localLights,
        SceneEditorService sceneEditor,
        SceneEditorSelectionService sceneEditorSelection,
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
        ActorLipSyncPresetService lipSyncPresets,
        ActionTimelinePickerWindow actionTimelinePickerWindow,
        PenumbraIpcService penumbraIpc,
        Action reloadAction,
        Action saveConfiguration,
        Func<bool> isGposing)
        : base("Yourcraft##YourcraftMain")
    {
        this.configuration = configuration;
        this.database = database;
        this.runtime = runtime;
        this.dataManager = dataManager;
        this.realNpcSpawn = realNpcSpawn;
        this.layoutProbe = layoutProbe;
        this.layerDump = layerDump;
        this.localLayoutObjects = localLayoutObjects;
        this.localLights = localLights;
        this.sceneEditor = sceneEditor;
        this.sceneEditorSelection = sceneEditorSelection;
        this.bgPartCollisionSourceProbe = bgPartCollisionSourceProbe;
        this.animatedBgPartControllerProbe = animatedBgPartControllerProbe;
        this.standaloneBgObjectProbe = standaloneBgObjectProbe;
        this.standaloneRenderListProbe = standaloneRenderListProbe;
        this.gameNpcCatalog = gameNpcCatalog;
        this.glamourerDesignCatalog = glamourerDesignCatalog;
        this.actorAnimationPicker = actorAnimationPicker;
        this.lipSyncPresets = lipSyncPresets;
        this.actionTimelinePickerWindow = actionTimelinePickerWindow;
        this.penumbraIpc = penumbraIpc;
        this.reloadAction = reloadAction;
        this.saveConfiguration = saveConfiguration;
        this.isGposing = isGposing;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 680),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        Localization.CurrentLanguage = Localization.Normalize(this.configuration.UiLanguage);
        var inGpose = this.IsInGpose();
        if (inGpose)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("GPose Actor 模式", "GPose Actor Mode"));
            ImGui.TextWrapped(T(
                "Actor 会在 GPose 内尝试重建并保持可操作；创建/删除等结构性操作仍建议在普通场景处理。",
                "Actors are restored inside GPose when possible. Creation and deletion are still best handled in the normal scene."));
            ImGui.Separator();
        }

        if (ImGui.Button(T("重新读取配置", "Reload Config")))
            this.reloadAction();
        ImGui.SameLine();
        if (ImGui.Button(T("保存配置", "Save Config")))
            this.database.Save();
        ImGui.SameLine();
        if (ImGui.Button(T("重新探测 IPC", "Refresh IPC")))
        {
            this.realNpcSpawn.ProbeBrioIpc();
            this.realNpcSpawn.ProbeGlamourerIpc();
        }

        this.SyncMainUiSelectionFromSceneEditor();

        ImGui.Separator();
        if (!ImGui.BeginTabBar("YourcraftMainTabs"))
            return;

        if (ImGui.BeginTabItem(T("Actor 实例", "Actors")))
        {
            this.DrawActorInstances();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("本地场景物体", "Local Scene Objects")))
        {
            this.DrawLocalLayoutObjects();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("本地灯光", "Local Lights")))
        {
            this.DrawLocalLights();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("画面编辑", "Scene Editor")))
        {
            this.DrawSceneEditor();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("原生场景修改", "Native Scene Edits")))
        {
            this.DrawSceneEditorHiddenList();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("BgPart 槽位池", "BgPart Slot Pool")))
        {
            this.DrawBgPartPool();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("BgPart 保护列表", "BgPart Protection")))
        {
            this.DrawBgPartProtectionTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("BgPart 优先改动列表", "BgPart Preferred Edits")))
        {
            this.DrawBgPartPreferredModifyTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("设置", "Settings")))
        {
            this.DrawSettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawSettings()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("设置", "Settings"));

        var language = Localization.Normalize(this.configuration.UiLanguage);
        if (ImGui.BeginCombo(T("界面语言", "Interface Language"), Localization.DisplayName(language)))
        {
            var chineseSelected = string.Equals(language, Localization.Chinese, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable("中文", chineseSelected))
            {
                this.configuration.UiLanguage = Localization.Chinese;
                Localization.CurrentLanguage = Localization.Chinese;
                this.saveConfiguration();
            }
            if (chineseSelected)
                ImGui.SetItemDefaultFocus();

            var englishSelected = string.Equals(language, Localization.English, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable("English", englishSelected))
            {
                this.configuration.UiLanguage = Localization.English;
                Localization.CurrentLanguage = Localization.English;
                this.saveConfiguration();
            }
            if (englishSelected)
                ImGui.SetItemDefaultFocus();

            ImGui.EndCombo();
        }

        var showUiInGpose = this.configuration.ShowPluginUiInGpose;
        if (ImGui.Checkbox(T("GPose 中显示插件窗口", "Show plugin windows in GPose"), ref showUiInGpose))
        {
            this.configuration.ShowPluginUiInGpose = showUiInGpose;
            this.saveConfiguration();
        }

        ImGui.TextDisabled(T("语言切换会立即保存并在当前窗口刷新。", "Language changes are saved immediately and refresh this window."));
        ImGui.Separator();
        this.DrawMapPresetImportExportSettings();
    }

    private static string T(string chinese, string english) => Localization.T(chinese, english);

    private void DrawMapPresetImportExportSettings()
    {
        DrawYellowSectionLabel("地图预设导入 / 导出", "Map Preset Import / Export");
        var territories = this.GetModifiedTerritories().ToList();
        if (territories.Count == 0)
        {
            ImGui.TextDisabled(T("当前没有可导出的地图修改。", "There are no modified maps to export."));
        }
        else
        {
            if (this.selectedMapPresetTerritory == 0 || !territories.Contains(this.selectedMapPresetTerritory))
                this.selectedMapPresetTerritory = territories[0];

            if (ImGui.BeginCombo(T("导出地图", "Export Map"), this.FormatTerritoryPresetLabel(this.selectedMapPresetTerritory)))
            {
                foreach (var territory in territories)
                {
                    var selected = territory == this.selectedMapPresetTerritory;
                    if (ImGui.Selectable(this.FormatTerritoryPresetLabel(territory), selected))
                        this.selectedMapPresetTerritory = territory;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button(T("导出 JSON", "Export JSON")))
            {
                try
                {
                    var path = this.ExportMapPreset(this.selectedMapPresetTerritory);
                    this.mapPresetStatus = T($"已导出：{path}", $"Exported: {path}");
                }
                catch (Exception ex)
                {
                    this.mapPresetStatus = T($"导出失败：{ex.Message}", $"Export failed: {ex.Message}");
                }
            }
        }

        ImGui.Spacing();
        var presetFiles = this.GetMapPresetFiles();
        var selectedImportLabel = string.IsNullOrWhiteSpace(this.mapPresetImportPath)
            ? T("选择 JSON 文件", "Select JSON File")
            : Path.GetFileName(this.mapPresetImportPath);
        if (ImGui.BeginCombo(T("导入文件", "Import File"), selectedImportLabel))
        {
            foreach (var path in presetFiles)
            {
                var selected = string.Equals(path, this.mapPresetImportPath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(Path.GetFileName(path), selected))
                    this.mapPresetImportPath = path;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(T("JSON 路径", "JSON Path"), ref this.mapPresetImportPath, 512);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(this.mapPresetImportPath));
        if (ImGui.Button(T("导入 JSON", "Import JSON")))
            this.BeginMapPresetImport(this.mapPresetImportPath);
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(this.mapPresetStatus))
            ImGui.TextWrapped(this.mapPresetStatus);

        this.DrawMapPresetImportConflictPopup();
    }

    private IEnumerable<uint> GetModifiedTerritories()
    {
        return this.database.ActorConfigs.Select(item => (uint)item.TerritoryType)
            .Concat(this.database.Npcs.Select(item => (uint)item.TerritoryType))
            .Concat(this.configuration.SceneEditorLocalBgParts.Select(item => item.TerritoryId))
            .Concat(this.configuration.SceneEditorLocalActors.Select(item => item.TerritoryId))
            .Concat(this.configuration.SceneEditorNativeModifications.Where(item => item.IsHidden || item.IsModified).Select(item => item.TerritoryId))
            .Concat(this.configuration.LocalLights.Select(item => item.TerritoryId))
            .Concat(this.configuration.ProtectedBgPartSlots.Select(item => item.TerritoryType))
            .Concat(this.configuration.ProtectedBgPartResourcePaths.Where(item => item.AppliesToCurrentTerritoryOnly).Select(item => item.TerritoryType))
            .Concat(this.configuration.PreferredModifyBgPartSlots.Select(item => item.TerritoryType))
            .Concat(this.configuration.PreferredModifyBgPartResourcePaths.Where(item => item.AppliesToCurrentTerritoryOnly).Select(item => item.TerritoryType))
            .Where(item => item != 0)
            .Distinct()
            .OrderBy(item => item);
    }

    private string FormatTerritoryPresetLabel(uint territory)
    {
        var name = this.GetTerritoryDisplayName(territory);
        return string.IsNullOrWhiteSpace(name)
            ? T($"地图 {territory}", $"Territory {territory}")
            : $"{name}({territory})";
    }

    private string GetTerritoryDisplayName(uint territory)
    {
        if (this.territoryNameCache.TryGetValue(territory, out var cached))
            return cached;

        var name = string.Empty;
        try
        {
            var sheet = this.dataManager.GetExcelSheet<TerritoryTypeSheet>();
            foreach (var row in sheet)
            {
                if (ReadUIntMember(row, "RowId") != territory)
                    continue;

                name = FirstNonEmpty(
                    ExtractDisplayText(ReadMemberValue(row, "PlaceName")),
                    ExtractDisplayText(ReadMemberValue(row, "Place")),
                    ExtractDisplayText(ReadMemberValue(row, "Zone")),
                    ExtractDisplayText(ReadMemberValue(row, "Region")),
                    ExtractDisplayText(ReadMemberValue(row, "Name")));
                break;
            }
        }
        catch
        {
            name = string.Empty;
        }

        this.territoryNameCache[territory] = name;
        return name;
    }

    private static string ExtractDisplayText(object? value, int depth = 0)
    {
        if (value == null || depth > 4)
            return string.Empty;

        if (value is string text)
            return text.Trim();

        var type = value.GetType();
        if (type.IsPrimitive || value is decimal)
            return string.Empty;

        foreach (var memberName in new[] { "ValueNullable", "Value", "Name", "Singular", "Text" })
        {
            var nested = ReadMemberValue(value, memberName);
            var nestedText = ExtractDisplayText(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(nestedText))
                return nestedText;
        }

        var fallback = value.ToString()?.Trim() ?? string.Empty;
        if (type.Name.Contains("SeString", StringComparison.OrdinalIgnoreCase))
            return fallback;
        if ((type.FullName?.Contains("RowRef", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (type.Namespace?.StartsWith("Lumina.Excel.Sheets", StringComparison.Ordinal) ?? false))
        {
            return string.Empty;
        }

        return fallback.Length == 0 || fallback.StartsWith(type.FullName ?? type.Name, StringComparison.Ordinal)
            ? string.Empty
            : fallback;
    }

    private static uint ReadUIntMember(object source, string name)
    {
        var value = ReadMemberValue(source, name);
        return value switch
        {
            byte byteValue => byteValue,
            ushort ushortValue => ushortValue,
            uint uintValue => uintValue,
            int intValue when intValue >= 0 => (uint)intValue,
            long longValue when longValue >= 0 => (uint)Math.Min(longValue, uint.MaxValue),
            ulong ulongValue => (uint)Math.Min(ulongValue, uint.MaxValue),
            _ => 0,
        };
    }

    private static object? ReadMemberValue(object source, string name)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
        var type = source.GetType();
        var property = type.GetProperty(name, flags);
        if (property != null)
        {
            try
            {
                return property.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        var field = type.GetField(name, flags);
        if (field == null)
            return null;

        try
        {
            return field.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private string MapPresetDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(documents, "YourcraftPreset");
        }
    }

    private IReadOnlyList<string> GetMapPresetFiles()
    {
        Directory.CreateDirectory(this.MapPresetDirectory);
        return Directory.EnumerateFiles(this.MapPresetDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private string ExportMapPreset(uint territory)
    {
        Directory.CreateDirectory(this.MapPresetDirectory);
        var preset = this.BuildMapPreset(territory);
        var mapName = this.GetTerritoryDisplayName(territory);
        if (string.IsNullOrWhiteSpace(mapName))
            mapName = T("地图", "Map");
        var fileName = $"{SanitizeFileName($"{mapName}({territory})")}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var path = Path.Combine(this.MapPresetDirectory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(preset, MapPresetJsonOptions));
        return path;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "YourcraftPreset" : sanitized;
    }

    private MapModificationPreset BuildMapPreset(uint territory)
    {
        var actorNpcIds = this.database.ActorConfigs
            .Where(item => item.TerritoryType == territory)
            .Select(item => item.SourceNpcPresetId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new MapModificationPreset
        {
            TerritoryId = territory,
            ExportedAtUtc = DateTime.UtcNow,
            ActorConfigs = CloneForPreset(this.database.ActorConfigs.Where(item => item.TerritoryType == territory)),
            Npcs = CloneForPreset(this.database.Npcs.Where(item => item.TerritoryType == territory || actorNpcIds.Contains(item.Id))),
            LocalBgParts = CloneForPreset(this.configuration.SceneEditorLocalBgParts.Where(item => item.TerritoryId == territory)),
            LocalActors = CloneForPreset(this.configuration.SceneEditorLocalActors.Where(item => item.TerritoryId == territory)),
            NativeModifications = CloneForPreset(this.configuration.SceneEditorNativeModifications.Where(item => item.TerritoryId == territory && (item.IsHidden || item.IsModified))),
            LocalLights = CloneForPreset(this.configuration.LocalLights.Where(item => item.TerritoryId == territory)),
            ProtectedSlots = CloneForPreset(this.configuration.ProtectedBgPartSlots.Where(item => item.TerritoryType == territory)),
            ProtectedResourcePaths = CloneForPreset(this.configuration.ProtectedBgPartResourcePaths.Where(item => item.AppliesToCurrentTerritoryOnly && item.TerritoryType == territory)),
            PreferredSlots = CloneForPreset(this.configuration.PreferredModifyBgPartSlots.Where(item => item.TerritoryType == territory)),
            PreferredResourcePaths = CloneForPreset(this.configuration.PreferredModifyBgPartResourcePaths.Where(item => item.AppliesToCurrentTerritoryOnly && item.TerritoryType == territory)),
        };
    }

    private void BeginMapPresetImport(string path)
    {
        try
        {
            var preset = ReadMapPreset(path);
            if (preset.TerritoryId == 0)
            {
                this.mapPresetStatus = T("导入失败：预设没有地图 ID。", "Import failed: preset has no territory id.");
                return;
            }

            var conflicts = this.FindMapPresetConflicts(preset);
            if (conflicts.Count > 0)
            {
                this.pendingImportPreset = preset;
                this.pendingImportPath = path;
                this.pendingImportConflicts = conflicts;
                this.pendingImportConfirmChecked = false;
                this.OpenConfirmPopupAtMouse("ConfirmImportMapPresetConflict");
                return;
            }

            this.ApplyMapPreset(preset);
            this.mapPresetStatus = T("导入成功。为保证稳定，请重新进入该地图或重载插件后加载。", "Import complete. For safety, re-enter the map or reload the plugin before loading it.");
        }
        catch (Exception ex)
        {
            this.mapPresetStatus = T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}");
        }
    }

    private static MapModificationPreset ReadMapPreset(string path)
    {
        var normalized = Path.GetFullPath(path.Trim().Trim('"'));
        var json = File.ReadAllText(normalized);
        return JsonSerializer.Deserialize<MapModificationPreset>(json, MapPresetJsonOptions)
               ?? throw new InvalidOperationException("Map preset JSON is empty.");
    }

    private void DrawMapPresetImportConflictPopup()
    {
        ImGui.SetNextWindowPos(this.confirmationPopupPosition, ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("ConfirmImportMapPresetConflict", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped(T("该预设修改了您已经修改过的内容：", "This preset modifies content you have already changed:"));
        ImGui.BeginChild("MapPresetConflictList", new Vector2(520f, 180f), true);
        for (var i = 0; i < this.pendingImportConflicts.Count; i++)
            ImGui.TextWrapped($"{i + 1}. {this.pendingImportConflicts[i]}");
        ImGui.EndChild();

        ImGui.Checkbox(T("确定导入", "Confirm Import"), ref this.pendingImportConfirmChecked);
        ImGui.BeginDisabled(!this.pendingImportConfirmChecked || this.pendingImportPreset == null);
        if (ImGui.Button(T("Yes", "Yes")))
        {
            try
            {
                this.ApplyMapPreset(this.pendingImportPreset!);
                this.mapPresetStatus = T("导入成功。为保证稳定，请重新进入该地图或重载插件后加载。", "Import complete. For safety, re-enter the map or reload the plugin before loading it.");
            }
            catch (Exception ex)
            {
                this.mapPresetStatus = T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}");
            }
            finally
            {
                this.pendingImportPreset = null;
                this.pendingImportPath = string.Empty;
                this.pendingImportConflicts = [];
                this.pendingImportConfirmChecked = false;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(T("No", "No")))
        {
            this.pendingImportPreset = null;
            this.pendingImportPath = string.Empty;
            this.pendingImportConflicts = [];
            this.pendingImportConfirmChecked = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private List<string> FindMapPresetConflicts(MapModificationPreset preset)
    {
        var territory = preset.TerritoryId;
        var conflicts = new List<string>();
        foreach (var actor in preset.ActorConfigs)
        {
            if (this.database.ActorConfigs.Any(existing =>
                    existing.TerritoryType == territory &&
                    (string.Equals(existing.ConfigId, actor.ConfigId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(existing.RuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase))))
            {
                conflicts.Add(T($"Actor：{FirstNonEmpty(actor.DisplayName, actor.NpcNameSnapshot, actor.ConfigId)}", $"Actor: {FirstNonEmpty(actor.DisplayName, actor.NpcNameSnapshot, actor.ConfigId)}"));
            }
        }

        foreach (var npc in preset.Npcs)
        {
            if (this.database.Npcs.Any(existing => existing.TerritoryType == territory && string.Equals(existing.Id, npc.Id, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add(T($"NPC：{npc.Name}", $"NPC: {npc.Name}"));
        }

        foreach (var bg in preset.LocalBgParts)
        {
            if (this.configuration.SceneEditorLocalBgParts.Any(existing =>
                    existing.TerritoryId == territory &&
                    (string.Equals(existing.InstanceId, bg.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(existing.SourceBgPartStableKey) && string.Equals(existing.SourceBgPartStableKey, bg.SourceBgPartStableKey, StringComparison.OrdinalIgnoreCase)))))
            {
                conflicts.Add(T($"本地场景物体：{FirstNonEmpty(bg.CurrentMdlPath, bg.SourceMdlPath, bg.InstanceId)}", $"Local Scene Object: {FirstNonEmpty(bg.CurrentMdlPath, bg.SourceMdlPath, bg.InstanceId)}"));
            }
        }

        foreach (var native in preset.NativeModifications)
        {
            if (this.configuration.SceneEditorNativeModifications.Any(existing =>
                    existing.TerritoryId == territory &&
                    existing.Kind == native.Kind &&
                    string.Equals(existing.StableKey, native.StableKey, StringComparison.OrdinalIgnoreCase) &&
                    (existing.IsHidden || existing.IsModified)))
            {
                conflicts.Add(T($"原生场景修改：{native.DisplayName}", $"Native Scene Edit: {native.DisplayName}"));
            }
        }

        foreach (var light in preset.LocalLights)
        {
            if (this.configuration.LocalLights.Any(existing =>
                    existing.TerritoryId == territory &&
                    (string.Equals(existing.Id, light.Id, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(existing.Name, light.Name, StringComparison.OrdinalIgnoreCase))))
            {
                conflicts.Add(T($"灯光：{light.Name}", $"Light: {light.Name}"));
            }
        }

        foreach (var slot in preset.ProtectedSlots)
        {
            if (this.configuration.ProtectedBgPartSlots.Any(existing => existing.TerritoryType == territory && string.Equals(existing.StableKey, slot.StableKey, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add(T($"Bgparts 保护：{slot.ResourcePath}", $"BgParts Protection: {slot.ResourcePath}"));
        }
        foreach (var resource in preset.ProtectedResourcePaths)
        {
            if (this.configuration.ProtectedBgPartResourcePaths.Any(existing => existing.TerritoryType == territory && string.Equals(existing.ResourcePath, resource.ResourcePath, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add(T($"Bgparts 保护资源：{resource.ResourcePath}", $"BgParts Protected Resource: {resource.ResourcePath}"));
        }

        foreach (var slot in preset.PreferredSlots)
        {
            if (this.configuration.PreferredModifyBgPartSlots.Any(existing => existing.TerritoryType == territory && string.Equals(existing.StableKey, slot.StableKey, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add(T($"Bgparts 优先改动：{slot.ResourcePath}", $"BgParts Preferred Edit: {slot.ResourcePath}"));
        }
        foreach (var resource in preset.PreferredResourcePaths)
        {
            if (this.configuration.PreferredModifyBgPartResourcePaths.Any(existing => existing.TerritoryType == territory && string.Equals(existing.ResourcePath, resource.ResourcePath, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add(T($"Bgparts 优先资源：{resource.ResourcePath}", $"BgParts Preferred Resource: {resource.ResourcePath}"));
        }

        return conflicts.Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
    }

    private void ApplyMapPreset(MapModificationPreset preset)
    {
        var territory = preset.TerritoryId;
        UpsertByKey(this.database.Npcs, preset.Npcs, item => item.Id);
        UpsertByKey(this.database.ActorConfigs, preset.ActorConfigs, item => FirstNonEmpty(item.ConfigId, item.RuntimeId));
        UpsertByKey(this.configuration.SceneEditorLocalActors, preset.LocalActors, item => FirstNonEmpty(item.RecordId, item.RuntimeId));
        UpsertByKey(this.configuration.SceneEditorLocalBgParts, preset.LocalBgParts, MapBgPartRecordKey);
        UpsertNativeRecords(preset.NativeModifications);
        UpsertByKey(this.configuration.LocalLights, preset.LocalLights, item => item.Id);
        UpsertByKey(this.configuration.ProtectedBgPartSlots, preset.ProtectedSlots, item => $"{item.TerritoryType}:{FirstNonEmpty(item.StableKey, item.LayoutInstanceAddress, item.ResourcePath)}");
        UpsertByKey(this.configuration.ProtectedBgPartResourcePaths, preset.ProtectedResourcePaths, item => $"{item.TerritoryType}:{item.AppliesToCurrentTerritoryOnly}:{item.ResourcePath}");
        UpsertByKey(this.configuration.PreferredModifyBgPartSlots, preset.PreferredSlots, item => $"{item.TerritoryType}:{FirstNonEmpty(item.StableKey, item.LayoutInstanceAddress, item.ResourcePath)}");
        UpsertByKey(this.configuration.PreferredModifyBgPartResourcePaths, preset.PreferredResourcePaths, item => $"{item.TerritoryType}:{item.AppliesToCurrentTerritoryOnly}:{item.ResourcePath}");

        foreach (var actor in preset.ActorConfigs.Where(item => item.TerritoryType == 0))
            actor.TerritoryType = (ushort)Math.Clamp((int)territory, 0, ushort.MaxValue);
        foreach (var light in preset.LocalLights.Where(item => item.TerritoryId == 0))
            light.TerritoryId = territory;

        this.database.Save();
        this.saveConfiguration();
    }

    private void UpsertNativeRecords(IReadOnlyList<SceneEditorNativeModificationRecord> imported)
    {
        foreach (var item in imported)
        {
            var index = this.configuration.SceneEditorNativeModifications.FindIndex(existing =>
                existing.TerritoryId == item.TerritoryId &&
                existing.Kind == item.Kind &&
                string.Equals(existing.StableKey, item.StableKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                this.configuration.SceneEditorNativeModifications[index] = item;
            else
                this.configuration.SceneEditorNativeModifications.Add(item);
        }
    }

    private static void UpsertByKey<T>(List<T> target, IEnumerable<T> imported, Func<T, string> keySelector)
    {
        foreach (var item in imported)
        {
            var key = keySelector(item);
            var index = target.FindIndex(existing => string.Equals(keySelector(existing), key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                target[index] = item;
            else
                target.Add(item);
        }
    }

    private static List<T> CloneForPreset<T>(IEnumerable<T> values)
        => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(values.ToList(), MapPresetJsonOptions), MapPresetJsonOptions) ?? [];

    private static string MapBgPartRecordKey(SceneEditorLocalBgPartRecord record)
        => !string.IsNullOrWhiteSpace(record.SourceBgPartStableKey)
            ? $"{record.TerritoryId}:{record.SourceBgPartStableKey}"
            : record.InstanceId;

    private void DrawRuntimeDebug()
    {
        ImGui.TextWrapped($"配置版本：{this.configuration.Version}");
        var showUiInGpose = this.configuration.ShowPluginUiInGpose;
        if (ImGui.Checkbox("GPose 中显示插件窗口", ref showUiInGpose))
        {
            this.configuration.ShowPluginUiInGpose = showUiInGpose;
            this.saveConfiguration();
        }
        ImGui.TextWrapped($"GPose 状态：{(this.IsInGpose() ? "当前在 GPose；Actor 运行态操作可用" : "普通状态")}");
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

    private bool IsInGpose()
    {
        try
        {
            return this.isGposing();
        }
        catch
        {
            return false;
        }
    }

    private bool BeginDisabledInGpose(string actionName)
    {
        var disabled = this.IsInGpose();
        ImGui.BeginDisabled(disabled);
        if (disabled && ImGui.IsItemHovered())
            ImGui.SetTooltip($"GPose 中当前仅开放只读诊断；请退出 GPose 后执行：{actionName}");
        return disabled;
    }

    private void DrawGposeBlockedMessage(string actionName)
        => ImGui.TextDisabled(T(
            $"GPose 中仍限制：{actionName}。请退出 GPose 后执行该操作。",
            $"{actionName} is still restricted in GPose. Leave GPose before using it."));

    private void SelectSceneEditableFromMainUi(SceneEditableKind kind, string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return;

        this.sceneEditorSelection.Select(kind, runtimeId, SceneEditorSelectionSource.MainUi);
    }

    private void SyncMainUiSelectionFromSceneEditor()
    {
        if (!this.sceneEditorSelection.HasSelection || this.sceneEditorSelection.SelectedKind == null)
            return;

        switch (this.sceneEditorSelection.SelectedKind.Value)
        {
            case SceneEditableKind.LocalActor:
                if (!string.Equals(this.selectedActorRuntimeId, this.sceneEditorSelection.SelectedRuntimeId, StringComparison.Ordinal))
                {
                    this.selectedActorRuntimeId = this.sceneEditorSelection.SelectedRuntimeId;
                    var actor = this.realNpcSpawn.Actors.FirstOrDefault(item => string.Equals(item.RuntimeId, this.selectedActorRuntimeId, StringComparison.Ordinal));
                    if (actor != null)
                        this.selectedNpcId = actor.NpcId;
                }
                break;
            case SceneEditableKind.LocalBgPart:
                if (!string.Equals(this.selectedLocalLayoutObjectId, this.sceneEditorSelection.SelectedRuntimeId, StringComparison.Ordinal))
                    this.selectedLocalLayoutObjectId = this.sceneEditorSelection.SelectedRuntimeId;
                break;
            case SceneEditableKind.LocalLight:
                if (!string.Equals(this.selectedLocalLightId, this.sceneEditorSelection.SelectedRuntimeId, StringComparison.Ordinal))
                    this.selectedLocalLightId = this.sceneEditorSelection.SelectedRuntimeId;
                break;
            case SceneEditableKind.NativeBgPart:
                var selected = this.sceneEditor.GetSelectedEditable();
                if (selected?.LayoutProbe != null &&
                    !string.Equals(this.selectedBgPartAddress, selected.LayoutProbe.Address, StringComparison.OrdinalIgnoreCase))
                {
                    this.selectedBgPartAddress = selected.LayoutProbe.Address;
                    this.layerDump.SelectReusableCandidate(selected.LayoutProbe);
                }
                break;
        }
    }

    private void DrawSceneEditor()
    {
        this.SyncSceneEditorBgPartCopyMode();

        var io = ImGui.GetIO();
        this.sceneEditor.TryHandleUndoShortcut(
            this.sceneEditor.Gizmo.InputState.State == SceneEditorInputInteractionState.GizmoDragging,
            io.WantTextInput,
            io.KeyCtrl,
            ImGui.IsKeyPressed(ImGuiKey.Z));

        var overlayEnabled = this.sceneEditor.OverlayEnabled;
        if (ImGui.Checkbox(T("启用", "Enabled"), ref overlayEnabled))
            this.sceneEditor.OverlayEnabled = overlayEnabled;

        var showPluginObjects = this.sceneEditor.ShowPluginObjects;
        if (ImGui.Checkbox(T("插件对象", "Plugin Objects"), ref showPluginObjects))
            this.sceneEditor.ShowPluginObjects = showPluginObjects;
        ImGui.SameLine();
        var showNativeObjects = this.sceneEditor.ShowNativeObjects;
        if (ImGui.Checkbox(T("原生对象", "Native Objects"), ref showNativeObjects))
            this.sceneEditor.ShowNativeObjects = showNativeObjects;

        var showActors = this.sceneEditor.ShowActors;
        if (ImGui.Checkbox("Actor", ref showActors))
            this.sceneEditor.ShowActors = showActors;
        ImGui.SameLine();
        var showBgParts = this.sceneEditor.ShowBgParts;
        if (ImGui.Checkbox("BgPart", ref showBgParts))
            this.sceneEditor.ShowBgParts = showBgParts;
        ImGui.SameLine();
        var showLights = this.sceneEditor.ShowLights;
        if (ImGui.Checkbox(T("灯光", "Light"), ref showLights))
            this.sceneEditor.ShowLights = showLights;

        var markerSize = this.sceneEditor.MarkerRadius;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.SliderFloat(T("标记大小", "Marker Size"), ref markerSize, 4f, 9f))
            this.sceneEditor.MarkerRadius = markerSize;

        if (ImGui.CollapsingHeader(T("Gizmo 设置", "Gizmo Settings"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped(T($"当前 Gizmo：{DisplaySceneEditorGizmoMode(this.sceneEditor.GizmoMode)}", $"Current Gizmo: {DisplaySceneEditorGizmoMode(this.sceneEditor.GizmoMode)}"));
            var moveSensitivity = this.sceneEditor.Gizmo.MoveSensitivity;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("位移灵敏度", "Move Sensitivity"), ref moveSensitivity))
                this.sceneEditor.Gizmo.MoveSensitivity = MathF.Max(0.01f, moveSensitivity);

            var rotateSensitivity = this.sceneEditor.Gizmo.RotateSensitivityDegreesPerPixel;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("旋转灵敏度", "Rotate Sensitivity"), ref rotateSensitivity))
                this.sceneEditor.Gizmo.RotateSensitivityDegreesPerPixel = MathF.Max(0.01f, rotateSensitivity);

            var scaleSensitivity = this.sceneEditor.Gizmo.ScaleSensitivity;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("缩放灵敏度", "Scale Sensitivity"), ref scaleSensitivity))
                this.sceneEditor.Gizmo.ScaleSensitivity = MathF.Max(0.001f, scaleSensitivity);

            var snapEnabled = this.sceneEditor.Gizmo.SnapEnabled;
            if (ImGui.Checkbox(T("启用吸附", "Snap Enabled"), ref snapEnabled))
                this.sceneEditor.Gizmo.SnapEnabled = snapEnabled;

            var moveSnap = this.sceneEditor.Gizmo.MoveSnapStep;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("位移吸附步长", "Move Snap Step"), ref moveSnap))
                this.sceneEditor.Gizmo.MoveSnapStep = MathF.Max(0.001f, moveSnap);

            var rotateSnap = this.sceneEditor.Gizmo.RotateSnapDegrees;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("旋转吸附角度", "Rotate Snap Degrees"), ref rotateSnap))
                this.sceneEditor.Gizmo.RotateSnapDegrees = MathF.Max(0.1f, rotateSnap);

            var scaleSnap = this.sceneEditor.Gizmo.ScaleSnapStep;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("缩放吸附步长", "Scale Snap Step"), ref scaleSnap))
                this.sceneEditor.Gizmo.ScaleSnapStep = MathF.Max(0.001f, scaleSnap);

            ImGui.TextDisabled(T("Shift = 微调，Ctrl = 拖动时吸附。", "Shift = fine tune, Ctrl = snap while dragging."));
        }

        var selected = this.sceneEditor.GetSelectedEditable();
        ImGui.Separator();
        if (selected == null)
            return;

        this.DrawSceneEditorModeButtons(selected);
        this.DrawSceneEditorUndoControls();
        this.DrawSceneEditorBgPartQuickActions(selected);
        this.DrawSceneEditorTransformPanel(selected);
        if (ImGui.Button(T("清空选择", "Clear Selection")))
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.SceneEditorPanel);

    }

    private void SyncSceneEditorBgPartCopyMode()
    {
        this.localLayoutFullCollisionMode = this.sceneEditor.BgPartCollisionModeEnabled;
        this.confirmFullLayoutCollisionMode = this.sceneEditor.BgPartCollisionModeConfirmed;
        this.sceneEditor.SetBgPartCollisionMode(
            this.localLayoutFullCollisionMode,
            this.confirmFullLayoutCollisionMode,
            this.realNpcSpawn.EnableUnsafeNativeWrites);
    }

    private void SetSceneEditorBgPartCollisionMode(bool enabled, bool confirmed)
    {
        this.localLayoutFullCollisionMode = enabled;
        this.confirmFullLayoutCollisionMode = enabled && confirmed;
        this.sceneEditor.SetBgPartCollisionMode(
            this.localLayoutFullCollisionMode,
            this.confirmFullLayoutCollisionMode,
            this.realNpcSpawn.EnableUnsafeNativeWrites);
    }

    private void DrawSceneEditorBgPartCollisionControls(string id)
    {
        var collisionMode = this.sceneEditor.BgPartCollisionModeEnabled;
        if (ImGui.Checkbox($"{T("模型和碰撞体一起变化", "Move Collision With Model")}##{id}", ref collisionMode))
            this.SetSceneEditorBgPartCollisionMode(collisionMode, collisionMode && this.sceneEditor.BgPartCollisionModeConfirmed);

        if (!this.sceneEditor.BgPartCollisionModeEnabled)
        {
            ImGui.TextDisabled(T("只移动视觉模型，不移动碰撞体。", "Visual only: collision stays in place."));
            return;
        }

        if (this.sceneEditor.BgPartCollisionModeConfirmed)
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.25f, 1f), T("已确认：模型和碰撞体会一起变化。", "Confirmed: model and collision move together."));
            ImGui.SameLine();
            if (ImGui.SmallButton($"{T("撤销确认", "Revoke")}##{id}Revoke"))
                this.SetSceneEditorBgPartCollisionMode(true, false);
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), T("已开启碰撞体编辑，但还需要二次确认。", "Collision editing is enabled and needs confirmation."));
        ImGui.BeginDisabled(!this.realNpcSpawn.EnableUnsafeNativeWrites);
        if (ImGui.Button($"{T("确认启用碰撞编辑", "Confirm Collision Editing")}##{id}Confirm"))
            this.SetSceneEditorBgPartCollisionMode(true, true);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered() && !this.realNpcSpawn.EnableUnsafeNativeWrites)
            ImGui.SetTooltip(T("需要 Native 写入。", "Native writes are required."));
    }

    private void DrawSceneEditorHiddenList()
    {
        this.SyncSceneEditorBgPartCopyMode();
        var territory = this.runtime.TerritoryType;
        var records = this.sceneEditor.NativeModificationRecords
            .Where(item => item.TerritoryId == territory)
            .ToList();

        var selected = this.sceneEditor.GetSelectedEditable();
        var selectedRecord = selected == null ? null : this.sceneEditor.GetNativeModificationRecord(selected);
        ImGui.BeginDisabled(selectedRecord == null);
        if (ImGui.Button(T("恢复选中", "Restore Selected")))
        {
            this.pendingNativeRestoreRecordId = selectedRecord!.RecordId;
            this.OpenConfirmPopupAtMouse("ConfirmRestoreNativeRecord");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(T("恢复当前地图全部隐藏对象", "Restore Current Map Hidden Objects")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreCurrentMapHidden");
        ImGui.SameLine();
        if (ImGui.Button(T("恢复当前地图全部原生修改", "Restore Current Map Native Edits")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreCurrentMapNativeEdits");
        ImGui.SameLine();
        if (ImGui.Button(T("恢复当前地图 NPC / EventNPC 修改", "Restore Current Map NPC / EventNPC Edits")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreCurrentMapNativeActors");
        if (ImGui.Button(T("恢复当前地图 BgPart 修改", "Restore Current Map BgPart Edits")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreCurrentMapBgParts");
        ImGui.SameLine();
        if (ImGui.Button(T("恢复当前地图 Light 修改", "Restore Current Map Light Edits")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreCurrentMapNativeLights");

        ImGui.Separator();
        this.DrawSceneEditorRecordSection(T("隐藏 BgPart", "Hidden BgParts"), records.Where(item => item.IsHidden && item.Kind == SceneEditableKind.NativeBgPart));
        this.DrawSceneEditorRecordSection(T("隐藏 Light", "Hidden Lights"), records.Where(item => item.IsHidden && item.Kind == SceneEditableKind.NativeLight));
        this.DrawSceneEditorRecordSection(T("隐藏 NPC / EventNPC", "Hidden NPC / EventNPC"), records.Where(item => item.IsHidden && item.Kind is SceneEditableKind.NativeActor or SceneEditableKind.EventNpc));
        ImGui.Separator();
        ImGui.TextWrapped(T("原生场景修改", "Native Scene Edits"));
        this.DrawSceneEditorRecordSection(T("原生 NPC / EventNPC 修改", "Native NPC / EventNPC Edits"), records.Where(item => item.IsModified && item.Kind is SceneEditableKind.NativeActor or SceneEditableKind.EventNpc));
        this.DrawSceneEditorRecordSection(T("原生 BgPart 修改", "Native BgPart Edits"), records.Where(item => item.IsModified && item.Kind == SceneEditableKind.NativeBgPart));
        this.DrawSceneEditorRecordSection(T("原生 Light 修改", "Native Light Edits"), records.Where(item => item.IsModified && item.Kind == SceneEditableKind.NativeLight));

        if (this.DrawConfirmPopup("ConfirmRestoreNativeRecord", T("确认恢复这条原生修改？", "Restore this native edit?")))
        {
            if (!string.IsNullOrWhiteSpace(this.pendingNativeRestoreRecordId))
                this.sceneEditor.RestoreNativeModification(this.pendingNativeRestoreRecordId);
            this.pendingNativeRestoreRecordId = string.Empty;
        }
        if (this.DrawConfirmPopup("ConfirmRestoreCurrentMapHidden", T("确认恢复当前地图全部隐藏对象？", "Restore all hidden objects on the current map?")))
            this.sceneEditor.RestoreCurrentTerritoryHiddenObjects();
        if (this.DrawConfirmPopup("ConfirmRestoreCurrentMapNativeEdits", T("确认恢复当前地图全部原生修改？", "Restore all native edits on the current map?")))
            this.sceneEditor.RestoreCurrentTerritoryNativeModifications();
        if (this.DrawConfirmPopup("ConfirmRestoreCurrentMapNativeActors", T("确认恢复当前地图 NPC / EventNPC 修改？", "Restore current map NPC / EventNPC edits?")))
            this.sceneEditor.RestoreCurrentTerritoryNativeActors();
        if (this.DrawConfirmPopup("ConfirmRestoreCurrentMapBgParts", T("确认恢复当前地图 BgPart 修改？", "Restore current map BgPart edits?")))
            this.sceneEditor.RestoreCurrentTerritoryNativeBgParts();
        if (this.DrawConfirmPopup("ConfirmRestoreCurrentMapNativeLights", T("确认恢复当前地图 Light 修改？", "Restore current map light edits?")))
            this.sceneEditor.RestoreCurrentTerritoryNativeLights();
    }

    private void RestoreSceneEditorRecords(IEnumerable<SceneEditorNativeModificationRecord> records)
    {
        foreach (var record in records.Select(item => item.RecordId).ToList())
            this.sceneEditor.RestoreNativeModification(record);
    }

    private void DrawSceneEditorRecordSection(string title, IEnumerable<SceneEditorNativeModificationRecord> source)
    {
        var rows = source.ToList();
        if (!ImGui.CollapsingHeader($"{title} ({rows.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(T("没有记录。", "No records."));
            return;
        }

        if (!ImGui.BeginTable($"SceneEditorRecords-{title}", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 240f)))
            return;

        ImGui.TableSetupColumn(T("类型", "Kind"), ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableSetupColumn(T("名称", "Name"));
        ImGui.TableSetupColumn("mdl");
        ImGui.TableSetupColumn(T("原始", "Original"));
        ImGui.TableSetupColumn(T("当前", "Current"));
        ImGui.TableSetupColumn(T("操作", "Actions"), ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableHeadersRow();

        var restoreRequested = false;
        foreach (var record in rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID(record.RecordId);
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(DisplaySceneEditableKind(record.Kind));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(record.DisplayName);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(record.MdlPath) ? "unknown" : record.MdlPath);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped($"P {FormatVector(ToVector3(record.OriginalPosition))}\nR {FormatVector(RadiansVectorToDegrees(ToVector3(record.OriginalRotationEuler)))}\nS {FormatVector(ToVector3(record.OriginalScale))}");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped($"P {FormatVector(ToVector3(record.CurrentPosition))}\nR {FormatVector(RadiansVectorToDegrees(ToVector3(record.CurrentRotationEuler)))}\nS {FormatVector(ToVector3(record.CurrentScale))}");
            ImGui.TableSetColumnIndex(5);
            if (ImGui.Button(T("恢复", "Restore")))
            {
                this.pendingNativeRestoreRecordId = record.RecordId;
                restoreRequested = true;
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (restoreRequested)
            this.OpenConfirmPopupAtMouse("ConfirmRestoreNativeRecord");
    }

    private void DrawSceneEditorModeButtons(SceneEditableRef selected)
    {
        if (!selected.TransformEditable)
        {
            ImGui.TextDisabled(T("当前对象不可编辑 Transform。", "This object cannot edit transform."));
            return;
        }

        ImGui.TextUnformatted(T("Gizmo：", "Gizmo:"));
        this.DrawSceneEditorModeButton(T("选择", "Select"), SceneEditorGizmoMode.Select);
        ImGui.SameLine();
        this.DrawSceneEditorModeButton(T("位移", "Move"), SceneEditorGizmoMode.Move);
        ImGui.SameLine();
        this.DrawSceneEditorModeButton(T("旋转", "Rotate"), SceneEditorGizmoMode.Rotate);
        ImGui.SameLine();
        this.DrawSceneEditorModeButton(T("缩放", "Scale"), SceneEditorGizmoMode.Scale);
    }

    private void DrawSceneEditorModeButton(string label, SceneEditorGizmoMode mode)
    {
        var active = this.sceneEditor.GizmoMode == mode;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.95f, 0.55f, 0.18f, 1f));

        if (ImGui.Button(label))
            this.sceneEditor.SetGizmoMode(mode);

        if (active)
            ImGui.PopStyleColor();
    }

    private void DrawSceneEditorUndoControls()
    {
        var entry = this.sceneEditor.Undo.Peek;
        ImGui.BeginDisabled(entry == null);
        if (ImGui.Button(T("撤销上次 Transform (Ctrl+Z)", "Undo Last Transform (Ctrl+Z)")))
            this.sceneEditor.TryUndoLast();
        ImGui.EndDisabled();
    }

    private void DrawSceneEditorBgPartQuickActions(SceneEditableRef selected)
    {
        if (selected.Kind is not (SceneEditableKind.LocalBgPart or SceneEditableKind.NativeBgPart))
            return;

        this.SyncSceneEditorBgPartCopyMode();
        ImGui.Separator();
        ImGui.TextWrapped(T("BgPart 快捷操作", "BgPart Quick Actions"));
        this.DrawSceneEditorBgPartCollisionControls("SceneEditorBgPartCollision");
        if (!string.IsNullOrWhiteSpace(selected.MdlPath))
        {
            ImGui.TextWrapped($"mdl: {selected.MdlPath}");
            ImGui.SameLine();
            if (ImGui.Button(T("复制 mdl##SceneEditorBgPartMdl", "Copy MDL##SceneEditorBgPartMdl")))
                ImGui.SetClipboardText(selected.MdlPath);
        }

        var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive || !this.realNpcSpawn.EnableUnsafeNativeWrites || fullLayoutBlocked);
        if (ImGui.Button(T("创建复制体##SceneEditorCopyOneBgPart", "Create Copy##SceneEditorCopyOneBgPart")))
            this.TryCreateSingleBgPartCopy(selected, "SceneEditorPanel");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered() && (!this.realNpcSpawn.EnableUnsafeNativeWrites || fullLayoutBlocked))
            ImGui.SetTooltip(fullLayoutBlocked ? T("需要先确认碰撞体编辑。", "Collision editing requires confirmation.") : T("需要 Native 写入。", "Native writes are required."));

        ImGui.SameLine();
        if (ImGui.Button(T("设为候选##SceneEditorBgPartCandidate", "Set Candidate##SceneEditorBgPartCandidate")))
            this.sceneEditor.TryMarkBgPartCandidate(selected);
        ImGui.SameLine();
        if (ImGui.Button(T("优先##SceneEditorBgPartPreferred", "Preferred##SceneEditorBgPartPreferred")))
            this.sceneEditor.TryPreferBgPart(selected);
        ImGui.SameLine();
        if (ImGui.Button(T("保护##SceneEditorBgPartProtect", "Protect##SceneEditorBgPartProtect")))
            this.sceneEditor.TryProtectBgPart(selected);
    }

    private void DrawSceneEditorTransformPanel(SceneEditableRef selected)
    {
        if (selected.IsNativeGameObject)
            this.DrawSceneEditorNativePanel(selected);

        if (!selected.TransformEditable)
        {
            return;
        }

        this.sceneEditorTransformEdit.Bind(selected, this.sceneEditorSelection.Generation + this.sceneEditor.TransformGeneration);

        var bgPartNeedsCollisionConfirmation = this.sceneEditor.IsBgPartCollisionConfirmationRequired(selected.Kind);
        var disabled = !selected.IsValid || this.IsInGpose() || bgPartNeedsCollisionConfirmation;
        if (disabled)
        {
            if (bgPartNeedsCollisionConfirmation)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), T("碰撞体编辑未确认，Transform 已阻止。", "Collision editing is not confirmed; transform is blocked."));
            else
                ImGui.TextDisabled(selected.IsValid ? T("GPose 中禁止写 Transform。", "Transform writes are disabled in GPose.") : T("对象当前无效。", "Object is currently invalid."));
        }

        ImGui.BeginDisabled(disabled);
        var changed = false;
        var position = this.sceneEditorTransformEdit.PositionInput;
        if (DrawVector3StepperRow(T("位移", "Position"), "SceneEditorPosition", ref position, 0.2f))
        {
            this.sceneEditorTransformEdit.PositionInput = position;
            changed = true;
        }

        var eulerDegrees = RadiansVectorToDegrees(this.sceneEditorTransformEdit.EulerInput);
        if (DrawVector3StepperRow(T("旋转", "Rotation"), "SceneEditorRotation", ref eulerDegrees, 0.2f))
        {
            this.sceneEditorTransformEdit.EulerInput = DegreesVectorToRadians(eulerDegrees);
            changed = true;
        }

        var scale = this.sceneEditorTransformEdit.ScaleInput;
        if (DrawVector3StepperRow(T("缩放", "Scale"), "SceneEditorScale", ref scale, 0.2f, 0.01f))
        {
            this.sceneEditorTransformEdit.ScaleInput = Vector3.Max(scale, new Vector3(0.01f));
            changed = true;
        }

        if (changed)
        {
            var before = selected.Transform;
            var after = this.sceneEditorTransformEdit.ToWorldTransform();
            if (this.sceneEditor.ApplyWorldTransform(selected.Kind, selected.RuntimeId, after))
                this.sceneEditor.PushTransformUndo(selected.Kind, selected.RuntimeId, selected.DisplayName, before, after, "TransformInput");
        }
        ImGui.EndDisabled();
    }

    private void DrawSceneEditorNativePanel(SceneEditableRef selected)
    {
        ImGui.Separator();
        DrawYellowSectionLabel(selected.IsPluginCreated ? "插件对象" : "原生对象", selected.IsPluginCreated ? "Plugin Object" : "Native Object");
        ImGui.TextWrapped(T($"类型：{DisplaySceneEditableKind(selected.Kind)}", $"Kind: {DisplaySceneEditableKind(selected.Kind)}"));
        ImGui.TextWrapped(T($"名称：{selected.DisplayName}", $"Name: {selected.DisplayName}"));
        if (!string.IsNullOrWhiteSpace(selected.MdlPath))
            ImGui.TextWrapped($"mdl: {selected.MdlPath}");

        var nativeRecord = this.sceneEditor.GetNativeModificationRecord(selected);
        if (selected.IsNativeGameObject && selected.Kind != SceneEditableKind.Player)
        {
            if (nativeRecord?.IsHidden == true)
            {
                if (ImGui.Button(T("恢复此原生对象", "Restore This Native Object")))
                    this.sceneEditor.RestoreNativeModification(nativeRecord.RecordId);
            }
            else
            {
                ImGui.BeginDisabled(!this.sceneEditor.AllowNativeTransformWrites);
                if (ImGui.Button(T("隐藏此原生对象", "Hide This Native Object")))
                    this.sceneEditor.HideNativeObject(selected);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered() && !this.sceneEditor.AllowNativeTransformWrites)
                    ImGui.SetTooltip(T("需要 Native 写入。", "Native writes are required."));
            }
        }

        switch (selected.Kind)
        {
            case SceneEditableKind.NativeBgPart:
                if (ImGui.Button(T("复制 mdl path", "Copy MDL Path")))
                    ImGui.SetClipboardText(selected.MdlPath);
                break;
            case SceneEditableKind.Player:
            case SceneEditableKind.NativeActor:
            case SceneEditableKind.EventNpc:
                ImGui.TextWrapped(selected.Kind == SceneEditableKind.Player
                    ? T("当前目标是玩家，Transform 编辑始终禁用。", "Selected target is LocalPlayer. Transform editing is always disabled.")
                    : selected.Kind == SceneEditableKind.EventNpc
                        ? T("当前目标是可交互 NPC。隐藏或移动后可从原生场景修改页恢复。", "Selected target is an interactable NPC. Restore it from Native Scene Edits after hiding or moving it.")
                    : selected.TransformEditable
                        ? T("当前目标是原生 NPC / Actor，Transform 编辑已启用。", "Selected target is a native NPC/actor. Transform editing is enabled.")
                        : T("当前目标是原生 NPC / Actor，需要 Native 写入才能移动。", "Selected target is a native NPC/actor. Native writes are required to move it."));
                this.DrawNativeActorGlamourerSavePanel(selected);
                break;
            case SceneEditableKind.NativeLight:
                ImGui.TextWrapped(T("原生灯光当前只读。", "Native Light is read-only."));
                if (!string.IsNullOrWhiteSpace(selected.LightInfo))
                    ImGui.TextWrapped(selected.LightInfo);
                break;
        }
    }

    private void DrawNativeActorGlamourerSavePanel(SceneEditableRef selected)
    {
        ImGui.Separator();
        ImGui.TextWrapped("Glamourer Design");
        ImGui.InputText(T("Design 名称", "Design Name"), ref this.sceneEditorGlamourerDesignName, 128);
        if (ImGui.Button(T("保存 Glamourer Design", "Save Glamourer Design")))
        {
            this.sceneEditorGlamourerDesignStatus =
                T("当前版本没有启用安全的 Glamourer 保存接口；目标未改变。", "Glamourer save-design IPC is not safely bound in this build; target was left unchanged.");
        }

        ImGui.TextDisabled(this.sceneEditorGlamourerDesignStatus);
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
        EditString("notes", appearance.Notes, 512, value => appearance.Notes = value);
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
        this.RefreshActorRuntimeSnapshotForUi();
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
        var inGpose = this.IsInGpose();
        if (ImGui.Button(T("刷新有效性", "Refresh State")))
            this.realNpcSpawn.RequestActorRebuild("manual UI refresh");
        ImGui.SameLine();
        ImGui.BeginDisabled(inGpose);
        if (ImGui.Button(T("删除全部 Actor", "Delete All Actors")))
        {
            this.deleteAllActorsConfirmPopupPosition = ImGui.GetMousePos();
            ImGui.OpenPopup("ConfirmDeleteAllActors");
        }
        ImGui.EndDisabled();
        this.DrawDeleteAllActorsConfirmationPopup();

        if (inGpose)
            this.DrawGposeBlockedMessage("Actor 创建 / 删除");
        else
            this.DrawActorSourceCreationPanel();

        ImGui.TextWrapped(T($"Actor 数量：{this.cachedActorRuntimeSnapshot.Count}", $"Actors: {this.cachedActorRuntimeSnapshot.Count}"));

        if (!ImGui.BeginTable("RuntimeActors", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 320f)))
            return;
        ImGui.TableSetupColumn(T("选择", "Select"));
        ImGui.TableSetupColumn(T("类型", "Kind"));
        ImGui.TableSetupColumn(T("名称", "Name"));
        ImGui.TableSetupColumn(T("序号", "Index"));
        ImGui.TableSetupColumn(T("位置", "Position"));
        ImGui.TableHeadersRow();
        foreach (var actor in this.cachedActorRuntimeSnapshot)
        {
            ImGui.TableNextRow();
            ImGui.PushID(actor.RuntimeId);
            ImGui.TableSetColumnIndex(0);
            var selected = string.Equals(this.selectedActorRuntimeId, actor.RuntimeId, StringComparison.Ordinal);
            if (ImGui.RadioButton($"##SelectActor{actor.RuntimeId}", selected))
            {
                this.selectedActorRuntimeId = actor.RuntimeId;
                this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalActor, actor.RuntimeId);
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(DisplayActorSpawnKind(actor.SpawnKind));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(actor.NpcName);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(actor.ObjectIndex);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(FormatVector(actor.LastKnownPosition));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawDeleteAllActorsConfirmationPopup()
    {
        ImGui.SetNextWindowPos(this.deleteAllActorsConfirmPopupPosition, ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("ConfirmDeleteAllActors"))
            return;

        ImGui.TextWrapped(T("确认删除全部 Actor？", "Delete all actors?"));
        if (ImGui.Button(T("确认", "Confirm")))
        {
            this.realNpcSpawn.DespawnAll(deleteConfigs: true);
            this.selectedActorRuntimeId = string.Empty;
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(T("取消", "Cancel")))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void OpenConfirmPopupAtMouse(string popupId)
    {
        this.confirmationPopupPosition = ImGui.GetMousePos();
        ImGui.OpenPopup(popupId);
    }

    private bool DrawConfirmPopup(string popupId, string message)
    {
        var confirmed = false;
        ImGui.SetNextWindowPos(this.confirmationPopupPosition, ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(popupId))
            return false;

        ImGui.TextWrapped(message);
        if (ImGui.Button(T("确认", "Confirm")))
        {
            confirmed = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(T("取消", "Cancel")))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
        return confirmed;
    }

    private void DrawActorSourceCreationPanel()
    {
        this.RefreshActorSourceSearchCacheForUi();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Glamourer Design");
        if (ImGui.Button(T("重新扫描 Glamourer 设计", "Rescan Glamourer Designs")))
        {
            this.glamourerDesignCatalog.Scan();
            this.cachedGlamourerSearchText = "\0";
            this.nextActorSourceSearchRefreshAt = DateTime.MinValue;
        }
        ImGui.InputText(T("搜索 Design", "Search Designs"), ref this.glamourerSearchText, 128);

        var designResults = this.cachedGlamourerDesignResults
            .Where(IsRecognizedGlamourerDesign)
            .ToList();
        if (ImGui.BeginTable("ActorGlamourerDesignSources", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 170f)))
        {
            ImGui.TableSetupColumn(T("名称", "Name"));
            ImGui.TableSetupColumn(T("操作", "Actions"));
            ImGui.TableHeadersRow();
            foreach (var design in designResults)
            {
                ImGui.TableNextRow();
                ImGui.PushID($"design-{design.Identifier}-{design.FilePath}");
                ImGui.TableSetColumnIndex(0);
                ImGui.TextWrapped(design.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.BeginDisabled(!this.realNpcSpawn.CanSpawnRealActor);
                if (ImGui.Button(T("生成 Actor", "Spawn Actor")))
                    this.SelectCreatedActor(this.realNpcSpawn.SpawnFromGlamourerDesign(design));
                ImGui.EndDisabled();
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Glamourer NPC");
        if (ImGui.Button(T("刷新 NPC 目录", "Refresh NPC Catalog")))
        {
            this.gameNpcCatalog.ReloadCatalog();
            this.cachedGameNpcSearchText = "\0";
            this.nextActorSourceSearchRefreshAt = DateTime.MinValue;
        }
        ImGui.InputText(T("搜索 NPC", "Search NPCs"), ref this.gameNpcSearchText, 128);

        var npcResults = this.cachedGameNpcResults;
        if (ImGui.BeginTable("ActorGlamourerNpcSources", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 190f)))
        {
            ImGui.TableSetupColumn(T("名称", "Name"));
            ImGui.TableSetupColumn(T("类型", "Kind"));
            ImGui.TableSetupColumn(T("行 ID", "Row ID"));
            ImGui.TableSetupColumn(T("模型", "Model"));
            ImGui.TableSetupColumn(T("操作", "Actions"));
            ImGui.TableHeadersRow();
            foreach (var entry in npcResults)
            {
                ImGui.TableNextRow();
                ImGui.PushID($"gnpc-{entry.Kind}-{entry.RowId}-{entry.ModelCharaId}");
                ImGui.TableSetColumnIndex(0);
                ImGui.TextWrapped(entry.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(DisplayGameNpcCatalogKind(entry.Kind));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(entry.RowId.ToString());
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(entry.ModelCharaId.ToString());
                ImGui.TableSetColumnIndex(4);
                ImGui.BeginDisabled(!this.realNpcSpawn.CanSpawnRealActor);
                if (ImGui.Button(T("生成 Actor", "Spawn Actor")))
                    this.SelectCreatedActor(this.realNpcSpawn.SpawnFromGlamourerNpc(entry));
                ImGui.EndDisabled();
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void SelectCreatedActor(RuntimeActorInstance? actor)
    {
        if (actor == null)
            return;

        this.cachedActorRuntimeSnapshot = this.realNpcSpawn.Actors;
        this.nextActorRuntimeSnapshotRefreshAt = DateTime.UtcNow.AddMilliseconds(250);
        this.selectedActorRuntimeId = actor.RuntimeId;
        this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalActor, actor.RuntimeId);
    }

    private void RefreshActorRuntimeSnapshotForUi()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextActorRuntimeSnapshotRefreshAt)
            return;

        this.cachedActorRuntimeSnapshot = this.realNpcSpawn.Actors;
        this.nextActorRuntimeSnapshotRefreshAt = now.AddMilliseconds(250);
    }

    private void RefreshActorSourceSearchCacheForUi()
    {
        var now = DateTime.UtcNow;
        var searchChanged =
            !string.Equals(this.cachedGlamourerSearchText, this.glamourerSearchText, StringComparison.Ordinal) ||
            !string.Equals(this.cachedGameNpcSearchText, this.gameNpcSearchText, StringComparison.Ordinal);
        if (!searchChanged && now < this.nextActorSourceSearchRefreshAt)
            return;

        this.cachedGlamourerSearchText = this.glamourerSearchText;
        this.cachedGameNpcSearchText = this.gameNpcSearchText;
        this.cachedGlamourerDesignResults = this.glamourerDesignCatalog.Search(this.glamourerSearchText, int.MaxValue);
        this.cachedGameNpcResults = this.gameNpcCatalog.Search(this.gameNpcSearchText, 80);
        this.nextActorSourceSearchRefreshAt = now.AddMilliseconds(500);
    }

    private static bool IsRecognizedGlamourerDesign(GlamourerDesignEntry design)
        => !design.SourceDescription.Contains("无法识别固定结构", StringComparison.OrdinalIgnoreCase) &&
           !LooksLikeGlamourerAutomationPreset(design);

    private static bool LooksLikeGlamourerAutomationPreset(GlamourerDesignEntry design)
    {
        var text = string.Join('|', design.Name, design.Identifier, design.FilePath, design.SourceDescription);
        return text.Contains("自动执行", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Automation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("AutoApply", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Auto-Apply", StringComparison.OrdinalIgnoreCase);
    }

    private void DrawSelectedActorDetailsPanel()
    {
        var selectedActor = string.IsNullOrWhiteSpace(this.selectedActorRuntimeId) ? null : this.realNpcSpawn.GetActor(this.selectedActorRuntimeId);
        if (selectedActor == null)
        {
            ImGui.TextWrapped(T("当前没有选中 Actor。请在左侧 Actor 列表中选择一个实例。", "No Actor is selected. Select an instance from the Actor list."));
            return;
        }
        ImGui.PushID(selectedActor.RuntimeId);
        ImGui.TextWrapped(T($"Actor：{selectedActor.DisplayName}", $"Actor: {selectedActor.DisplayName}"));
        ImGui.BeginDisabled(this.IsInGpose());
        if (ImGui.Button(T("删除此 Actor", "Delete Actor")))
        {
            this.OpenConfirmPopupAtMouse("ConfirmDeleteSelectedActor");
        }
        ImGui.EndDisabled();
        if (this.DrawConfirmPopup("ConfirmDeleteSelectedActor", T("确认删除此 Actor？", "Delete this Actor?")))
        {
            this.DeleteSelectedActor(selectedActor);
            ImGui.PopID();
            return;
        }

        this.DrawSelectedActorTransformEditor(selectedActor, null);
        this.DrawSelectedActorBehaviorEditor(selectedActor, null);
        this.DrawSelectedActorActionSequenceEditor(selectedActor);
        this.DrawSelectedActorAppearanceEditor(selectedActor, null);
        ImGui.PopID();
    }

    #pragma warning disable CS0162, CS8602

    private void DrawSelectedActorTransformEditor(RuntimeActorInstance actor, CustomNpc? npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("Actor 世界变换", "Actor World Transform"));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("复制", "Copy")))
        {
            ImGui.SetClipboardText(FormatActorTransformClipboard(actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale));
            this.realNpcSpawn.SetMessage(T("已复制 Actor Transform。", "Actor transform copied."));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(T("粘贴", "Paste")))
        {
            var clipboard = ImGui.GetClipboardText();
            if (TryParseActorTransformClipboard(clipboard, out var pastedPosition, out var pastedRotation, out var pastedScale))
            {
                actor.TransformEditPosition = pastedPosition;
                actor.TransformEditRotationEuler = pastedRotation;
                actor.TransformEditScale = pastedScale;
                this.realNpcSpawn.ApplyAndSaveActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
                this.realNpcSpawn.SetMessage(T("已粘贴并应用 Actor Transform。", "Actor transform pasted and applied."));
            }
            else
            {
                this.realNpcSpawn.SetMessage(T("剪贴板中没有可识别的 Actor Transform。", "Clipboard does not contain a recognized Actor transform."));
            }
        }

        var runtimeReady = actor.LifecycleState == ActorLifecycleState.Ready && actor.IsValid && actor.CharacterObject != null && !actor.IsStale;
        var stateColor = runtimeReady
            ? new Vector4(0.25f, 0.95f, 0.45f, 1f)
            : actor.LifecycleState == ActorLifecycleState.Failed
                ? new Vector4(1f, 0.35f, 0.25f, 1f)
                : new Vector4(1f, 0.78f, 0.25f, 1f);
        ImGui.TextColored(stateColor, $"Lifecycle: {actor.LifecycleState}");
        ImGui.Spacing();

        if (actor.TransformEditScale == Vector3.Zero)
            actor.TransformEditScale = Vector3.One;
        actor.TransformEditRotationEuler = ActorTransformUtil.NormalizeRotation(actor.TransformEditRotationEuler);
        actor.TransformEditScale = ActorTransformUtil.NormalizeScale(actor.TransformEditScale);

        var transformChanged = false;
        var editPosition = actor.TransformEditPosition;
        transformChanged |= DrawSmallFloatStepper(T("X", "X"), "ActorTransformX", ref editPosition.X, 0.2f);
        ImGui.SameLine(0f, 12f);
        transformChanged |= DrawSmallFloatStepper(T("Y", "Y"), "ActorTransformY", ref editPosition.Y, 0.2f);
        ImGui.SameLine(0f, 12f);
        transformChanged |= DrawSmallFloatStepper(T("Z", "Z"), "ActorTransformZ", ref editPosition.Z, 0.2f);
        actor.TransformEditPosition = editPosition;
        ImGui.NewLine();

        var yawDegrees = RadiansToDegrees(actor.TransformEditRotationEuler.Y);
        if (DrawSmallFloatStepper(T("旋转", "Rotation"), "ActorTransformRotation", ref yawDegrees, 0.2f))
        {
            actor.TransformEditRotationEuler = new Vector3(0f, DegreesToRadians(yawDegrees), 0f);
            transformChanged = true;
        }

        var uniformScale = ActorTransformUtil.UniformScaleFrom(actor.TransformEditScale);
        ImGui.SameLine(0f, 12f);
        if (DrawSmallFloatStepper(T("缩放", "Scale"), "ActorTransformScale", ref uniformScale, 0.2f, 0.01f))
        {
            actor.TransformEditScale = new Vector3(MathF.Max(0.01f, uniformScale));
            transformChanged = true;
        }

        if (transformChanged)
            this.realNpcSpawn.ApplyAndSaveActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);

        ImGui.Spacing();
        if (ImGui.Button(T("移动到玩家位置", "Move to Player Position")) && this.runtime.PlayerPosition.HasValue)
        {
            actor.TransformEditPosition = this.runtime.PlayerPosition.Value;
            this.realNpcSpawn.ApplyAndSaveActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }
        ImGui.SameLine();
        if (ImGui.Button(T("重置", "Reset")))
        {
            actor.TransformEditPosition = actor.SpawnPosition;
            actor.TransformEditRotationEuler = Vector3.Zero;
            actor.TransformEditScale = Vector3.One;
            this.realNpcSpawn.ApplyAndSaveActorTransform(actor.RuntimeId, actor.TransformEditPosition, actor.TransformEditRotationEuler, actor.TransformEditScale);
        }
    }

    private static bool DrawSmallFloatStepper(string label, string id, ref float value, float delta, float min = float.MinValue)
    {
        var changed = false;
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(58f);
        if (ImGui.InputFloat(label, ref value, 0f, 0f, "%.2f"))
            changed = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("+"))
        {
            value += delta;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("-"))
        {
            value -= delta;
            changed = true;
        }

        if (changed && value < min)
            value = min;
        ImGui.PopID();
        return changed;
    }

    private static bool DrawVector3StepperRow(string label, string idPrefix, ref Vector3 value, float delta, float min = float.MinValue)
    {
        var changed = false;
        ImGui.TextUnformatted(label);
        ImGui.SameLine(82f);
        changed |= DrawSmallFloatStepper(T("X", "X"), $"{idPrefix}X", ref value.X, delta, min);
        ImGui.SameLine(0f, 12f);
        changed |= DrawSmallFloatStepper(T("Y", "Y"), $"{idPrefix}Y", ref value.Y, delta, min);
        ImGui.SameLine(0f, 12f);
        changed |= DrawSmallFloatStepper(T("Z", "Z"), $"{idPrefix}Z", ref value.Z, delta, min);
        return changed;
    }

    private static string FormatActorTransformClipboard(Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var yawDegrees = RadiansToDegrees(ActorTransformUtil.NormalizeRotation(rotationEuler).Y);
        var uniformScale = ActorTransformUtil.UniformScaleFrom(ActorTransformUtil.NormalizeScale(scale));
        return string.Join(
            Environment.NewLine,
            "YourcraftActorTransform v1",
            FormattableString.Invariant($"x={position.X:0.######}"),
            FormattableString.Invariant($"y={position.Y:0.######}"),
            FormattableString.Invariant($"z={position.Z:0.######}"),
            FormattableString.Invariant($"yaw={yawDegrees:0.######}"),
            FormattableString.Invariant($"scale={uniformScale:0.######}"));
    }

    private static bool TryParseActorTransformClipboard(string? text, out Vector3 position, out Vector3 rotationEuler, out Vector3 scale)
    {
        position = Vector3.Zero;
        rotationEuler = Vector3.Zero;
        scale = Vector3.One;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawToken in text.Split(['\r', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = rawToken.IndexOf('=');
            if (separatorIndex < 0)
                separatorIndex = rawToken.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= rawToken.Length - 1)
                continue;

            var key = rawToken[..separatorIndex].Trim();
            var valueText = rawToken[(separatorIndex + 1)..].Trim();
            if (float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value))
                values[key] = value;
        }

        if (!values.TryGetValue("x", out var x) ||
            !values.TryGetValue("y", out var y) ||
            !values.TryGetValue("z", out var z) ||
            !values.TryGetValue("yaw", out var yawDegrees) ||
            !values.TryGetValue("scale", out var uniformScale))
        {
            return false;
        }

        position = ActorTransformUtil.SanitizePosition(new Vector3(x, y, z), Vector3.Zero);
        rotationEuler = new Vector3(0f, DegreesToRadians(yawDegrees), 0f);
        scale = new Vector3(MathF.Max(0.01f, uniformScale));
        return true;
    }

    private static string FormatFullTransformClipboard(string header, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var rotationDegrees = RadiansVectorToDegrees(rotationEuler);
        return string.Join(
            Environment.NewLine,
            $"{header} v1",
            FormattableString.Invariant($"x={position.X:0.######}"),
            FormattableString.Invariant($"y={position.Y:0.######}"),
            FormattableString.Invariant($"z={position.Z:0.######}"),
            FormattableString.Invariant($"pitch={rotationDegrees.X:0.######}"),
            FormattableString.Invariant($"yaw={rotationDegrees.Y:0.######}"),
            FormattableString.Invariant($"roll={rotationDegrees.Z:0.######}"),
            FormattableString.Invariant($"scaleX={scale.X:0.######}"),
            FormattableString.Invariant($"scaleY={scale.Y:0.######}"),
            FormattableString.Invariant($"scaleZ={scale.Z:0.######}"));
    }

    private static bool TryParseFullTransformClipboard(string? text, out Vector3 position, out Vector3 rotationEuler, out Vector3 scale)
    {
        position = Vector3.Zero;
        rotationEuler = Vector3.Zero;
        scale = Vector3.One;
        var values = ParseClipboardValues(text);
        if (!values.TryGetValue("x", out var x) ||
            !values.TryGetValue("y", out var y) ||
            !values.TryGetValue("z", out var z))
        {
            return false;
        }

        var pitch = values.GetValueOrDefault("pitch", 0f);
        var yaw = values.GetValueOrDefault("yaw", 0f);
        var roll = values.GetValueOrDefault("roll", 0f);
        var uniformScale = values.GetValueOrDefault("scale", 1f);
        var scaleX = values.GetValueOrDefault("scaleX", uniformScale);
        var scaleY = values.GetValueOrDefault("scaleY", uniformScale);
        var scaleZ = values.GetValueOrDefault("scaleZ", uniformScale);

        position = ActorTransformUtil.SanitizePosition(new Vector3(x, y, z), Vector3.Zero);
        rotationEuler = DegreesVectorToRadians(new Vector3(pitch, yaw, roll));
        scale = Vector3.Max(new Vector3(scaleX, scaleY, scaleZ), new Vector3(0.01f));
        return true;
    }

    private static string FormatLightParamsClipboard(LocalLightInstance light)
        => string.Join(
            Environment.NewLine,
            "YourcraftLightParams v1",
            $"kind={light.LightKind}",
            $"falloffType={light.FalloffType}",
            FormattableString.Invariant($"colorR={light.ColorRgb.X:0.######}"),
            FormattableString.Invariant($"colorG={light.ColorRgb.Y:0.######}"),
            FormattableString.Invariant($"colorB={light.ColorRgb.Z:0.######}"),
            FormattableString.Invariant($"intensity={light.Intensity:0.######}"),
            FormattableString.Invariant($"range={light.Range:0.######}"),
            FormattableString.Invariant($"falloff={light.Falloff:0.######}"),
            FormattableString.Invariant($"lightAngle={light.LightAngle:0.######}"),
            FormattableString.Invariant($"falloffAngle={light.FalloffAngle:0.######}"),
            FormattableString.Invariant($"areaX={light.AreaAngleX:0.######}"),
            FormattableString.Invariant($"areaY={light.AreaAngleY:0.######}"),
            $"specular={light.EnableSpecular}",
            $"dynamicShadows={light.EnableDynamicShadows}");

    private static bool TryApplyLightParamsClipboard(string? text, LocalLightInstance light)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var raw = ParseClipboardRawValues(text);
        var values = ParseClipboardValues(text);
        if (!raw.ContainsKey("kind") && !values.ContainsKey("intensity") && !values.ContainsKey("range"))
            return false;

        if (raw.TryGetValue("kind", out var kindText) && Enum.TryParse<LocalLightKind>(kindText, true, out var kind))
            light.LightKind = kind;
        if (raw.TryGetValue("falloffType", out var falloffText) && Enum.TryParse<LocalLightFalloffType>(falloffText, true, out var falloffType))
            light.FalloffType = falloffType;
        if (values.TryGetValue("colorR", out var r) || values.TryGetValue("r", out r))
            light.ColorRgb = new Vector3(Math.Clamp(r, 0f, 1f), light.ColorRgb.Y, light.ColorRgb.Z);
        if (values.TryGetValue("colorG", out var g) || values.TryGetValue("g", out g))
            light.ColorRgb = new Vector3(light.ColorRgb.X, Math.Clamp(g, 0f, 1f), light.ColorRgb.Z);
        if (values.TryGetValue("colorB", out var b) || values.TryGetValue("b", out b))
            light.ColorRgb = new Vector3(light.ColorRgb.X, light.ColorRgb.Y, Math.Clamp(b, 0f, 1f));
        if (values.TryGetValue("intensity", out var intensity))
            light.Intensity = MathF.Max(0f, intensity);
        if (values.TryGetValue("range", out var range))
            light.Range = MathF.Max(0f, range);
        if (values.TryGetValue("falloff", out var falloff))
            light.Falloff = MathF.Max(0f, falloff);
        if (values.TryGetValue("lightAngle", out var lightAngle) || values.TryGetValue("spotAngle", out lightAngle))
            light.LightAngle = Math.Clamp(lightAngle, 0f, 90f);
        if (values.TryGetValue("falloffAngle", out var falloffAngle))
            light.FalloffAngle = Math.Clamp(falloffAngle, 0f, 90f);
        if (values.TryGetValue("areaX", out var areaX))
            light.AreaAngleX = Math.Clamp(areaX, 0f, 90f);
        if (values.TryGetValue("areaY", out var areaY))
            light.AreaAngleY = Math.Clamp(areaY, 0f, 90f);
        if (raw.TryGetValue("specular", out var specularText) && bool.TryParse(specularText, out var specular))
            light.EnableSpecular = specular;
        if (raw.TryGetValue("dynamicShadows", out var shadowsText) && bool.TryParse(shadowsText, out var shadows))
            light.EnableDynamicShadows = shadows;
        return true;
    }

    private static Dictionary<string, float> ParseClipboardValues(string? text)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, valueText) in ParseClipboardRawValues(text))
        {
            if (float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value))
                result[key] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ParseClipboardRawValues(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var rawToken in text.Split(['\r', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = rawToken.IndexOf('=');
            if (separatorIndex < 0)
                separatorIndex = rawToken.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= rawToken.Length - 1)
                continue;

            result[rawToken[..separatorIndex].Trim()] = rawToken[(separatorIndex + 1)..].Trim();
        }
        return result;
    }


    #pragma warning restore CS0162, CS8602

    private void DrawSelectedActorBehaviorEditor(RuntimeActorInstance actor, CustomNpc? npc)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("Actor 行为", "Actor Behavior"));
        var lookAtEnabled = actor.LookAtPlayerEnabled;
        var lookAtChanged = false;
        if (ImGui.Checkbox(T("此 Actor 看向玩家", "Look at Player"), ref lookAtEnabled))
        {
            actor.LookAtPlayerEnabled = lookAtEnabled;
            actor.LookAtMode = NpcLookAtMode.NativeLookAt;
            lookAtChanged = true;
        }

        var lookRadius = actor.LookAtRadius <= 0.1f ? npc?.LookAtRadius ?? 8f : actor.LookAtRadius;
        ImGui.SetNextItemWidth(86f);
        if (ImGui.InputFloat(T("此 Actor 看向半径", "Look Radius"), ref lookRadius))
        {
            actor.LookAtRadius = Math.Max(0.1f, lookRadius);
            lookAtChanged = true;
        }

        actor.LookAtMode = NpcLookAtMode.NativeLookAt;

        if (lookAtChanged)
            this.realNpcSpawn.UpdateActorLookAtSettings(actor.RuntimeId, actor.LookAtPlayerEnabled, actor.LookAtRadius);

        ImGui.Spacing();
        var animationId = (int)Math.Min(actor.CurrentAnimationId == 0 ? actor.DefaultAnimationId : actor.CurrentAnimationId, int.MaxValue);
        ImGui.SetNextItemWidth(130f);
        if (ImGui.InputInt(T("此 Actor 动画 ID", "Actor Animation ID"), ref animationId))
            actor.CurrentAnimationId = (uint)Math.Max(0, animationId);
        ImGui.SameLine(0f, 8f);
        this.DrawAnimationPickerButton("##ActorCurrentAnimationPicker", ActorAnimationPickerRequest.ForActorCurrent(actor.RuntimeId, ActorAnimationPickerMode.EmoteActionsOnly));

        ImGui.Spacing();
        var expressionSettingsChanged = false;
        var expressionId = (int)Math.Min(actor.CurrentExpressionId, int.MaxValue);
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt(T("表情", "Expression"), ref expressionId))
        {
            actor.CurrentExpressionId = (uint)Math.Max(0, expressionId);
            expressionSettingsChanged = true;
        }
        ImGui.SameLine(0f, 8f);
        this.DrawAnimationPickerButton("##ActorExpressionPicker", ActorAnimationPickerRequest.ForActorExpression(actor.RuntimeId, ActorAnimationPickerMode.ExpressionCandidates));

        var expressionLayer = actor.CurrentExpressionLayer;
        ImGui.SameLine(0f, 14f);
        ImGui.SetNextItemWidth(120f);
        if (DrawExpressionLayerCombo(T("层", "Layer"), ref expressionLayer))
        {
            actor.CurrentExpressionLayer = expressionLayer;
            expressionSettingsChanged = true;
        }

        var expressionLoopIntervalMs = (int)MathF.Round(Math.Max(0.05f, actor.ExpressionBlendLoopIntervalSeconds) * 1000f);
        ImGui.SameLine(0f, 14f);
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputInt(T("间隔 ms", "Interval ms"), ref expressionLoopIntervalMs))
        {
            actor.ExpressionBlendLoopIntervalSeconds = Math.Max(50, expressionLoopIntervalMs) / 1000f;
            expressionSettingsChanged = true;
        }
        if (expressionSettingsChanged)
            this.realNpcSpawn.UpdateActorExpressionSettings(actor.RuntimeId, actor.CurrentExpressionId, actor.CurrentExpressionLayer, actor.ExpressionBlendLoopIntervalSeconds);

        ImGui.Spacing();
        var lipTalkKey = actor.CurrentLipTalkKey;
        var lipTalkId = actor.CurrentLipTalkId;
        ImGui.SetNextItemWidth(220f);
        if (this.DrawLipAnimationCombo(T("口型 / Lips", "Lips"), ref lipTalkKey, ref lipTalkId))
            this.realNpcSpawn.SetActorLipTalkPreset(actor.RuntimeId, lipTalkKey);

        var lipLoopIntervalMs = (int)MathF.Round(Math.Max(0.05f, actor.LipTalkLoopIntervalSeconds) * 1000f);
        ImGui.SameLine(0f, 14f);
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputInt(T("间隔 ms##Lip", "Interval ms##Lip"), ref lipLoopIntervalMs))
        {
            actor.LipTalkLoopIntervalSeconds = Math.Max(50, lipLoopIntervalMs) / 1000f;
            this.realNpcSpawn.UpdateActorLipTalkSettings(actor.RuntimeId, actor.CurrentLipTalkKey, actor.CurrentLipTalkId, actor.LipTalkLoopIntervalSeconds);
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
        if (ImGui.Button(T("播放动画", "Play Animation")))
            this.realNpcSpawn.PlayAnimation(actor.RuntimeId, actor.CurrentAnimationId);
        ImGui.SameLine();
        if (ImGui.Button(T("停止/恢复 idle", "Stop / Restore Idle")))
            this.realNpcSpawn.StopAnimation(actor.RuntimeId);
        ImGui.EndDisabled();

        if (ImGui.Button(T("表情单次应用", "Apply Expression Once")))
            this.realNpcSpawn.PlayExpressionBlend(actor.RuntimeId, actor.CurrentExpressionId, actor.CurrentExpressionLayer);
        ImGui.SameLine();
        if (ImGui.Button(T("表情 Loop", "Expression Loop")))
            this.realNpcSpawn.StartExpressionBlendLoop(actor.RuntimeId, actor.CurrentExpressionId, actor.CurrentExpressionLayer, actor.ExpressionBlendLoopIntervalSeconds);
        ImGui.SameLine();
        if (ImGui.Button(T("表情 Stop", "Stop Expression")))
            this.realNpcSpawn.StopExpressionBlendLoop(actor.RuntimeId);
        ImGui.SameLine();
        if (ImGui.Button(T("清除表情选择", "Clear Expression")))
            this.realNpcSpawn.ClearExpressionBlend(actor.RuntimeId);

        if (ImGui.Button(T("口型单次应用", "Apply Lips Once")))
            this.realNpcSpawn.ApplyLipTalkPreset(actor.RuntimeId, actor.CurrentLipTalkKey);
        ImGui.SameLine();
        if (ImGui.Button(T("口型 Loop", "Lip Loop")))
            this.realNpcSpawn.StartLipTalkLoopPreset(actor.RuntimeId, actor.CurrentLipTalkKey, actor.LipTalkLoopIntervalSeconds);
        ImGui.SameLine();
        if (ImGui.Button(T("口型 Stop", "Stop Lips")))
            this.realNpcSpawn.StopLipTalkLoop(actor.RuntimeId);
    }

    private void DrawSelectedActorActionSequenceEditor(RuntimeActorInstance actor)
    {
        ImGui.Separator();
        if (!ImGui.TreeNode(T("动作序列 + 头顶气泡", "Action Sequence + Bubble")))
            return;

        var enabled = actor.EnableActionSequence;
        if (ImGui.Checkbox(T("启用动作序列", "Enable Action Sequence"), ref enabled))
        {
            actor.EnableActionSequence = enabled;
            this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
        }

        var loop = actor.ActionSequenceLoop;
        if (ImGui.Checkbox(T("循环播放", "Loop Playback"), ref loop))
            actor.ActionSequenceLoop = loop;

        var loopDelay = actor.ActionSequenceLoopDelay;
        if (ImGui.InputFloat(T("循环间隔（秒）", "Loop Delay (seconds)"), ref loopDelay))
            actor.ActionSequenceLoopDelay = Math.Max(0f, loopDelay);

        ImGui.TextWrapped(T($"状态：{actor.ActionSequenceStatus}", $"Status: {actor.ActionSequenceStatus}"));
        ImGui.TextWrapped(T($"错误：{(string.IsNullOrWhiteSpace(actor.LastActionSequenceError) ? "无" : actor.LastActionSequenceError)}", $"Error: {(string.IsNullOrWhiteSpace(actor.LastActionSequenceError) ? "none" : actor.LastActionSequenceError)}"));

        if (ImGui.Button(T("添加步骤", "Add Step")))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = $"Step {actor.ActionSequence.Count + 1}", DurationSeconds = 3f });
        ImGui.SameLine();
        if (ImGui.Button(T("添加 Spawn", "Add Spawn")))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = "Spawn", Kind = ActorActionStepKind.Spawn, DurationSeconds = 0.1f });
        ImGui.SameLine();
        if (ImGui.Button(T("添加 Despawn", "Add Despawn")))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = "Despawn", Kind = ActorActionStepKind.Despawn, DurationSeconds = 1f, HideBubbleOnDespawn = true });
        ImGui.SameLine();
        if (ImGui.Button(T("添加 Move", "Add Move")))
            actor.ActionSequence.Add(new ActorActionSequenceStep { Name = "Move", Kind = ActorActionStepKind.Move, DurationSeconds = 3f, MoveDurationSeconds = 3f, MoveEndWorldOffset = new Vector3(2f, 0f, 0f) });
        ImGui.SameLine();
        if (ImGui.Button(T("从当前默认动作创建步骤", "Create Step From Current Default Action")))
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
        if (ImGui.Button(T("重置序列运行状态", "Reset Sequence Runtime")))
            this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);

        for (var i = 0; i < actor.ActionSequence.Count; i++)
        {
            var step = actor.ActionSequence[i];
            ImGui.PushID(step.Id.ToString("N"));
            if (ImGui.TreeNode($"{i + 1}. {step.Name}##ActionStep"))
            {
                EditString(T("步骤名称", "Step Name"), step.Name, 96, value => step.Name = value);
                DrawActorActionStepKindCombo(step);

                if (step.Kind == ActorActionStepKind.Action)
                {
                    var animationStepId = (int)step.AnimationId;
                    if (ImGui.InputInt(T("动画 ID", "Animation ID"), ref animationStepId))
                        step.AnimationId = (ushort)Math.Clamp(animationStepId, 0, ushort.MaxValue);
                    ImGui.SameLine();
                    this.DrawAnimationPickerButton("##StepAnimationPicker", ActorAnimationPickerRequest.ForStepAnimation(actor.RuntimeId, step.Id, ActorAnimationPickerMode.EmoteActionsOnly));
                }
                else if (step.Kind == ActorActionStepKind.Despawn)
                {
                    ImGui.TextDisabled(T("Despawn 只隐藏模型，不删除 Actor。循环播放时后续 Spawn 会重新显示。", "Despawn only hides the model. It does not delete the Actor; later Spawn steps show it again."));
                    var hideBubble = step.HideBubbleOnDespawn;
                    if (ImGui.Checkbox(T("Despawn 时隐藏气泡", "Hide Bubble On Despawn"), ref hideBubble))
                        step.HideBubbleOnDespawn = hideBubble;
                }
                else if (step.Kind == ActorActionStepKind.Spawn)
                {
                    ImGui.TextDisabled(T("Spawn 只恢复已有 Actor 的显示，不创建新 Actor。", "Spawn only shows the existing Actor. It does not create a new Actor."));
                }
                else if (step.Kind == ActorActionStepKind.Move)
                {
                    this.DrawActorActionStepMoveOptions(actor, step);
                }

                var duration = step.DurationSeconds;
                if (ImGui.InputFloat(T("持续时间", "Duration"), ref duration))
                    step.DurationSeconds = Math.Max(0f, duration);

                if (step.Kind == ActorActionStepKind.Action)
                    this.DrawActorActionStepAnimationOptions(actor, step);

                DrawActorActionStepBubbleOptions(step);

                ImGui.BeginDisabled(i == 0);
                if (ImGui.Button(T("上移", "Move Up")))
                {
                    (actor.ActionSequence[i - 1], actor.ActionSequence[i]) = (actor.ActionSequence[i], actor.ActionSequence[i - 1]);
                    this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i >= actor.ActionSequence.Count - 1);
                if (ImGui.Button(T("下移", "Move Down")))
                {
                    (actor.ActionSequence[i + 1], actor.ActionSequence[i]) = (actor.ActionSequence[i], actor.ActionSequence[i + 1]);
                    this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(!actor.IsValid || actor.CharacterObject == null);
                if (ImGui.Button(T("预览当前步骤", "Preview Step")))
                    this.realNpcSpawn.TestActionSequenceStep(actor.RuntimeId, step.Id);
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button(T("删除步骤", "Delete Step")))
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
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("Actor 外观", "Actor Appearance"));
        ImGui.TextWrapped(T($"来源：{DisplayAppearanceSourceKind(actor.AppearanceSourceType)}", $"Source: {DisplayAppearanceSourceKind(actor.AppearanceSourceType)}"));
        var appearanceName = string.IsNullOrWhiteSpace(actor.GlamourerDesignName) ? actor.DisplayName : actor.GlamourerDesignName;
        ImGui.TextWrapped(T($"名称：{appearanceName}", $"Name: {appearanceName}"));
        var modelCharaId = (int)Math.Min(actor.EditingModelCharaId, int.MaxValue);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Model ID", ref modelCharaId))
            actor.EditingModelCharaId = (uint)Math.Max(0, modelCharaId);
        ImGui.SameLine();
        if (ImGui.Button(T("应用", "Apply")))
            this.realNpcSpawn.ApplyActorModelCharaOverride(actor.RuntimeId, actor.EditingModelCharaId);
    }

    private static void DrawActorActionStepKindCombo(ActorActionSequenceStep step)
    {
        if (!ImGui.BeginCombo(T("类型", "Kind"), DisplayActorActionStepKind(step.Kind)))
            return;

        foreach (var kind in Enum.GetValues<ActorActionStepKind>())
        {
            var selected = step.Kind == kind;
            if (ImGui.Selectable(DisplayActorActionStepKind(kind), selected))
                step.Kind = kind;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawActorActionStepAnimationOptions(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        var loopAnimation = step.LoopAnimation;
        if (ImGui.Checkbox(T("循环动画", "Loop Animation"), ref loopAnimation))
            step.LoopAnimation = loopAnimation;
        ImGui.SameLine();
        var stayInPose = step.StayInPose;
        if (ImGui.Checkbox(T("保持姿势", "Stay In Pose"), ref stayInPose))
            step.StayInPose = stayInPose;

        var repeatAfter = step.RepeatAfterSeconds;
        if (ImGui.InputFloat(T("重复间隔", "Repeat After"), ref repeatAfter))
            step.RepeatAfterSeconds = Math.Max(0f, repeatAfter);

        var expressionId = (int)step.ExpressionId;
        if (ImGui.InputInt(T("表情 ID", "Expression ID"), ref expressionId))
            step.ExpressionId = (ushort)Math.Clamp(expressionId, 0, ushort.MaxValue);
        ImGui.SameLine();
        this.DrawAnimationPickerButton("##StepExpressionPicker", ActorAnimationPickerRequest.ForStepExpression(actor.RuntimeId, step.Id, ActorAnimationPickerMode.ExpressionCandidates));

        var playExpression = step.PlayExpressionWithAction;
        if (ImGui.Checkbox(T("随动作播放表情", "Play Expression With Action"), ref playExpression))
            step.PlayExpressionWithAction = playExpression;
        ImGui.SameLine();
        var loopExpression = step.LoopExpression;
        if (ImGui.Checkbox(T("循环表情", "Loop Expression"), ref loopExpression))
            step.LoopExpression = loopExpression;

        DrawExpressionLayerCombo(step);

        var expressionDelay = step.ExpressionDelaySeconds;
        if (ImGui.InputFloat(T("表情延迟", "Expression Delay"), ref expressionDelay))
            step.ExpressionDelaySeconds = Math.Max(0f, expressionDelay);
        var expressionDuration = step.ExpressionDurationSeconds;
        if (ImGui.InputFloat(T("表情持续", "Expression Duration"), ref expressionDuration))
            step.ExpressionDurationSeconds = Math.Max(0f, expressionDuration);
        var expressionWeight = step.ExpressionWeight;
        if (ImGui.SliderFloat(T("表情权重", "Expression Weight"), ref expressionWeight, 0f, 1f))
            step.ExpressionWeight = Math.Clamp(expressionWeight, 0f, 1f);

        ImGui.Spacing();
        var lipOptionsChanged = false;
        var playLipTalk = step.PlayLipTalkWithAction;
        if (ImGui.Checkbox(T("随动作播放口型", "Play Lips With Action"), ref playLipTalk))
        {
            step.PlayLipTalkWithAction = playLipTalk;
            lipOptionsChanged = true;
        }
        ImGui.SameLine();
        var loopLipTalk = step.LoopLipTalk;
        if (ImGui.Checkbox(T("循环口型", "Loop Lips"), ref loopLipTalk))
        {
            step.LoopLipTalk = loopLipTalk;
            lipOptionsChanged = true;
        }

        var lipTalkKey = step.LipTalkKey;
        var lipTalkId = (uint)step.LipTalkId;
        ImGui.SetNextItemWidth(220f);
        if (this.DrawLipAnimationCombo(T("步骤口型 / Lips", "Step Lips"), ref lipTalkKey, ref lipTalkId))
        {
            step.LipTalkKey = lipTalkKey;
            step.LipTalkId = (ushort)Math.Min(lipTalkId, ushort.MaxValue);
            lipOptionsChanged = true;
        }

        ImGui.Spacing();
        var lipDelay = step.LipTalkDelaySeconds;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputFloat(T("口型延迟", "Lip Delay"), ref lipDelay))
        {
            step.LipTalkDelaySeconds = Math.Max(0f, lipDelay);
            lipOptionsChanged = true;
        }
        ImGui.SameLine(0f, 14f);
        var lipDuration = step.LipTalkDurationSeconds;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.InputFloat(T("口型持续", "Lip Duration"), ref lipDuration))
        {
            step.LipTalkDurationSeconds = Math.Max(0f, lipDuration);
            lipOptionsChanged = true;
        }

        if (lipOptionsChanged)
            this.realNpcSpawn.ResetActionSequence(actor.RuntimeId);

        ImGui.TextDisabled(T("提示：表情会交给游戏 ActionTimeline slot 自动归位；不是所有 ID 都能与基础动作叠加。", "Tip: expressions return through the game's ActionTimeline slot. Not every ID blends with every base action."));
    }

    private void DrawActorActionStepMoveOptions(RuntimeActorInstance actor, ActorActionSequenceStep step)
    {
        ImGui.TextWrapped(T("移动仅在动作序列运行时生效，不会修改 Actor 原始生成位置。关闭序列或循环重开时会回到原始位置。", "Move steps only affect sequence playback. They do not change the Actor's original spawn position."));
        var moveDuration = step.MoveDurationSeconds <= 0f ? step.DurationSeconds : step.MoveDurationSeconds;
        if (ImGui.InputFloat(T("移动时长", "Move Duration"), ref moveDuration))
            step.MoveDurationSeconds = Math.Max(0.01f, moveDuration);

        var absolute = step.MoveUseAbsoluteWorldTarget;
        if (ImGui.Checkbox(T("使用绝对世界目标", "Use Absolute World Target"), ref absolute))
            step.MoveUseAbsoluteWorldTarget = absolute;

        if (step.MoveUseAbsoluteWorldTarget)
        {
            var target = step.MoveWorldTarget;
            if (InputVector3(T("世界目标", "World Target"), ref target))
                step.MoveWorldTarget = target;
        }
        else
        {
            var start = step.MoveStartWorldOffset;
            if (InputVector3(T("起点偏移", "Start Offset"), ref start))
                step.MoveStartWorldOffset = start;
            var end = step.MoveEndWorldOffset;
            if (InputVector3(T("终点偏移", "End Offset"), ref end))
                step.MoveEndWorldOffset = end;
        }

        DrawEnumCombo(T("移动插值", "Move Interpolation"), step.MoveInterpolation, value => step.MoveInterpolation = value);
        var restore = step.MoveRestoreAtStepEnd;
        if (ImGui.Checkbox(T("Move 步骤结束后回到原点", "Restore Origin After Move"), ref restore))
            step.MoveRestoreAtStepEnd = restore;
        var face = step.MoveFaceDirection;
        if (ImGui.Checkbox(T("移动时朝向移动方向", "Face Move Direction"), ref face))
            step.MoveFaceDirection = face;
        var affectsRotation = step.MoveAffectsRotation;
        if (ImGui.Checkbox(T("移动时使用指定旋转", "Use Move Yaw"), ref affectsRotation))
            step.MoveAffectsRotation = affectsRotation;
        if (step.MoveAffectsRotation)
        {
            var yaw = step.MoveYawDegrees;
            if (ImGui.InputFloat(T("移动旋转", "Move Yaw"), ref yaw))
                step.MoveYawDegrees = yaw;
        }

        var playMoveAnimation = step.PlayMoveAnimationOnEnter;
        if (ImGui.Checkbox(T("进入 Move 时播放动画", "Play Animation On Move"), ref playMoveAnimation))
            step.PlayMoveAnimationOnEnter = playMoveAnimation;
        if (step.PlayMoveAnimationOnEnter)
        {
            var moveAnimationId = (int)step.MoveAnimationId;
            if (ImGui.InputInt(T("移动动画 ID", "Move Animation ID"), ref moveAnimationId))
                step.MoveAnimationId = (ushort)Math.Clamp(moveAnimationId, 0, ushort.MaxValue);
        }
    }

    private static void DrawExpressionLayerCombo(ActorActionSequenceStep step)
    {
        var layer = step.ExpressionLayer;
        if (DrawExpressionLayerCombo(T("表情 Layer", "Expression Layer"), ref layer))
            step.ExpressionLayer = layer;
    }

    private bool DrawLipAnimationCombo(string label, ref string lipKey, ref uint lipTimelineId)
    {
        var current = this.lipSyncPresets.Resolve(lipKey, lipTimelineId);
        var preview = $"{current.DisplayName} [{current.ResolvedTimelineId}]";
        if (!current.IsResolved && !current.IsLegacy)
            preview = $"{current.DisplayName} [unresolved]";

        if (!ImGui.BeginCombo(label, preview))
            return false;

        var changed = false;
        foreach (var entry in this.lipSyncPresets.EntriesWithLegacy(lipKey, lipTimelineId))
        {
            var selected = string.Equals(current.InternalKey, entry.InternalKey, StringComparison.OrdinalIgnoreCase);
            var display = entry.IsResolved || entry.IsLegacy
                ? $"{entry.DisplayName} [{entry.ResolvedTimelineId}]"
                : $"{entry.DisplayName} [unresolved]";
            if (ImGui.Selectable(display, selected))
            {
                lipKey = entry.InternalKey;
                lipTimelineId = entry.ResolvedTimelineId;
                changed = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{entry.InternalKey}\n{entry.Status}");
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static bool DrawExpressionLayerCombo(string label, ref ActorExpressionLayer layer)
    {
        if (!ImGui.BeginCombo(label, DisplayExpressionLayer(layer)))
            return false;

        var changed = false;
        foreach (var candidate in Enum.GetValues<ActorExpressionLayer>())
        {
            var selected = layer == candidate;
            if (ImGui.Selectable(DisplayExpressionLayer(candidate), selected))
            {
                layer = candidate;
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static void DrawActorActionStepBubbleOptions(ActorActionSequenceStep step)
    {
        var allowLookAt = step.AllowLookAtDuringStep;
        if (ImGui.Checkbox(T("此步骤允许看向玩家", "Allow Look-at During Step"), ref allowLookAt))
            step.AllowLookAtDuringStep = allowLookAt;

        var showBubble = step.ShowBubbleOnEnter;
        if (ImGui.Checkbox(T("进入步骤时显示头顶气泡", "Show Bubble On Step Enter"), ref showBubble))
            step.ShowBubbleOnEnter = showBubble;
        EditString(T("气泡文字", "Bubble Text"), step.BubbleText, 240, value => step.BubbleText = value);
        var autoDuration = step.BubbleUseAutoDuration;
        if (ImGui.Checkbox(T("气泡自动时长", "Auto Bubble Duration"), ref autoDuration))
            step.BubbleUseAutoDuration = autoDuration;
        ImGui.SameLine();
        var bubbleDuration = step.BubbleDurationSeconds;
        if (ImGui.InputFloat(T("气泡时长", "Bubble Duration"), ref bubbleDuration))
            step.BubbleDurationSeconds = Math.Max(0f, bubbleDuration);
    }

    private void DrawAnimationPickerButton(string id, ActorAnimationPickerRequest request)
    {
        if (ImGui.SmallButton($"{FontAwesomeIcon.Search.ToIconString()}{id}"))
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

        var savedPosition = ToVector3(npc.Position);
        if (npc.TerritoryType == this.runtime.TerritoryType && savedPosition != Vector3.Zero)
            return savedPosition;

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
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
            return;
        }

        var nextIndex = Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, remaining.Count - 1);
        this.selectedActorRuntimeId = remaining[nextIndex].RuntimeId;
        this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalActor, this.selectedActorRuntimeId);
        if (!deleted)
            this.realNpcSpawn.SetMessage($"删除 Actor 失败或只完成了本地移除：{actor.LastError}");
    }

    private void DrawLocalLayoutObjects()
    {
        this.SyncSceneEditorBgPartCopyMode();
        if (!this.realNpcSpawn.EnableUnsafeNativeWrites)
        {
            this.realNpcSpawn.EnableUnsafeNativeWrites = true;
            this.SetSceneEditorBgPartCollisionMode(this.localLayoutFullCollisionMode, this.confirmFullLayoutCollisionMode);
        }

        if (this.IsInGpose())
        {
            this.DrawGposeBlockedMessage("本地场景物体创建 / 修改 / Restore");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Min(620f, Math.Max(420f, available.X * 0.52f));

        if (ImGui.BeginChild("LocalLayoutLeftPanel", new Vector2(leftWidth, 0f), true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            this.DrawLocalLayoutObjectsLeftPanel();
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("LocalLayoutDetailsPanel", Vector2.Zero, true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
            this.DrawSelectedLocalLayoutObjectControls();
        ImGui.EndChild();
    }

    private void DrawLocalLayoutObjectsLeftPanel()
    {
        var collisionMode = this.localLayoutFullCollisionMode;
        if (ImGui.Checkbox(T("模型和碰撞体一起变化", "Move Collision With Model"), ref collisionMode))
            this.SetSceneEditorBgPartCollisionMode(collisionMode, collisionMode && this.confirmFullLayoutCollisionMode);
        if (this.localLayoutFullCollisionMode)
        {
            ImGui.SameLine();
            if (this.confirmFullLayoutCollisionMode)
            {
                if (ImGui.Button(T("取消碰撞确认", "Cancel Collision Confirmation")))
                    this.SetSceneEditorBgPartCollisionMode(this.localLayoutFullCollisionMode, false);
            }
            else if (ImGui.Button(T("确认启用碰撞编辑", "Confirm Collision Editing")))
            {
                this.SetSceneEditorBgPartCollisionMode(this.localLayoutFullCollisionMode, true);
            }

            if (!this.confirmFullLayoutCollisionMode)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), T("需要二次确认后才会移动碰撞体。", "Collision edits require confirmation."));
        }

        this.SyncSceneEditorBgPartCopyMode();
        this.DrawBgPartSelectionControls();

        if (this.DrawConfirmPopup("ConfirmRestoreAllLocalLayoutObjects", T("确认恢复全部复制体？", "Restore all copied objects?")))
        {
            this.localLayoutObjects.RestoreAll(
                bgParts: this.AllBgParts(),
                unsafeEnabled: this.realNpcSpawn.EnableUnsafeNativeWrites,
                fullLayoutConfirmed: this.confirmFullLayoutCollisionMode);
            this.sceneEditor.ForgetAllLocalBgPartRecords();
            this.selectedLocalLayoutObjectId = string.Empty;
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
        }

        ImGui.Separator();
        this.DrawLocalLayoutObjectTable();
    }

    private static void DrawYellowSectionLabel(string chinese, string english)
        => ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T(chinese, english));

    private static string FormatCarrierWarningForUi(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return T("无", "None");

        var parts = warning
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TranslateCarrierWarningPart)
            .Where(item => !string.IsNullOrWhiteSpace(item));
        return string.Join("; ", parts);
    }

    private static string TranslateCarrierWarningPart(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return string.Empty;

        if (warning.StartsWith("DynamicSuspected:", StringComparison.OrdinalIgnoreCase))
        {
            var reason = warning["DynamicSuspected:".Length..].Trim();
            return T($"疑似动态资源：{TranslateDynamicWarningReason(reason)}", $"Dynamic resource suspected: {TranslateDynamicWarningReason(reason)}");
        }

        return warning switch
        {
            "FloorLike" => T("地板类", "Floor-like"),
            "WallLike" => T("墙体类", "Wall-like"),
            "TerrainLike" => T("地形类", "Terrain-like"),
            "StructureLike" => T("建筑结构类", "Structure-like"),
            "TooLarge" => T("尺寸较大", "Large object"),
            "TooCloseImportantGeometry" => T("靠近重要场景结构", "Near important geometry"),
            "SharedGroupChild" => T("SharedGroup 子对象", "SharedGroup child"),
            _ => warning,
        };
    }

    private static string TranslateDynamicWarningReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return T("未知原因", "unknown reason");
        if (reason.Contains("/vfx/", StringComparison.OrdinalIgnoreCase))
            return T("路径包含 /vfx/，可能由特效控制", "path contains /vfx/, likely VFX-controlled");
        if (reason.Contains("/light/", StringComparison.OrdinalIgnoreCase))
            return T("路径包含 /light/，可能是动态灯光资源", "path contains /light/, likely a dynamic light resource");
        if (reason.Contains("/shared/", StringComparison.OrdinalIgnoreCase))
            return T("路径包含 /shared/，可能是共享动态资源", "path contains /shared/, likely a shared dynamic resource");
        if (reason.Contains("/evt/", StringComparison.OrdinalIgnoreCase))
            return T("路径包含 /evt/，可能由事件控制", "path contains /evt/, likely event-controlled");
        if (reason.Contains("/aet/", StringComparison.OrdinalIgnoreCase))
            return T("路径包含 /aet/，可能由动态控制器驱动", "path contains /aet/, likely controller-driven");
        if (reason.Contains("屏幕", StringComparison.OrdinalIgnoreCase) || reason.Contains("广告", StringComparison.OrdinalIgnoreCase))
            return T("疑似城镇动态屏幕或广告牌", "likely a town screen or billboard");

        return reason;
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
        if (HideLegacyCreateManyUi())
        {
            ImGui.TextDisabled("旧的批量创建入口已隐藏；请在画面编辑小面板里选中 BgPart 后使用“Copy 1”。");
            return;
        }
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
        if (false && ImGui.Button(mode == LocalLayoutTransformMode.VisualOnly ? "创建 N 个 VisualOnly 复制体" : "创建 N 个 FullLayoutWithCollision 复制体"))
            this.CreateMany(template, allBgParts, this.layoutCopyCount, basePosition, mode);
        ImGui.EndDisabled();
    }

    private void DrawLocalLayoutCreateSingleCopyControls()
    {
        var selectedEditable = this.sceneEditor.GetSelectedEditable();
        var selectedEditableIsBgPart = selectedEditable?.Kind is SceneEditableKind.LocalBgPart or SceneEditableKind.NativeBgPart;
        var sourceSlot = selectedEditableIsBgPart ? null : this.GetSelectedBgPart();
        var hasSource = selectedEditableIsBgPart || sourceSlot != null;
        var mode = this.localLayoutFullCollisionMode
            ? LocalLayoutTransformMode.FullLayoutWithCollision
            : LocalLayoutTransformMode.VisualOnly;
        var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "创建 1 个复制体");
        ImGui.TextWrapped(selectedEditableIsBgPart
            ? $"Source: SceneEditor {selectedEditable!.Kind} / {selectedEditable.DisplayName}"
            : sourceSlot == null
                ? "Source: none. 请先在画面编辑中选择 BgPart，或在 BgPart Slot Pool 选中 BgPart。"
                : $"Source: {sourceSlot.ResourcePath} @ {sourceSlot.Address}");
        ImGui.TextWrapped($"New copy mode: {mode}");

        var disabled = this.localLayoutObjects.IsBusy ||
                       this.localLayoutObjects.IsCreateQueueActive ||
                       !this.realNpcSpawn.EnableUnsafeNativeWrites ||
                       !hasSource ||
                       fullLayoutBlocked;
        ImGui.BeginDisabled(disabled);
        if (ImGui.Button("创建 1 个复制体##LocalLayoutCreateOne"))
        {
            if (selectedEditableIsBgPart && selectedEditable != null)
                this.TryCreateSingleBgPartCopy(selectedEditable, "LocalLayoutPage");
            else
                this.TryCreateSingleBgPartCopy(sourceSlot, "LocalLayoutPage");
        }
        ImGui.EndDisabled();

        if (disabled)
        {
            if (!this.realNpcSpawn.EnableUnsafeNativeWrites)
                ImGui.TextDisabled("需要启用 Unsafe/native 写入。");
            else if (fullLayoutBlocked)
                ImGui.TextDisabled("FullLayoutWithCollision 需要勾选危险确认。");
            else if (!hasSource)
                ImGui.TextDisabled("请先选择一个 BgPart source。");
        }

        ImGui.TextDisabled(this.sceneEditor.LastQuickActionStatus);
    }

    private bool TryCreateSingleBgPartCopy(SceneEditableRef source, string context)
    {
        this.SyncSceneEditorBgPartCopyMode();
        var ok = this.sceneEditor.TryCopyOneBgPart(source, new Vector3(0.6f, 0f, 0.6f));
        this.SyncCreatedLocalBgPartSelectionFromSceneEditor(context);
        return ok;
    }

    private bool TryCreateSingleBgPartCopy(LayoutProbeInstance? source, string context)
    {
        this.SyncSceneEditorBgPartCopyMode();
        var ok = this.sceneEditor.TryCopyOneBgPart(source, new Vector3(0.6f, 0f, 0.6f));
        this.SyncCreatedLocalBgPartSelectionFromSceneEditor(context);
        return ok;
    }

    private void SyncCreatedLocalBgPartSelectionFromSceneEditor(string context)
    {
        if (this.sceneEditorSelection.SelectedKind == SceneEditableKind.LocalBgPart &&
            !string.IsNullOrWhiteSpace(this.sceneEditorSelection.SelectedRuntimeId))
        {
            this.selectedLocalLayoutObjectId = this.sceneEditorSelection.SelectedRuntimeId;
            this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalBgPart, this.selectedLocalLayoutObjectId);
        }
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
        {
            this.selectedLocalLayoutObjectId = last.Id;
            this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalBgPart, last.Id);
        }
    }

    private void DrawBgPartSelectionControls()
    {
        ImGui.Separator();
        if (ImGui.Button(T("重新扫描 BgPart", "Rescan BgParts")))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);
        ImGui.SameLine();
        if (ImGui.Button(T("选择最近 BgPart", "Select Nearest BgPart")))
        {
            var nearest = this.FilteredBgParts().FirstOrDefault();
            if (nearest != null)
                this.SelectBgPartCandidate(nearest);
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || this.localLayoutObjects.Instances.Count == 0);
        if (ImGui.Button(T("恢复全部", "Restore All")))
            this.OpenConfirmPopupAtMouse("ConfirmRestoreAllLocalLayoutObjects");
        ImGui.EndDisabled();

        var candidate = this.GetSelectedBgPart();
        ImGui.TextWrapped(candidate == null
            ? T("当前选中 BgPart：无", "Selected BgPart: none")
            : T($"当前选中 BgPart：{candidate.ResourcePath} | 距离 {candidate.DistanceToPlayer:F1}y | {FormatVector(candidate.Position)}",
                $"Selected BgPart: {candidate.ResourcePath} | {candidate.DistanceToPlayer:F1}y | {FormatVector(candidate.Position)}"));
        if (candidate != null)
        {
            var carrierWarning = this.localLayoutObjects.GetCarrierWarningReason(candidate);
            if (!string.IsNullOrWhiteSpace(carrierWarning))
                ImGui.TextWrapped(T($"注意：{FormatCarrierWarningForUi(carrierWarning)}", $"Notice: {FormatCarrierWarningForUi(carrierWarning)}"));
            this.DrawProtectedBgPartControls(candidate);
            this.DrawPreferredModifyBgPartControls(candidate);
        }
        ImGui.InputText(T("搜索 resourcePath", "Search resourcePath"), ref this.bgPartSearchText, 256);

        var rows = this.FilteredBgParts().Take(80).ToList();
        if (!ImGui.BeginTable("BgPartSelectionTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 220f)))
            return;
        ImGui.TableSetupColumn(T("选择", "Select"), ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn(T("距离", "Distance"), ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn(T("来源", "Source"), ImGuiTableColumnFlags.WidthFixed, 86f);
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn(T("可见", "Visible"), ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn(T("注意", "Notice"));
        ImGui.TableSetupColumn(T("操作", "Action"), ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID(item.Address);
            ImGui.TableSetColumnIndex(0);
            var selected = string.Equals(this.selectedBgPartAddress, item.Address, StringComparison.OrdinalIgnoreCase);
            if (ImGui.RadioButton($"##SelectBgPart{item.Address}", selected))
                this.SelectBgPartCandidate(item);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{item.DistanceToPlayer:F1}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(item.SourceKind);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(item.Visible ? T("是", "Yes") : T("否", "No"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(FormatCarrierWarningForUi(this.localLayoutObjects.GetCarrierWarningReason(item)));
            ImGui.TableSetColumnIndex(6);
            var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive || !this.realNpcSpawn.EnableUnsafeNativeWrites || fullLayoutBlocked);
            if (ImGui.Button(T("创建复制体", "Create Copy")))
            {
                this.SelectBgPartCandidate(item);
                this.TryCreateSingleBgPartCopy(item, "LocalLayoutTable");
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawLocalLayoutObjectTable()
    {
        DrawYellowSectionLabel("复制体管理", "Copy Management");
        if (!ImGui.BeginTable("LocalLayoutObjects", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 300f)))
            return;
        ImGui.TableSetupColumn(T("选择", "Select"), ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn(T("原模型", "Original Model"));
        ImGui.TableSetupColumn(T("当前模型", "Current Model"));
        ImGui.TableSetupColumn("mdl path");
        ImGui.TableSetupColumn(T("状态", "Status"), ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn(T("位置", "Position"));
        ImGui.TableSetupColumn(T("缩放", "Scale"));
        ImGui.TableSetupColumn(T("操作", "Actions"), ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableHeadersRow();
        foreach (var instance in this.localLayoutObjects.Instances)
        {
            ImGui.TableNextRow();
            ImGui.PushID(instance.Id);
            ImGui.TableSetColumnIndex(0);
            var selected = string.Equals(this.selectedLocalLayoutObjectId, instance.Id, StringComparison.Ordinal);
            if (ImGui.RadioButton($"##SelectLocalLayout{instance.Id}", selected))
            {
                this.selectedLocalLayoutObjectId = instance.Id;
                this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalBgPart, instance.Id);
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(instance.OriginalSlotResourcePath);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(instance.CurrentModelPath);
            ImGui.TableSetColumnIndex(3);
            var rowCustomPath = instance.CustomModelPath;
            if (ImGui.InputText("##rowCustomMdl", ref rowCustomPath, 320))
                instance.CustomModelPath = rowCustomPath;
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(instance.InstanceState);
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(FormatVector(instance.CurrentPosition));
            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped(FormatVector(instance.CurrentScale));
            ImGui.TableSetColumnIndex(7);
            var fullLayoutNeedsConfirmation = this.sceneEditor.IsBgPartCollisionConfirmationRequired(SceneEditableKind.LocalBgPart);
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid || instance.IsDuplicate || instance.IsRenderInvalid || fullLayoutNeedsConfirmation);
            if (ImGui.Button(T("应用 mdl", "Apply MDL")))
            {
                if (this.sceneEditor.ApplyWorldTransform(SceneEditableKind.LocalBgPart, instance.Id, WorldTransform.FromEuler(instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale)))
                    this.localLayoutObjects.ApplyMdlPath(instance.Id, instance.CustomModelPath, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || instance.IsRestored || instance.IsInvalid);
            if (ImGui.Button(T("恢复", "Restore")))
                this.localLayoutObjects.RestoreModelAndTransform(instance.Id, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawProtectedBgPartControls(LayoutProbeInstance candidate)
    {
        var registry = this.localLayoutObjects.ProtectedBgParts;
        if (registry == null)
            return;

        DrawYellowSectionLabel("Bgparts保护", "BgParts Protection");
        if (ImGui.Button(T("保护当前 slot", "Protect Slot")))
            registry.ProtectSlot(candidate, "User protected from Yourcraft UI");
        ImGui.SameLine();
        if (ImGui.Button(T("取消保护 slot", "Unprotect Slot")))
            this.OpenConfirmPopupAtMouse("ConfirmUnprotectSelectedBgPartSlot");
        ImGui.SameLine();
        if (ImGui.Button(T("保护同款资源", "Protect Resource")))
            registry.ProtectResourcePath(candidate.ResourcePath, currentTerritoryOnly: true, "User protected resourcePath from Yourcraft UI");
        ImGui.SameLine();
        if (ImGui.Button(T("取消同款保护", "Unprotect Resource")))
            this.OpenConfirmPopupAtMouse("ConfirmUnprotectSelectedBgPartResource");

        if (this.DrawConfirmPopup("ConfirmUnprotectSelectedBgPartSlot", T("确认取消保护当前 slot？", "Unprotect the selected slot?")))
            registry.UnprotectSlot(candidate);
        if (this.DrawConfirmPopup("ConfirmUnprotectSelectedBgPartResource", T("确认取消同款资源保护？", "Unprotect this resource?")))
            registry.UnprotectResourcePath(candidate.ResourcePath);
    }

    private void DrawBgPartProtectionTab()
    {
        var registry = this.localLayoutObjects.ProtectedBgParts;
        if (registry == null)
        {
            ImGui.TextWrapped(T("BgPart 保护列表服务不可用。", "BgPart protection registry is unavailable."));
            return;
        }

        ImGui.InputText(T("搜索保护项", "Search Protection"), ref this.protectedBgPartSearchText, 256);
        ImGui.SameLine();
        if (ImGui.Button(T("重新扫描 BgPart", "Rescan BgParts")))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);

        var candidate = this.GetSelectedBgPart();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("当前选中 BgPart 快捷保护", "Protect Selected BgPart"));
        if (candidate == null)
        {
            ImGui.TextWrapped(T("当前没有选中 BgPart。可以在画面编辑或本地场景物体中选择一个。", "No BgPart selected. Select one in Scene Editor or Local Scene Objects."));
        }
        else
        {
            ImGui.TextWrapped($"{candidate.ResourcePath} | {FormatVector(candidate.Position)}");
            this.DrawProtectedBgPartControls(candidate);
        }

        ImGui.Separator();
        ImGui.BeginDisabled(registry.ProtectedSlots.Count == 0 && registry.ProtectedResourcePaths.Count == 0);
        if (ImGui.Button(T("清空保护列表", "Clear Protection List")))
            this.OpenConfirmPopupAtMouse("ConfirmClearProtectedBgParts");
        ImGui.EndDisabled();

        this.DrawProtectedResourcePathTable(registry);
        this.DrawProtectedSlotTable(registry);

        if (this.DrawConfirmPopup("ConfirmClearProtectedBgParts", T("确认清空保护列表？", "Clear the protection list?")))
            registry.Clear();
    }

    private void DrawProtectedResourcePathTable(ProtectedBgPartRegistry registry)
    {
        var rows = registry.ProtectedResourcePaths
            .Where(item => this.MatchesProtectedBgPartSearch(item.ResourcePath, item.Note, item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped(T($"resourcePath 保护：显示 {rows.Count}/{registry.ProtectedResourcePaths.Count}", $"Protected resources: {rows.Count}/{registry.ProtectedResourcePaths.Count}"));
        if (!ImGui.BeginTable("ProtectedBgPartResourcePaths", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn(T("范围", "Scope"));
        ImGui.TableSetupColumn(T("地图", "Territory"));
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn(T("备注", "Note"));
        ImGui.TableSetupColumn(T("操作", "Actions"));
        ImGui.TableHeadersRow();

        var removeRequested = false;
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
            if (ImGui.Button(T("移除", "Remove")))
            {
                this.pendingProtectedResourcePathRemove = item;
                removeRequested = true;
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeRequested)
            this.OpenConfirmPopupAtMouse("ConfirmRemoveProtectedResourcePath");
        if (this.DrawConfirmPopup("ConfirmRemoveProtectedResourcePath", T("确认移除此保护资源？", "Remove this protected resource?")))
        {
            registry.RemoveResourcePathEntry(this.pendingProtectedResourcePathRemove);
            this.pendingProtectedResourcePathRemove = null;
        }
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
        ImGui.TextWrapped(T($"slot 保护：显示 {rows.Count}/{registry.ProtectedSlots.Count}", $"Protected slots: {rows.Count}/{registry.ProtectedSlots.Count}"));
        if (!ImGui.BeginTable("ProtectedBgPartSlots", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 260f)))
            return;

        ImGui.TableSetupColumn(T("选择", "Select"), ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn(T("地图", "Territory"));
        ImGui.TableSetupColumn(T("来源", "Source"));
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn(T("位置", "Position"));
        ImGui.TableSetupColumn(T("备注", "Note"));
        ImGui.TableSetupColumn(T("操作", "Actions"));
        ImGui.TableHeadersRow();

        var removeRequested = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"protected-slot-{i}-{item.LayoutInstanceAddress}-{item.StableKey}");
            ImGui.TableSetColumnIndex(0);
            var resolved = this.ResolveBgPartSlot(item.LayoutInstanceAddress, item.StableKey, item.ResourcePath, ToVector3(item.OriginalPosition));
            var selected = resolved != null && string.Equals(this.selectedBgPartAddress, resolved.Address, StringComparison.OrdinalIgnoreCase);
            ImGui.BeginDisabled(resolved == null);
            if (ImGui.RadioButton("##SelectProtectedSlot", selected) && resolved != null)
                this.SelectBgPartCandidate(resolved);
            ImGui.EndDisabled();
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.TerritoryType == 0 ? "all" : item.TerritoryType.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.SourceType);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(FormatVector(ToVector3(item.OriginalPosition)));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(6);
            if (ImGui.Button(T("移除", "Remove")))
            {
                this.pendingProtectedSlotRemove = item;
                removeRequested = true;
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeRequested)
            this.OpenConfirmPopupAtMouse("ConfirmRemoveProtectedSlot");
        if (this.DrawConfirmPopup("ConfirmRemoveProtectedSlot", T("确认移除此保护 slot？", "Remove this protected slot?")))
        {
            registry.RemoveSlotEntry(this.pendingProtectedSlotRemove);
            this.pendingProtectedSlotRemove = null;
        }
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

        DrawYellowSectionLabel("Bgparts优先改动", "BgParts Preferred Modify");
        if (ImGui.Button(T("优先当前 slot", "Prefer Slot")))
            registry.ProtectSlot(candidate, "User preferred slot from Yourcraft UI");
        ImGui.SameLine();
        if (ImGui.Button(T("取消优先 slot", "Unprefer Slot")))
            this.OpenConfirmPopupAtMouse("ConfirmUnpreferSelectedBgPartSlot");
        ImGui.SameLine();
        if (ImGui.Button(T("优先同款资源", "Prefer Resource")))
            registry.ProtectResourcePath(candidate.ResourcePath, currentTerritoryOnly: true, "User preferred resourcePath from Yourcraft UI");
        ImGui.SameLine();
        if (ImGui.Button(T("取消同款优先", "Unprefer Resource")))
            this.OpenConfirmPopupAtMouse("ConfirmUnpreferSelectedBgPartResource");

        if (this.DrawConfirmPopup("ConfirmUnpreferSelectedBgPartSlot", T("确认取消当前 slot 的优先改动？", "Remove the selected slot from preferred edits?")))
            registry.UnprotectSlot(candidate);
        if (this.DrawConfirmPopup("ConfirmUnpreferSelectedBgPartResource", T("确认取消同款资源优先改动？", "Remove this resource from preferred edits?")))
            registry.UnprotectResourcePath(candidate.ResourcePath);
    }

    private void DrawBgPartPreferredModifyTab()
    {
        var registry = this.localLayoutObjects.PreferredModifyBgParts;
        if (registry == null)
        {
            ImGui.TextWrapped(T("BgPart 优先改动列表服务不可用。", "BgPart preferred edit registry is unavailable."));
            return;
        }

        ImGui.InputText(T("搜索优先改动项", "Search Preferred Edits"), ref this.preferredModifyBgPartSearchText, 256);
        ImGui.SameLine();
        if (ImGui.Button(T("重新扫描 BgPart", "Rescan BgParts")))
            this.layoutProbe.EnumerateInstances(this.runtime.PlayerPosition);

        var candidate = this.GetSelectedBgPart();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), T("当前选中 BgPart 快捷加入", "Add Selected BgPart"));
        if (candidate == null)
        {
            ImGui.TextWrapped(T("当前没有选中 BgPart。可以在画面编辑或本地场景物体中选择一个。", "No BgPart selected. Select one in Scene Editor or Local Scene Objects."));
        }
        else
        {
            ImGui.TextWrapped($"{candidate.ResourcePath} | {FormatVector(candidate.Position)}");
            this.DrawPreferredModifyBgPartControls(candidate);
        }

        ImGui.Separator();
        ImGui.BeginDisabled(registry.PreferredSlots.Count == 0 && registry.PreferredResourcePaths.Count == 0);
        if (ImGui.Button(T("清空优先改动列表", "Clear Preferred Edit List")))
            this.OpenConfirmPopupAtMouse("ConfirmClearPreferredBgParts");
        ImGui.EndDisabled();

        this.DrawPreferredModifyResourcePathTable(registry);
        this.DrawPreferredModifySlotTable(registry);

        if (this.DrawConfirmPopup("ConfirmClearPreferredBgParts", T("确认清空优先改动列表？", "Clear the preferred edit list?")))
            registry.Clear();
    }

    private void DrawPreferredModifyResourcePathTable(PreferredModifyBgPartRegistry registry)
    {
        var rows = registry.PreferredResourcePaths
            .Where(item => this.MatchesPreferredModifySearch(item.ResourcePath, item.Note, item.TerritoryType.ToString()))
            .ToList();
        ImGui.TextWrapped(T($"resourcePath 优先改动：显示 {rows.Count}/{registry.PreferredResourcePaths.Count}", $"Preferred resources: {rows.Count}/{registry.PreferredResourcePaths.Count}"));
        if (!ImGui.BeginTable("PreferredModifyBgPartResourcePaths", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 180f)))
            return;

        ImGui.TableSetupColumn(T("范围", "Scope"));
        ImGui.TableSetupColumn(T("地图", "Territory"));
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn(T("备注", "Note"));
        ImGui.TableSetupColumn(T("操作", "Actions"));
        ImGui.TableHeadersRow();

        var removeRequested = false;
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
            if (ImGui.Button(T("移除", "Remove")))
            {
                this.pendingPreferredResourcePathRemove = item;
                removeRequested = true;
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeRequested)
            this.OpenConfirmPopupAtMouse("ConfirmRemovePreferredResourcePath");
        if (this.DrawConfirmPopup("ConfirmRemovePreferredResourcePath", T("确认移除此优先资源？", "Remove this preferred resource?")))
        {
            registry.RemoveResourcePathEntry(this.pendingPreferredResourcePathRemove);
            this.pendingPreferredResourcePathRemove = null;
        }
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
        ImGui.TextWrapped(T($"slot 优先改动：显示 {rows.Count}/{registry.PreferredSlots.Count}", $"Preferred slots: {rows.Count}/{registry.PreferredSlots.Count}"));
        if (!ImGui.BeginTable("PreferredModifyBgPartSlots", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 260f)))
            return;

        ImGui.TableSetupColumn(T("选择", "Select"), ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn(T("地图", "Territory"));
        ImGui.TableSetupColumn(T("来源", "Source"));
        ImGui.TableSetupColumn("resourcePath");
        ImGui.TableSetupColumn(T("位置", "Position"));
        ImGui.TableSetupColumn(T("备注", "Note"));
        ImGui.TableSetupColumn(T("操作", "Actions"));
        ImGui.TableHeadersRow();

        var removeRequested = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            ImGui.TableNextRow();
            ImGui.PushID($"preferred-slot-{i}-{item.LayoutInstanceAddress}-{item.StableKey}");
            ImGui.TableSetColumnIndex(0);
            var resolved = this.ResolveBgPartSlot(item.LayoutInstanceAddress, item.StableKey, item.ResourcePath, ToVector3(item.OriginalPosition));
            var selected = resolved != null && string.Equals(this.selectedBgPartAddress, resolved.Address, StringComparison.OrdinalIgnoreCase);
            ImGui.BeginDisabled(resolved == null);
            if (ImGui.RadioButton("##SelectPreferredSlot", selected) && resolved != null)
                this.SelectBgPartCandidate(resolved);
            ImGui.EndDisabled();
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.TerritoryType == 0 ? "all" : item.TerritoryType.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item.SourceType);
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(item.ResourcePath);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(FormatVector(ToVector3(item.OriginalPosition)));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextWrapped(item.Note);
            ImGui.TableSetColumnIndex(6);
            if (ImGui.Button(T("移除", "Remove")))
            {
                this.pendingPreferredSlotRemove = item;
                removeRequested = true;
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (removeRequested)
            this.OpenConfirmPopupAtMouse("ConfirmRemovePreferredSlot");
        if (this.DrawConfirmPopup("ConfirmRemovePreferredSlot", T("确认移除此优先 slot？", "Remove this preferred slot?")))
        {
            registry.RemoveSlotEntry(this.pendingPreferredSlotRemove);
            this.pendingPreferredSlotRemove = null;
        }
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
        {
            ImGui.TextDisabled(T("请选择一个复制体。", "Select a copy instance."));
            return;
        }

        if (!string.Equals(this.lastWorldTransformReadLocalLayoutObjectId, selected.Id, StringComparison.Ordinal))
        {
            this.localLayoutObjects.RefreshWorldTransform(selected.Id);
            this.lastWorldTransformReadLocalLayoutObjectId = selected.Id;
        }

        var fullLayoutNeedsConfirmation = this.sceneEditor.IsBgPartCollisionConfirmationRequired(SceneEditableKind.LocalBgPart);
        var disabled = this.localLayoutObjects.IsBusy || !this.realNpcSpawn.EnableUnsafeNativeWrites || selected.IsDuplicate || selected.IsRestored || selected.IsRenderInvalid || fullLayoutNeedsConfirmation;

        DrawYellowSectionLabel("选中实例", "Selected Instance");
        ImGui.SameLine();
        if (ImGui.SmallButton(T("复制", "Copy") + "##LocalLayoutTransformCopy"))
        {
            ImGui.SetClipboardText(FormatFullTransformClipboard("YourcraftLocalSceneTransform", selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale));
            this.mapPresetStatus = T("已复制场景物体 Transform。", "Local scene object transform copied.");
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(disabled);
        if (ImGui.SmallButton(T("粘贴", "Paste") + "##LocalLayoutTransformPaste"))
        {
            if (TryParseFullTransformClipboard(ImGui.GetClipboardText(), out var pastedPosition, out var pastedRotation, out var pastedScale))
            {
                selected.CurrentPosition = pastedPosition;
                selected.CurrentRotationEuler = pastedRotation;
                selected.CurrentScale = pastedScale;
                this.sceneEditor.ApplyWorldTransform(SceneEditableKind.LocalBgPart, selected.Id, WorldTransform.FromEuler(selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale));
                this.mapPresetStatus = T("已粘贴并应用场景物体 Transform。", "Local scene object transform pasted and applied.");
            }
            else
            {
                this.mapPresetStatus = T("剪贴板中没有可识别的 Transform。", "Clipboard does not contain a recognized transform.");
            }
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped(T($"当前模型：{selected.CurrentResourcePath}", $"Current Model: {selected.CurrentResourcePath}"));
        ImGui.TextWrapped(T($"原始模型：{selected.OriginalModelResourcePath}", $"Original Model: {selected.OriginalModelResourcePath}"));
        if (!string.IsNullOrWhiteSpace(selected.LastError))
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), T($"错误：{selected.LastError}", $"Error: {selected.LastError}"));
        if (selected.IsRenderInvalid)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), T("当前实例不可写，请恢复或重新创建。", "This instance is not writable. Restore or recreate it."));
        if (fullLayoutNeedsConfirmation)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f), T("碰撞体编辑需要先在左侧完成二次确认。", "Collision editing requires confirmation on the left."));

        ImGui.Spacing();
        ImGui.BeginDisabled(disabled);
        var transformChanged = false;
        var editPosition = selected.CurrentPosition;
        transformChanged |= DrawSmallFloatStepper(T("X", "X"), "LocalLayoutPosX", ref editPosition.X, 0.2f);
        ImGui.SameLine(0f, 12f);
        transformChanged |= DrawSmallFloatStepper(T("Y", "Y"), "LocalLayoutPosY", ref editPosition.Y, 0.2f);
        ImGui.SameLine(0f, 12f);
        transformChanged |= DrawSmallFloatStepper(T("Z", "Z"), "LocalLayoutPosZ", ref editPosition.Z, 0.2f);
        ImGui.SameLine(0f, 12f);
        if (ImGui.Button(T("重置位移", "Reset Position")))
            this.OpenConfirmPopupAtMouse("ConfirmResetLocalLayoutPosition");
        if (transformChanged)
            selected.CurrentPosition = editPosition;

        var rotationDegrees = new Vector3(RadiansToDegrees(selected.CurrentRotationEuler.X), RadiansToDegrees(selected.CurrentRotationEuler.Y), RadiansToDegrees(selected.CurrentRotationEuler.Z));
        var rotationChanged = false;
        rotationChanged |= DrawSmallFloatStepper(T("X", "X"), "LocalLayoutRotX", ref rotationDegrees.X, 0.2f);
        ImGui.SameLine(0f, 12f);
        rotationChanged |= DrawSmallFloatStepper(T("Y", "Y"), "LocalLayoutRotY", ref rotationDegrees.Y, 0.2f);
        ImGui.SameLine(0f, 12f);
        rotationChanged |= DrawSmallFloatStepper(T("Z", "Z"), "LocalLayoutRotZ", ref rotationDegrees.Z, 0.2f);
        ImGui.SameLine(0f, 12f);
        if (ImGui.Button(T("重置旋转", "Reset Rotation")))
            this.OpenConfirmPopupAtMouse("ConfirmResetLocalLayoutRotation");
        if (rotationChanged)
        {
            selected.CurrentRotationEuler = DegreesVectorToRadians(rotationDegrees);
            transformChanged = true;
        }

        var editScale = selected.CurrentScale;
        var scaleChanged = false;
        scaleChanged |= DrawSmallFloatStepper(T("X", "X"), "LocalLayoutScaleX", ref editScale.X, 0.2f, 0.01f);
        ImGui.SameLine(0f, 12f);
        scaleChanged |= DrawSmallFloatStepper(T("Y", "Y"), "LocalLayoutScaleY", ref editScale.Y, 0.2f, 0.01f);
        ImGui.SameLine(0f, 12f);
        scaleChanged |= DrawSmallFloatStepper(T("Z", "Z"), "LocalLayoutScaleZ", ref editScale.Z, 0.2f, 0.01f);
        ImGui.SameLine(0f, 12f);
        if (ImGui.Button(T("重置缩放", "Reset Scale")))
            this.OpenConfirmPopupAtMouse("ConfirmResetLocalLayoutScale");
        if (scaleChanged)
        {
            selected.CurrentScale = Vector3.Max(editScale, new Vector3(0.01f));
            transformChanged = true;
        }

        if (transformChanged)
            this.sceneEditor.ApplyWorldTransform(SceneEditableKind.LocalBgPart, selected.Id, WorldTransform.FromEuler(selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale));
        ImGui.EndDisabled();

        if (this.DrawConfirmPopup("ConfirmResetLocalLayoutPosition", T("确认重置位移？", "Reset position?")))
            this.localLayoutObjects.ResetPosition(selected.Id);
        if (this.DrawConfirmPopup("ConfirmResetLocalLayoutRotation", T("确认重置旋转？", "Reset rotation?")))
            this.localLayoutObjects.ResetRotation(selected.Id);
        if (this.DrawConfirmPopup("ConfirmResetLocalLayoutScale", T("确认重置缩放？", "Reset scale?")))
            this.localLayoutObjects.ResetScale(selected.Id);

        ImGui.Spacing();
        EditString("mdl path", selected.CustomModelPath, 512, value => selected.CustomModelPath = value);
        ImGui.BeginDisabled(disabled);
        if (ImGui.Button(T("把模型放到玩家脚下", "Put Model At Player Feet")) && this.runtime.PlayerPosition.HasValue)
        {
            selected.CurrentPosition = this.runtime.PlayerPosition.Value;
            this.sceneEditor.ApplyWorldTransform(SceneEditableKind.LocalBgPart, selected.Id, WorldTransform.FromEuler(selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale));
        }
        ImGui.SameLine();
        if (ImGui.Button(T("应用 mdl path", "Apply MDL Path")))
        {
            if (this.sceneEditor.ApplyWorldTransform(SceneEditableKind.LocalBgPart, selected.Id, WorldTransform.FromEuler(selected.CurrentPosition, selected.CurrentRotationEuler, selected.CurrentScale)))
                this.localLayoutObjects.ApplyMdlPath(selected.Id, selected.CustomModelPath, this.FilteredBgParts(), this.realNpcSpawn.EnableUnsafeNativeWrites, this.confirmFullLayoutCollisionMode);
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.BeginDisabled(this.localLayoutObjects.IsBusy);
        if (ImGui.Button(T("删除实例", "Delete Instance")))
            this.OpenConfirmPopupAtMouse("ConfirmDeleteLocalLayoutInstance");
        ImGui.EndDisabled();

        if (this.DrawConfirmPopup("ConfirmDeleteLocalLayoutInstance", T("确认删除此复制体？", "Delete this copy instance?")))
        {
            this.localLayoutObjects.Delete(selected.Id);
            this.sceneEditor.ForgetLocalBgPartRecord(selected.Id);
            this.selectedLocalLayoutObjectId = string.Empty;
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
        }
    }

    private void DrawLocalLights()
    {
        var unsafeEnabled = this.realNpcSpawn.EnableUnsafeNativeWrites;
        if (!unsafeEnabled)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), T("Native 写入未开启，不能创建或修改灯光。", "Native writes are disabled; lights cannot be created or edited."));

        if (!unsafeEnabled)
            ImGui.BeginDisabled();

        if (ImGui.Button(T("创建点光", "Create Point Light")))
            this.CreateLocalLight(LocalLightKind.Point);

        ImGui.SameLine();
        if (ImGui.Button(T("创建聚焦光", "Create Spot Light")))
            this.CreateLocalLight(LocalLightKind.Spot);

        ImGui.SameLine();
        if (ImGui.Button(T("创建面光", "Create Area Light")))
            this.CreateLocalLight(LocalLightKind.Area);

        if (!unsafeEnabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(T("删除当前地图灯光", "Delete Current Map Lights")))
            this.OpenConfirmPopupAtMouse("ConfirmDeleteCurrentMapLights");

        if (this.DrawConfirmPopup("ConfirmDeleteCurrentMapLights", T("确认删除当前地图的全部灯光？", "Delete all lights on the current map?")))
        {
            foreach (var light in this.CurrentTerritoryLights().ToList())
                this.localLights.RequestDelete(light.Id);
            this.selectedLocalLightId = string.Empty;
            this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
        }

        ImGui.Separator();

        var lights = this.CurrentTerritoryLights();
        if (lights.Count == 0)
        {
            ImGui.TextDisabled(T("当前地图还没有本地灯光。", "There are no local lights on this map."));
            return;
        }

        if (!lights.Any(item => string.Equals(item.Id, this.selectedLocalLightId, StringComparison.OrdinalIgnoreCase)))
            this.selectedLocalLightId = lights[0].Id;

        var leftWidth = Math.Min(360f, Math.Max(260f, ImGui.GetContentRegionAvail().X * 0.35f));
        if (ImGui.BeginChild("LocalLightsListPanel", new Vector2(leftWidth, 0f), true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (ImGui.BeginTable("LocalLightsListTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 0f)))
            {
                ImGui.TableSetupColumn(T("启用", "Enabled"), ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn(T("隐藏", "Hidden"), ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn(T("名称", "Name"));
                ImGui.TableSetupColumn(T("类型", "Kind"), ImGuiTableColumnFlags.WidthFixed, 72f);
                ImGui.TableHeadersRow();
                foreach (var light in lights)
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(light.Id);
                    ImGui.TableSetColumnIndex(0);
                    var enabled = light.Enabled;
                    if (ImGui.Checkbox("##LocalLightEnabled", ref enabled))
                        this.localLights.RequestSetEnabled(light.Id, enabled);
                    ImGui.TableSetColumnIndex(1);
                    var hidden = light.Hidden;
                    if (ImGui.Checkbox("##LocalLightHidden", ref hidden))
                    {
                        light.Hidden = hidden;
                        this.localLights.RequestApply(light.Id);
                    }
                    ImGui.TableSetColumnIndex(2);
                    var selected = string.Equals(light.Id, this.selectedLocalLightId, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable($"{light.Name}##SelectLocalLight", selected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        this.selectedLocalLightId = light.Id;
                        this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalLight, light.Id);
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(DisplayLocalLightKind(light.LightKind));
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        var selectedLight = this.localLights.GetById(this.selectedLocalLightId);
        if (selectedLight == null)
        {
            ImGui.TextDisabled(T("未选择灯光。", "No light selected."));
            return;
        }

        if (ImGui.BeginChild("LocalLightsEditPanel", Vector2.Zero, true, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            DrawYellowSectionLabel("灯光实例", "Light Instance");
            if (!string.IsNullOrWhiteSpace(selectedLight.LastError))
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), T($"错误：{selectedLight.LastError}", $"Error: {selectedLight.LastError}"));

            var lightName = selectedLight.Name;
            if (ImGui.InputText(T("名称##LocalLightName", "Name##LocalLightName"), ref lightName, 128))
            {
                selectedLight.Name = lightName;
                this.localLights.RequestApply(selectedLight.Id);
            }

            if (DrawLocalLightKindCombo(selectedLight))
                this.localLights.RequestApply(selectedLight.Id);
            if (DrawLocalLightFalloffCombo(selectedLight))
                this.localLights.RequestApply(selectedLight.Id);

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.28f, 1f), "Transform");
            ImGui.SameLine();
            if (ImGui.SmallButton(T("复制", "Copy") + "##LocalLightTransformCopy"))
            {
                ImGui.SetClipboardText(FormatFullTransformClipboard("YourcraftLightTransform", selectedLight.Position, selectedLight.Rotation, selectedLight.Scale));
                this.mapPresetStatus = T("已复制灯光 Transform。", "Light transform copied.");
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(!unsafeEnabled);
            if (ImGui.SmallButton(T("粘贴", "Paste") + "##LocalLightTransformPaste"))
            {
                if (TryParseFullTransformClipboard(ImGui.GetClipboardText(), out var pastedPosition, out var pastedRotation, out var pastedScale))
                {
                    selectedLight.Position = pastedPosition;
                    selectedLight.Rotation = pastedRotation;
                    selectedLight.Scale = pastedScale;
                    this.localLights.RequestApply(selectedLight.Id);
                    this.mapPresetStatus = T("已粘贴并应用灯光 Transform。", "Light transform pasted and applied.");
                }
                else
                {
                    this.mapPresetStatus = T("剪贴板中没有可识别的 Transform。", "Clipboard does not contain a recognized transform.");
                }
            }
            ImGui.EndDisabled();
            var transformChanged = false;
            ImGui.BeginDisabled(!unsafeEnabled);
            var position = selectedLight.Position;
            if (DrawVector3StepperRow(T("位移", "Position"), "LocalLightPosition", ref position, 0.2f))
            {
                selectedLight.Position = position;
                transformChanged = true;
            }
            var rotationDegrees = RadiansVectorToDegrees(selectedLight.Rotation);
            if (DrawVector3StepperRow(T("旋转", "Rotation"), "LocalLightRotation", ref rotationDegrees, 0.2f))
            {
                selectedLight.Rotation = DegreesVectorToRadians(rotationDegrees);
                transformChanged = true;
            }
            var scale = selectedLight.Scale;
            if (DrawVector3StepperRow(T("缩放", "Scale"), "LocalLightScale", ref scale, 0.2f, 0.01f))
            {
                selectedLight.Scale = Vector3.Max(scale, new Vector3(0.01f));
                transformChanged = true;
            }
            if (transformChanged)
                this.localLights.RequestApply(selectedLight.Id);
            ImGui.EndDisabled();

            if (ImGui.Button(T("移动到玩家位置", "Move To Player")))
            {
                selectedLight.Position = this.runtime.PlayerPosition ?? selectedLight.Position;
                this.localLights.RequestApply(selectedLight.Id);
            }

            ImGui.Separator();
            DrawYellowSectionLabel("灯光参数", "Light");
            ImGui.SameLine();
            if (ImGui.SmallButton(T("复制", "Copy") + "##LocalLightParamsCopy"))
            {
                ImGui.SetClipboardText(FormatLightParamsClipboard(selectedLight));
                this.mapPresetStatus = T("已复制灯光参数。", "Light parameters copied.");
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(!unsafeEnabled);
            if (ImGui.SmallButton(T("粘贴", "Paste") + "##LocalLightParamsPaste"))
            {
                if (TryApplyLightParamsClipboard(ImGui.GetClipboardText(), selectedLight))
                {
                    this.localLights.RequestApply(selectedLight.Id);
                    this.mapPresetStatus = T("已粘贴并应用灯光参数。", "Light parameters pasted and applied.");
                }
                else
                {
                    this.mapPresetStatus = T("剪贴板中没有可识别的灯光参数。", "Clipboard does not contain recognized light parameters.");
                }
            }
            ImGui.EndDisabled();
            var parameterChanged = false;
            var color = selectedLight.ColorRgb;
            if (ImGui.ColorEdit3(T("颜色 RGB", "Color RGB"), ref color))
            {
                selectedLight.ColorRgb = color;
                parameterChanged = true;
            }
            var intensity = selectedLight.Intensity;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("强度", "Intensity"), ref intensity))
            {
                selectedLight.Intensity = MathF.Max(0f, intensity);
                parameterChanged = true;
            }
            var range = selectedLight.Range;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("范围", "Range"), ref range))
            {
                selectedLight.Range = MathF.Max(0f, range);
                parameterChanged = true;
            }
            var falloff = selectedLight.Falloff;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("衰减", "Falloff"), ref falloff))
            {
                selectedLight.Falloff = MathF.Max(0f, falloff);
                parameterChanged = true;
            }
            var spotAngle = selectedLight.LightAngle;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("聚焦角度", "Spot Angle"), ref spotAngle))
            {
                selectedLight.LightAngle = Math.Clamp(spotAngle, 0f, 90f);
                parameterChanged = true;
            }
            var falloffAngle = selectedLight.FalloffAngle;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputFloat(T("衰减角度", "Falloff Angle"), ref falloffAngle))
            {
                selectedLight.FalloffAngle = Math.Clamp(falloffAngle, 0f, 90f);
                parameterChanged = true;
            }
            var area = new Vector2(selectedLight.AreaAngleX, selectedLight.AreaAngleY);
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputFloat2(T("面光 X/Y", "Area X/Y"), ref area))
            {
                selectedLight.AreaAngleX = Math.Clamp(area.X, 0f, 90f);
                selectedLight.AreaAngleY = Math.Clamp(area.Y, 0f, 90f);
                parameterChanged = true;
            }

            if (ImGui.TreeNode(T("阴影 / 高级参数", "Shadows / Advanced")))
            {
                var specular = selectedLight.EnableSpecular;
                if (ImGui.Checkbox(T("高光", "Specular Highlights"), ref specular))
                {
                    selectedLight.EnableSpecular = specular;
                    parameterChanged = true;
                }
                var shadows = selectedLight.EnableDynamicShadows;
                if (ImGui.Checkbox(T("动态阴影", "Dynamic Shadows"), ref shadows))
                {
                    selectedLight.EnableDynamicShadows = shadows;
                    parameterChanged = true;
                }
                ImGui.TreePop();
            }

            if (parameterChanged)
                this.localLights.RequestApply(selectedLight.Id);

            ImGui.Separator();
            if (!unsafeEnabled)
                ImGui.BeginDisabled();

            if (ImGui.Button(T("删除选中", "Delete Selected")))
                this.OpenConfirmPopupAtMouse("ConfirmDeleteSelectedLocalLight");

            if (this.DrawConfirmPopup("ConfirmDeleteSelectedLocalLight", T("确认删除选中的灯光？", "Delete the selected light?")))
            {
                this.localLights.RequestDelete(selectedLight.Id);
                this.selectedLocalLightId = string.Empty;
                this.sceneEditorSelection.Clear(SceneEditorSelectionSource.MainUi);
            }

            if (!unsafeEnabled)
                ImGui.EndDisabled();
        }
        ImGui.EndChild();
    }

    private IReadOnlyList<LocalLightInstance> CurrentTerritoryLights()
    {
        var territory = this.runtime.TerritoryType;
        return territory == 0
            ? []
            : this.localLights.Instances.Where(item => item.TerritoryId == territory).ToList();
    }

    private void CreateLocalLight(LocalLightKind kind)
    {
        var instance = this.localLights.Create(kind, this.NextLocalLightName(kind), this.runtime.PlayerPosition ?? Vector3.Zero, Vector3.Zero, Vector3.One);
        this.selectedLocalLightId = instance.Id;
        this.SelectSceneEditableFromMainUi(SceneEditableKind.LocalLight, instance.Id);
    }

    private string NextLocalLightName(LocalLightKind kind)
    {
        var number = this.CurrentTerritoryLights().Count(item => item.LightKind == kind) + 1;
        var prefix = DisplayLocalLightKind(kind);
        return Localization.IsEnglish ? $"{prefix} {number}" : $"{prefix}{number}";
    }

    private static bool DrawLocalLightKindCombo(LocalLightInstance light)
    {
        if (!ImGui.BeginCombo(T("类型", "Kind"), DisplayLocalLightKind(light.LightKind)))
            return false;

        var changed = false;
        foreach (var kind in Enum.GetValues<LocalLightKind>())
        {
            var selected = light.LightKind == kind;
            if (ImGui.Selectable(DisplayLocalLightKind(kind), selected))
            {
                light.LightKind = kind;
                changed = true;
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static bool DrawLocalLightFalloffCombo(LocalLightInstance light)
    {
        if (!ImGui.BeginCombo(T("衰减类型", "Falloff Type"), DisplayLocalLightFalloff(light.FalloffType)))
            return false;

        var changed = false;
        foreach (var falloff in Enum.GetValues<LocalLightFalloffType>())
        {
            var selected = light.FalloffType == falloff;
            if (ImGui.Selectable(DisplayLocalLightFalloff(falloff), selected))
            {
                light.FalloffType = falloff;
                changed = true;
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static string DisplayLocalLightKind(LocalLightKind kind)
        => kind switch
        {
            LocalLightKind.Point => T("点光", "Point Light"),
            LocalLightKind.Spot => T("聚焦光", "Spot Light"),
            LocalLightKind.Area => T("面光", "Area Light"),
            LocalLightKind.Directional => T("方向光", "Directional Light"),
            _ => kind.ToString(),
        };

    private static string DisplayLocalLightFalloff(LocalLightFalloffType falloff)
        => falloff switch
        {
            LocalLightFalloffType.Linear => T("线性", "Linear"),
            LocalLightFalloffType.Quadratic => T("二次", "Quadratic"),
            LocalLightFalloffType.Cubic => T("三次", "Cubic"),
            _ => falloff.ToString(),
        };

    private static string DisplaySceneEditorGizmoMode(SceneEditorGizmoMode mode)
        => mode switch
        {
            SceneEditorGizmoMode.Select => T("选择", "Select"),
            SceneEditorGizmoMode.Move => T("位移", "Move"),
            SceneEditorGizmoMode.Rotate => T("旋转", "Rotate"),
            SceneEditorGizmoMode.Scale => T("缩放", "Scale"),
            _ => mode.ToString(),
        };

    private static string DisplaySceneEditableKind(SceneEditableKind kind)
        => kind switch
        {
            SceneEditableKind.LocalActor => T("Actor", "Actor"),
            SceneEditableKind.LocalBgPart => T("本地 BgPart", "Local BgPart"),
            SceneEditableKind.LocalLight => T("本地灯光", "Local Light"),
            SceneEditableKind.NativeActor => T("原生 Actor", "Native Actor"),
            SceneEditableKind.EventNpc => T("事件 NPC", "Event NPC"),
            SceneEditableKind.NativeBgPart => T("原生 BgPart", "Native BgPart"),
            SceneEditableKind.NativeLight => T("原生灯光", "Native Light"),
            SceneEditableKind.Player => T("玩家", "Player"),
            _ => kind.ToString(),
        };

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
            var fullLayoutBlocked = this.localLayoutFullCollisionMode && !this.confirmFullLayoutCollisionMode;
            ImGui.BeginDisabled(templateSlot == null || !this.realNpcSpawn.EnableUnsafeNativeWrites || this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive || fullLayoutBlocked);
            if (ImGui.Button("创建 1 个复制体"))
                this.TryCreateSingleBgPartCopy(templateSlot, "BgPartSlotPool");
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

    private LayoutProbeInstance? ResolveBgPartSlot(string? address, string? stableKey, string? resourcePath, Vector3 position)
        => this.layoutProbe.Instances.FirstOrDefault(instance =>
            string.Equals(instance.Type, "BgPart", StringComparison.Ordinal) &&
            ((!string.IsNullOrWhiteSpace(address) && string.Equals(instance.Address, address, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(stableKey) && (string.Equals(instance.Key, stableKey, StringComparison.OrdinalIgnoreCase) || string.Equals(instance.ParentKey, stableKey, StringComparison.OrdinalIgnoreCase))) ||
             (!string.IsNullOrWhiteSpace(resourcePath) && string.Equals(instance.ResourcePath, resourcePath, StringComparison.OrdinalIgnoreCase) && Vector3.Distance(instance.Position, position) <= 0.25f)));

    private void SelectBgPartCandidate(LayoutProbeInstance candidate)
    {
        this.selectedBgPartAddress = candidate.Address;
        this.layerDump.SelectReusableCandidate(candidate);
        var editable = this.sceneEditor.GetEditables().FirstOrDefault(item =>
            item.Kind == SceneEditableKind.NativeBgPart &&
            item.LayoutProbe != null &&
            string.Equals(item.LayoutProbe.Address, candidate.Address, StringComparison.OrdinalIgnoreCase));
        if (editable != null)
            this.SelectSceneEditableFromMainUi(SceneEditableKind.NativeBgPart, editable.RuntimeId);
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

    private static bool InputVector3(string label, ref Vector3 vector)
    {
        var changed = false;
        var x = vector.X;
        var y = vector.Y;
        var z = vector.Z;
        if (ImGui.InputFloat($"{label} X", ref x))
            changed = true;
        if (ImGui.InputFloat($"{label} Y", ref y))
            changed = true;
        if (ImGui.InputFloat($"{label} Z", ref z))
            changed = true;
        if (changed)
            vector = new Vector3(x, y, z);
        return changed;
    }

    private static bool HideLegacyCreateManyUi()
        => true;

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

    private static string DisplayActorSpawnKind(ActorSpawnKind kind)
        => kind switch
        {
            ActorSpawnKind.Unknown => T("未知", "Unknown"),
            ActorSpawnKind.Character => T("角色", "Character"),
            ActorSpawnKind.Demihuman => T("半人形", "Demihuman"),
            ActorSpawnKind.Mount => T("坐骑", "Mount"),
            ActorSpawnKind.Minion => T("宠物", "Minion"),
            ActorSpawnKind.Unsupported => T("不支持", "Unsupported"),
            _ => kind.ToString(),
        };

    private static string DisplayGameNpcCatalogKind(GameNpcCatalogKind kind)
        => kind switch
        {
            GameNpcCatalogKind.ENpc => "ENpc",
            GameNpcCatalogKind.BNpc => "BNpc",
            GameNpcCatalogKind.ModelChara => "ModelChara",
            GameNpcCatalogKind.Mount => T("坐骑", "Mount"),
            GameNpcCatalogKind.Companion => T("宠物", "Minion"),
            GameNpcCatalogKind.Unknown => T("未知", "Unknown"),
            _ => kind.ToString(),
        };

    private static string DisplayAppearanceSourceKind(ActorAppearanceSourceKind source)
        => source switch
        {
            ActorAppearanceSourceKind.None => T("无", "None"),
            ActorAppearanceSourceKind.CurrentPlayer => T("当前玩家", "Current Player"),
            ActorAppearanceSourceKind.GlamourerDesign => "Glamourer Design",
            ActorAppearanceSourceKind.GlamourerNpc => "Glamourer NPC",
            ActorAppearanceSourceKind.ManualSnapshot => T("手动快照", "Manual Snapshot"),
            ActorAppearanceSourceKind.GameNpc => T("游戏 NPC", "Game NPC"),
            ActorAppearanceSourceKind.Local => T("本地", "Local"),
            _ => source.ToString(),
        };

    private static string DisplayAppearanceSourceKind(string source)
        => Enum.TryParse<ActorAppearanceSourceKind>(source, true, out var parsed)
            ? DisplayAppearanceSourceKind(parsed)
            : string.IsNullOrWhiteSpace(source) ? T("无", "None") : source;

    private static string DisplayActorActionStepKind(ActorActionStepKind kind)
        => kind switch
        {
            ActorActionStepKind.Action => T("动作", "Action"),
            ActorActionStepKind.Spawn => "Spawn",
            ActorActionStepKind.Despawn => "Despawn",
            ActorActionStepKind.Wait => T("等待", "Wait"),
            ActorActionStepKind.Move => T("移动", "Move"),
            ActorActionStepKind.ResetToDefaultAction => T("恢复默认动作", "Reset To Default Action"),
            ActorActionStepKind.Idle => "Idle",
            _ => kind.ToString(),
        };

    private static string DisplayExpressionLayer(ActorExpressionLayer layer)
        => layer switch
        {
            ActorExpressionLayer.None => T("无", "None"),
            ActorExpressionLayer.Facial => T("面部", "Facial"),
            ActorExpressionLayer.UpperBodyBlend => T("上半身混合", "Upper Body Blend"),
            ActorExpressionLayer.FullBlend => T("全身混合", "Full Blend"),
            _ => layer.ToString(),
        };

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

    private sealed class MapModificationPreset
    {
        public int SchemaVersion { get; set; } = 1;

        public uint TerritoryId { get; set; }

        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

        public string PluginName { get; set; } = "Yourcraft";

        public List<PersistentActorConfig> ActorConfigs { get; set; } = [];

        public List<CustomNpc> Npcs { get; set; } = [];

        public List<SceneEditorLocalBgPartRecord> LocalBgParts { get; set; } = [];

        public List<SceneEditorLocalActorRecord> LocalActors { get; set; } = [];

        public List<SceneEditorNativeModificationRecord> NativeModifications { get; set; } = [];

        public List<LocalLightInstance> LocalLights { get; set; } = [];

        public List<ProtectedBgPartSlot> ProtectedSlots { get; set; } = [];

        public List<ProtectedBgPartResourcePath> ProtectedResourcePaths { get; set; } = [];

        public List<PreferredModifyBgPartSlot> PreferredSlots { get; set; } = [];

        public List<PreferredModifyBgPartResourcePath> PreferredResourcePaths { get; set; } = [];
    }
}


