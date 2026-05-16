using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class GameNpcAppearanceResolver
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<string, GameNpcAppearanceResolution> cache = new(StringComparer.OrdinalIgnoreCase);

    public GameNpcAppearanceResolver(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public GameNpcAppearanceResolution Resolve(CustomNpcAppearance appearance)
    {
        var cacheKey = BuildCacheKey(appearance);
        if (this.cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = new GameNpcAppearanceResolution
        {
            ResidentRowId = appearance.GameNpcBaseId,
            BaseRowId = appearance.GameNpcKind == GameNpcKind.ModelChara ? 0 : appearance.GameNpcBaseId,
            SourceKind = appearance.GameNpcKind,
        };
        result.Chain.Add($"输入：kind={appearance.GameNpcKind}, rowId={appearance.GameNpcBaseId}, modelCharaId={appearance.GameNpcModelId}, customizeId={appearance.GameNpcCustomizeId}");

        if (appearance.GameNpcModelId > 0)
        {
            result.Success = true;
            result.ModelCharaId = appearance.GameNpcModelId;
            result.Appearance.Kind = GameNpcResolvedAppearanceKind.Monster;
            result.Appearance.ModelCharaId = appearance.GameNpcModelId;
            result.CustomizeId = appearance.GameNpcCustomizeId == 0 ? null : appearance.GameNpcCustomizeId;
            result.Message = $"已解析 ModelCharaId={result.ModelCharaId}。";
            result.Chain.Add(result.Message);
            this.cache[cacheKey] = result;
            return result;
        }

        result = appearance.GameNpcKind switch
        {
            GameNpcKind.ModelChara => this.ResolveModelCharaRow(appearance.GameNpcBaseId, result),
            GameNpcKind.ENpc => this.ResolveENpc(appearance.GameNpcBaseId, result),
            GameNpcKind.BNpc or GameNpcKind.Monster => this.ResolveBNpc(appearance.GameNpcBaseId, result),
            _ => this.Fail(result, "GameNpcKind", $"当前 GameNpcKind={appearance.GameNpcKind} 暂无可解析表。"),
        };
        this.cache[cacheKey] = result;
        return result;
    }

    public bool TryGetCached(CustomNpcAppearance appearance, out GameNpcAppearanceResolution resolution)
        => this.cache.TryGetValue(BuildCacheKey(appearance), out resolution!);

    public void ClearCache()
        => this.cache.Clear();

    private GameNpcAppearanceResolution ResolveENpc(uint rowId, GameNpcAppearanceResolution result)
    {
        result.Chain.Add($"ENpcResident RowId {rowId} 仅用于名称，继续查 ENpcBase。");
        var row = this.FindRow<ENpcBase>(rowId, "ENpcBase", result);
        if (row == null)
            return this.Fail(result, "ENpcBase", $"未找到 ENpcBase RowId={rowId}。");

        result.FoundENpcBase = true;
        result.RawDebugInfo = DumpPublicMembers(row);
        return this.ResolveModelFromRow(row, result, "ENpcBase");
    }

    private GameNpcAppearanceResolution ResolveBNpc(uint rowId, GameNpcAppearanceResolution result)
    {
        result.Chain.Add($"BNpcName RowId {rowId} 仅用于名称，继续查 BNpcBase。");
        var row = this.FindRow<BNpcBase>(rowId, "BNpcBase", result);
        if (row == null)
            return this.Fail(result, "BNpcBase", $"未找到 BNpcBase RowId={rowId}。");

        result.FoundBNpcBase = true;
        result.RawDebugInfo = DumpPublicMembers(row);
        return this.ResolveModelFromRow(row, result, "BNpcBase");
    }

    private GameNpcAppearanceResolution ResolveModelCharaRow(uint rowId, GameNpcAppearanceResolution result)
    {
        var row = this.FindRow<ModelChara>(rowId, "ModelChara", result);
        if (row == null)
            return this.Fail(result, "ModelChara", $"未找到 ModelChara RowId={rowId}。");

        result.Success = true;
        result.ModelCharaId = rowId;
        result.Appearance.Kind = GameNpcResolvedAppearanceKind.Monster;
        result.Appearance.ModelCharaId = rowId;
        result.Message = $"已解析 ModelCharaId={rowId}。";
        result.Chain.Add(result.Message);
        result.RawDebugInfo = DumpPublicMembers(row);
        return result;
    }

    private GameNpcAppearanceResolution ResolveModelFromRow(object row, GameNpcAppearanceResolution result, string tableName)
    {
        var modelCharaId = ReadFirstUInt(row, "ModelChara", "ModelCharaId", "Model", "ModelId", "Character", "ModelMain");
        var customizeId = ReadFirstUInt(row, "Customize", "CustomizeId", "CustomizeData", "Base");
        var equipment = ReadEquipmentInfo(row);
        var humanoid = this.CreateHumanoidAppearance(row);

        result.EquipmentInfo = equipment;
        result.Chain.Add($"{tableName} RowId {result.BaseRowId} -> ModelCharaId={modelCharaId}, CustomizeId={customizeId}, Equipment={equipment}");
        if (humanoid != null)
        {
            result.Success = true;
            result.Appearance = humanoid;
            result.ModelCharaId = 0;
            result.CustomizeId = customizeId == 0 ? null : customizeId;
            result.Message = "已解析人形 NPC 外观：Customize + Equipment。";
            result.Chain.Add(result.Message);
            return result;
        }

        if (modelCharaId == 0)
            return this.Fail(result, tableName, $"{tableName} 未能解析 ModelCharaId，也未检测到完整人形 Customize/Equipment 字段。");

        result.Success = true;
        result.ModelCharaId = modelCharaId;
        result.Appearance.Kind = GameNpcResolvedAppearanceKind.Monster;
        result.Appearance.ModelCharaId = modelCharaId;
        result.CustomizeId = customizeId == 0 ? null : customizeId;
        result.Message = $"已解析 ModelCharaId={modelCharaId}。";
        return result;
    }

    private GameNpcResolvedAppearance? CreateHumanoidAppearance(object row)
    {
        var hasCustomize = HasAnyMember(row, "Race", "Gender", "Tribe", "Face", "HairStyle", "SkinColor", "EyeColor");
        var hasEquipment = HasAnyMember(row, "ModelHead", "ModelBody", "ModelHands", "ModelLegs", "ModelFeet", "ModelMainHand", "ModelOffHand");
        if (!hasCustomize && !hasEquipment)
            return null;

        return new GameNpcResolvedAppearance
        {
            Kind = GameNpcResolvedAppearanceKind.Humanoid,
            Customize = new GameNpcResolvedCustomize
            {
                Race = ReadFirstUInt(row, "Race"),
                Gender = ReadFirstUInt(row, "Gender"),
                Tribe = ReadFirstUInt(row, "Tribe"),
                BodyType = ReadFirstUInt(row, "BodyType"),
                Height = ReadFirstUInt(row, "Height"),
                Face = ReadFirstUInt(row, "Face"),
                HairStyle = ReadFirstUInt(row, "HairStyle"),
                HairHighlight = ReadFirstUInt(row, "HairHighlight"),
                SkinColor = ReadFirstUInt(row, "SkinColor"),
                EyeHeterochromia = ReadFirstUInt(row, "EyeHeterochromia"),
                HairColor = ReadFirstUInt(row, "HairColor"),
                HairHighlightColor = ReadFirstUInt(row, "HairHighlightColor"),
                FacialFeature = ReadFirstUInt(row, "FacialFeature"),
                FacialFeatureColor = ReadFirstUInt(row, "FacialFeatureColor"),
                Eyebrows = ReadFirstUInt(row, "Eyebrows"),
                EyeColor = ReadFirstUInt(row, "EyeColor"),
                EyeShape = ReadFirstUInt(row, "EyeShape"),
                Nose = ReadFirstUInt(row, "Nose"),
                Jaw = ReadFirstUInt(row, "Jaw"),
                Mouth = ReadFirstUInt(row, "Mouth"),
                LipColor = ReadFirstUInt(row, "LipColor"),
                BustOrTone1 = ReadFirstUInt(row, "BustOrTone1"),
                ExtraFeature1 = ReadFirstUInt(row, "ExtraFeature1"),
                ExtraFeature2OrBust = ReadFirstUInt(row, "ExtraFeature2OrBust"),
                FacePaint = ReadFirstUInt(row, "FacePaint"),
                FacePaintColor = ReadFirstUInt(row, "FacePaintColor"),
            },
            Equipment = new GameNpcResolvedEquipment
            {
                MainHand = ReadFirstUInt64(row, "ModelMainHand"),
                OffHand = ReadFirstUInt64(row, "ModelOffHand"),
                Head = ReadFirstUInt64(row, "ModelHead"),
                Body = ReadFirstUInt64(row, "ModelBody"),
                Hands = ReadFirstUInt64(row, "ModelHands"),
                Legs = ReadFirstUInt64(row, "ModelLegs"),
                Feet = ReadFirstUInt64(row, "ModelFeet"),
                Ears = ReadFirstUInt64(row, "ModelEars"),
                Neck = ReadFirstUInt64(row, "ModelNeck"),
                Wrists = ReadFirstUInt64(row, "ModelWrists"),
                LeftRing = ReadFirstUInt64(row, "ModelLeftRing"),
                RightRing = ReadFirstUInt64(row, "ModelRightRing"),
            },
            DebugInfo = DumpPublicMembers(row),
        };
    }

    private object? FindRow<T>(uint rowId, string tableName, GameNpcAppearanceResolution result)
        where T : struct, IExcelRow<T>
    {
        try
        {
            foreach (var row in this.dataManager.GetExcelSheet<T>())
            {
                if (ReadFirstUInt(row, "RowId") == rowId)
                {
                    result.Chain.Add($"命中 {tableName} RowId={rowId}。");
                    return row;
                }
            }
        }
        catch (Exception ex)
        {
            result.Chain.Add($"读取 {tableName} 失败：{ex.Message}");
            this.log.Warning(ex, "Failed to resolve GameNpc appearance from {TableName}", tableName);
        }

        return null;
    }

    private GameNpcAppearanceResolution Fail(GameNpcAppearanceResolution result, string step, string message)
    {
        result.FailureStep = step;
        result.Message = message;
        result.Chain.Add(message);
        return result;
    }

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

    private static ulong ReadFirstUInt64(object source, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadUInt64(source, memberName);
            if (value != 0)
                return value;
        }

        return 0;
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
        return uint.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static ulong ReadUInt64(object source, string memberName)
    {
        var value = ReadMember(source, memberName);
        if (value == null)
            return 0;

        if (value is ulong ulongValue)
            return ulongValue;
        if (value is long longValue && longValue >= 0)
            return (ulong)longValue;
        if (value is uint uintValue)
            return uintValue;
        if (value is ushort ushortValue)
            return ushortValue;
        if (value is byte byteValue)
            return byteValue;
        if (value is int intValue && intValue >= 0)
            return (uint)intValue;
        return ulong.TryParse(value.ToString(), out var parsed) ? parsed : 0;
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

    private static bool HasAnyMember(object source, params string[] memberNames)
        => memberNames.Any(name => ReadMember(source, name) != null);

    private static string BuildCacheKey(CustomNpcAppearance appearance)
        => string.Join("|", appearance.GameNpcKind, appearance.GameNpcBaseId, appearance.GameNpcModelId, appearance.GameNpcCustomizeId, appearance.GameNpcName);

    private static string DumpPublicMembers(object source)
    {
        try
        {
            var type = source.GetType();
            var members = new List<string>();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                try { members.Add($"{property.Name}={property.GetValue(source)}"); }
                catch { }
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                try { members.Add($"{field.Name}={field.GetValue(source)}"); }
                catch { }
            }

            return string.Join("; ", members.Take(120));
        }
        catch
        {
            return "无法读取字段";
        }
    }
}
