using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class GameNpcCatalogService
{
    private readonly ITargetManager targetManager;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<GameNpcCatalogEntry> entries = [];

    public GameNpcCatalogService(ITargetManager targetManager, IClientState clientState, IDataManager dataManager, IPluginLog log)
    {
        this.targetManager = targetManager;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.log = log;
        this.ReloadCatalog();
    }

    public TargetGameObjectInfo? CurrentTarget => CreateTargetInfo(this.targetManager.Target, this.clientState.TerritoryType);

    public IReadOnlyList<GameNpcCatalogEntry> Entries => this.entries;

    public int ENpcCount { get; private set; }

    public int BNpcCount { get; private set; }

    public int ModelCharaCount { get; private set; }

    public int MountCount { get; private set; }

    public int CompanionCount { get; private set; }

    public string LastLoadMessage { get; private set; } = string.Empty;

    public void ReloadCatalog()
    {
        this.entries.Clear();
        this.ENpcCount = 0;
        this.BNpcCount = 0;
        this.ModelCharaCount = 0;
        this.MountCount = 0;
        this.CompanionCount = 0;

        this.TryReadENpcResident();
        this.TryReadBNpcName();
        this.TryReadModelChara();
        this.TryReadMount();
        this.TryReadCompanion();

        this.LastLoadMessage = $"已读取 ENpc {this.ENpcCount} 条，BNpc {this.BNpcCount} 条，ModelChara {this.ModelCharaCount} 条。";
        this.LastLoadMessage = $"Loaded GameNpc catalog. ENpc {this.ENpcCount}, BNpc {this.BNpcCount}, ModelChara {this.ModelCharaCount}, Mount {this.MountCount}, Minion {this.CompanionCount}.";
        this.log.Information("Loaded GameNpc catalog. ENpc={ENpcCount}, BNpc={BNpcCount}, ModelChara={ModelCharaCount}, Mount={MountCount}, Companion={CompanionCount}", this.ENpcCount, this.BNpcCount, this.ModelCharaCount, this.MountCount, this.CompanionCount);
    }

    public IReadOnlyList<GameNpcCatalogEntry> Search(string query, int maxResults = 80)
    {
        var trimmed = query.Trim();
        IEnumerable<GameNpcCatalogEntry> source = this.entries;

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            source = source.Where(entry =>
                entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                entry.RowId.ToString().Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                entry.ModelCharaId.ToString().Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                entry.DebugInfo.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return source
            .OrderBy(entry => string.IsNullOrWhiteSpace(entry.Name))
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RowId)
            .Take(maxResults)
            .ToList();
    }

    public void ApplyCatalogEntryToNpc(CustomNpc npc, GameNpcCatalogEntry entry)
    {
        npc.Appearance.SourceType = CustomNpcAppearanceSourceType.GameNpc;
        npc.Appearance.DisplayName = entry.Name;
        npc.Appearance.GameNpcName = entry.Name;
        npc.Appearance.GameNpcBaseId = entry.ResidentRowId != 0 ? entry.ResidentRowId : entry.RowId;
        npc.Appearance.GameNpcKind = ToGameNpcKind(entry.Kind);
        npc.Appearance.GameNpcModelId = entry.ModelCharaId;
        npc.Appearance.GameNpcCustomizeId = entry.CustomizeId ?? 0;
        npc.Appearance.Notes = $"{entry.DebugInfo}\n{entry.RawDebugInfo}";
    }

    public bool SaveCurrentTargetAsGameNpcAppearance(CustomNpc npc, bool moveNpcToPlayer, Vector3? playerPosition, out string message)
    {
        var target = this.CurrentTarget;
        if (target == null)
        {
            message = "当前没有选中目标。";
            return false;
        }

        npc.Appearance.SourceType = CustomNpcAppearanceSourceType.GameNpc;
        npc.Appearance.DisplayName = target.Name;
        npc.Appearance.GameNpcName = target.Name;
        npc.Appearance.GameNpcBaseId = target.DataId;
        npc.Appearance.GameNpcKind = target.GameNpcKind;
        npc.Appearance.Notes = $"从当前目标读取：ObjectKind={target.ObjectKind}, EntityId={target.EntityId}, GameObjectId={target.GameObjectId}";

        if (moveNpcToPlayer && playerPosition != null)
        {
            npc.TerritoryType = (ushort)Math.Clamp((int)target.TerritoryType, 0, ushort.MaxValue);
            npc.Position = new Vector3Data
            {
                X = playerPosition.Value.X,
                Y = playerPosition.Value.Y,
                Z = playerPosition.Value.Z,
            };
        }

        this.log.Information(
            "Saved target as GameNpc appearance for {NpcId}: Name={Name}, ObjectKind={ObjectKind}, DataId={DataId}, EntityId={EntityId}",
            npc.Id,
            target.Name,
            target.ObjectKind,
            target.DataId,
            target.EntityId);

        message = $"已从当前目标读取外观来源：{target.Name} ({target.GameNpcKind}, DataId {target.DataId})";
        return true;
    }

    private void TryReadENpcResident()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<ENpcResident>();
            if (sheet == null)
                return;

            var baseRows = this.BuildRowMap<ENpcBase>();

            foreach (var row in sheet)
            {
                var residentRowId = ReadUInt(row, "RowId");
                baseRows.TryGetValue(residentRowId, out var baseRow);
                var entry = CreateCatalogEntry(row, GameNpcCatalogKind.ENpc, baseRow, residentRowId);
                if (entry == null)
                    continue;

                this.entries.Add(entry);
                this.ENpcCount++;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read ENpcResident sheet for GameNpc catalog");
        }
    }

    private void TryReadBNpcName()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<BNpcName>();
            if (sheet == null)
                return;

            var baseRows = this.BuildRowMap<BNpcBase>();

            foreach (var row in sheet)
            {
                var residentRowId = ReadUInt(row, "RowId");
                baseRows.TryGetValue(residentRowId, out var baseRow);
                var entry = CreateCatalogEntry(row, GameNpcCatalogKind.BNpc, baseRow, residentRowId);
                if (entry == null)
                    continue;

                this.entries.Add(entry);
                this.BNpcCount++;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read BNpcName sheet for GameNpc catalog");
        }
    }

    private void TryReadModelChara()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<ModelChara>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var entry = CreateCatalogEntry(row, GameNpcCatalogKind.ModelChara, null, ReadUInt(row, "RowId"));
                if (entry == null)
                    continue;

                this.entries.Add(entry);
                this.ModelCharaCount++;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read ModelChara sheet for GameNpc catalog");
        }
    }

    private void TryReadMount()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Mount>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var entry = CreateCatalogEntry(row, GameNpcCatalogKind.Mount, null, ReadUInt(row, "RowId"));
                if (entry == null)
                    continue;

                this.entries.Add(entry);
                this.MountCount++;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read Mount sheet for GameNpc catalog");
        }
    }

    private void TryReadCompanion()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Companion>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var entry = CreateCatalogEntry(row, GameNpcCatalogKind.Companion, null, ReadUInt(row, "RowId"));
                if (entry == null)
                    continue;

                this.entries.Add(entry);
                this.CompanionCount++;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read Companion sheet for GameNpc catalog");
        }
    }

    private Dictionary<uint, object> BuildRowMap<T>()
        where T : struct, IExcelRow<T>
    {
        var map = new Dictionary<uint, object>();
        try
        {
            var sheet = this.dataManager.GetExcelSheet<T>();
            foreach (var row in sheet)
            {
                var rowId = ReadUInt(row, "RowId");
                if (rowId != 0)
                    map[rowId] = row;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to build row map for {Type}", typeof(T).Name);
        }

        return map;
    }

    private static GameNpcCatalogEntry? CreateCatalogEntry(object row, GameNpcCatalogKind kind, object? baseRow, uint residentRowId)
    {
        var rowId = ReadUInt(row, "RowId");
        if (rowId == 0)
            return null;

        var name = ReadFirstString(row, "Singular", "Name", "Text", "Model", "Description");
        var modelSource = baseRow ?? row;
        var modelCharaId = kind == GameNpcCatalogKind.ModelChara
            ? rowId
            : ReadFirstUInt(modelSource, "ModelChara", "ModelCharaId", "Model", "ModelId", "Character", "ModelMain");
        if (kind is GameNpcCatalogKind.Mount or GameNpcCatalogKind.Companion && modelCharaId == 0)
            modelCharaId = ReadFirstUInt(modelSource, "ModelCharaRow", "ModelRow", "Model");
        var customizeId = ReadNullableUInt(modelSource, "Customize", "CustomizeId", "CustomizeData", "Base");
        var baseRowId = baseRow == null ? 0 : ReadUInt(baseRow, "RowId");
        var equipmentInfo = ReadEquipmentInfo(modelSource);
        var rawDebugInfo = CreateRawDebugInfo(row, baseRow);
        var debugInfo = CreateDebugInfo(row, kind, residentRowId, baseRowId, modelCharaId, customizeId, equipmentInfo);

        if (string.IsNullOrWhiteSpace(name))
            name = $"{kind} #{rowId}";

        return new GameNpcCatalogEntry
        {
            DisplayName = name,
            SourceKind = kind,
            ResidentRowId = residentRowId,
            BaseRowId = baseRowId,
            Name = name,
            Kind = kind,
            RowId = rowId,
            ModelCharaId = modelCharaId,
            CustomizeId = customizeId,
            Description = $"{kind} RowId {rowId}",
            DebugInfo = debugInfo,
            Race = ReadFirstString(modelSource, "Race"),
            Gender = ReadFirstString(modelSource, "Gender", "Sex"),
            Tribe = ReadFirstString(modelSource, "Tribe"),
            EquipmentInfo = equipmentInfo,
            RawDebugInfo = rawDebugInfo,
        };
    }

    private static string CreateDebugInfo(object row, GameNpcCatalogKind kind, uint residentRowId, uint baseRowId, uint modelCharaId, uint? customizeId, string equipmentInfo)
        => $"{kind} ResidentRowId={residentRowId}, BaseRowId={baseRowId}, ModelCharaId={modelCharaId}, CustomizeId={(customizeId == null ? "无" : customizeId.Value)}, Equipment={equipmentInfo}, RowType={row.GetType().Name}";

    private static string ReadEquipmentInfo(object source)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "Equipment", "Equip", "ModelEquip", "Weapon", "MainHand", "OffHand", "EObj" })
        {
            var value = ReadMember(source, name);
            if (value != null)
                parts.Add($"{name}={value}");
        }

        return parts.Count == 0 ? "无" : string.Join(", ", parts);
    }

    private static string CreateRawDebugInfo(object residentRow, object? baseRow)
    {
        var parts = new List<string> { $"Resident: {DumpPublicMembers(residentRow)}" };
        if (baseRow != null)
            parts.Add($"Base: {DumpPublicMembers(baseRow)}");
        else
            parts.Add("Base: 未找到");

        return string.Join("\n", parts);
    }

    private static string DumpPublicMembers(object source)
    {
        try
        {
            var type = source.GetType();
            var members = new List<string>();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                object? value;
                try { value = property.GetValue(source); }
                catch { continue; }
                members.Add($"{property.Name}={value}");
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object? value;
                try { value = field.GetValue(source); }
                catch { continue; }
                members.Add($"{field.Name}={value}");
            }

            return string.Join("; ", members.Take(80));
        }
        catch
        {
            return "无法读取字段";
        }
    }

    private static TargetGameObjectInfo? CreateTargetInfo(IGameObject? target, uint territoryType)
    {
        if (target == null)
            return null;

        var objectKind = target.ObjectKind.ToString();
        return new TargetGameObjectInfo(
            target.Name.ToString(),
            objectKind,
            target.BaseId,
            target.EntityId,
            ReadOptionalProperty(target, "GameObjectId"),
            target.Position,
            territoryType,
            MapGameNpcKind(objectKind));
    }

    private static string ReadOptionalProperty(object source, string propertyName)
    {
        try
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
            return value?.ToString() ?? "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    private static string ReadFirstString(object source, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadMember(source, memberName);
            if (value == null)
                continue;

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    private static uint ReadFirstUInt(object source, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadUInt(source, memberName);
            if (value != 0)
                return value;
        }

        return 0;
    }

    private static uint? ReadNullableUInt(object source, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadUInt(source, memberName);
            if (value != 0)
                return value;
        }

        return null;
    }

    private static uint ReadUInt(object source, string memberName)
    {
        var value = ReadMember(source, memberName);
        if (value == null)
            return 0;

        if (value is uint uintValue)
            return uintValue;

        if (value is ushort ushortValue)
            return ushortValue;

        if (value is byte byteValue)
            return byteValue;

        if (value is int intValue && intValue >= 0)
            return (uint)intValue;

        if (TryReadRowId(value, out var rowId))
            return rowId;

        if (uint.TryParse(value.ToString(), out var parsed))
            return parsed;

        return 0;
    }

    private static bool TryReadRowId(object value, out uint rowId)
    {
        rowId = 0;
        try
        {
            var type = value.GetType();
            var raw = type.GetProperty("RowId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) ??
                      type.GetField("RowId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
            if (raw is uint u32)
            {
                rowId = u32;
                return rowId != 0;
            }

            if (raw is int i32 && i32 >= 0)
            {
                rowId = (uint)i32;
                return rowId != 0;
            }

            if (raw != null && uint.TryParse(raw.ToString(), out rowId))
                return rowId != 0;
        }
        catch
        {
        }

        return false;
    }

    private static object? ReadMember(object source, string memberName)
    {
        try
        {
            var type = source.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.GetValue(source);

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static GameNpcKind MapGameNpcKind(string objectKind)
    {
        if (objectKind.Contains("EventNpc", StringComparison.OrdinalIgnoreCase))
            return GameNpcKind.ENpc;

        if (objectKind.Contains("BattleNpc", StringComparison.OrdinalIgnoreCase))
            return GameNpcKind.BNpc;

        if (objectKind.Contains("Mount", StringComparison.OrdinalIgnoreCase))
            return GameNpcKind.Mount;

        if (objectKind.Contains("Companion", StringComparison.OrdinalIgnoreCase) ||
            objectKind.Contains("Ornament", StringComparison.OrdinalIgnoreCase))
            return GameNpcKind.Companion;

        if (objectKind.Contains("Monster", StringComparison.OrdinalIgnoreCase))
            return GameNpcKind.Monster;

        return GameNpcKind.Unknown;
    }

    private static GameNpcKind ToGameNpcKind(GameNpcCatalogKind kind)
        => kind switch
        {
            GameNpcCatalogKind.ENpc => GameNpcKind.ENpc,
            GameNpcCatalogKind.BNpc => GameNpcKind.BNpc,
            GameNpcCatalogKind.ModelChara => GameNpcKind.ModelChara,
            GameNpcCatalogKind.Mount => GameNpcKind.Mount,
            GameNpcCatalogKind.Companion => GameNpcKind.Companion,
            _ => GameNpcKind.Unknown,
        };
}

public sealed record TargetGameObjectInfo(
    string Name,
    string ObjectKind,
    uint DataId,
    ulong EntityId,
    string GameObjectId,
    Vector3 Position,
    uint TerritoryType,
    GameNpcKind GameNpcKind);
