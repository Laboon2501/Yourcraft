using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Reflection;
using System.Text.Json;

namespace LocalQuestReborn.Services;

public sealed class ActorAppearanceLocalizerService
{
    private readonly IDataManager dataManager;
    private readonly GameNpcAppearanceResolver gameNpcResolver;
    private readonly IPluginLog log;
    private Dictionary<ulong, object>? itemCache;

    public ActorAppearanceLocalizerService(
        IDataManager dataManager,
        GameNpcAppearanceResolver gameNpcResolver,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.gameNpcResolver = gameNpcResolver;
        this.log = log;
    }

    public ActorAppearanceData FromNpcTemplate(CustomNpc npc)
    {
        var appearance = npc.Appearance ?? new CustomNpcAppearance();
        return appearance.SourceType switch
        {
            CustomNpcAppearanceSourceType.GlamourerDesign
                when this.TryCreateFromGlamourerDesign(
                    new GlamourerDesignEntry
                    {
                        Identifier = appearance.GlamourerDesignId,
                        Name = string.IsNullOrWhiteSpace(appearance.DisplayName) ? npc.Name : appearance.DisplayName,
                        FilePath = appearance.Notes,
                    },
                    out var designData,
                    out _) => designData,
            CustomNpcAppearanceSourceType.GameNpc
                when this.TryCreateFromGameNpcAppearance(appearance, ActorAppearanceSourceKind.GameNpc, out var gameNpcData, out _) => gameNpcData,
            CustomNpcAppearanceSourceType.CurrentPlayer => new ActorAppearanceData
            {
                SourceKind = ActorAppearanceSourceKind.CurrentPlayer,
                SourceId = npc.Id,
                SourceName = npc.Name,
                Summary = "Legacy current-player source; no local appearance snapshot was available.",
            },
            _ => new ActorAppearanceData
            {
                SourceKind = ActorAppearanceSourceKind.None,
                SourceId = npc.Id,
                SourceName = npc.Name,
                Summary = $"Legacy source {appearance.SourceType} could not be localized.",
            },
        };
    }

    public bool TryCreateFromGlamourerDesign(GlamourerDesignEntry design, out ActorAppearanceData data, out string reason)
    {
        data = new ActorAppearanceData
        {
            SourceKind = ActorAppearanceSourceKind.GlamourerDesign,
            SourceId = design.Identifier,
            SourceName = design.Name,
            SourcePath = design.FilePath,
        };

        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(design.FilePath) || !File.Exists(design.FilePath))
        {
            reason = $"Glamourer design file not found: {design.FilePath}";
            data.Summary = reason;
            return false;
        }

        try
        {
            using var stream = File.OpenRead(design.FilePath);
            using var document = JsonDocument.Parse(stream);
            if (!TryFindDesignObject(document.RootElement, design.Identifier, out var designObject))
            {
                reason = $"No Glamourer design object found in {design.FilePath}.";
                data.Summary = reason;
                return false;
            }

            if (TryGetString(designObject, out var name, "Name", "DisplayName") && !string.IsNullOrWhiteSpace(name))
                data.SourceName = name;
            if (TryGetString(designObject, out var identifier, "Identifier", "Guid", "GUID", "Id", "ID") && !string.IsNullOrWhiteSpace(identifier))
                data.SourceId = identifier;

            var explicitSpawnKind = ActorSpawnKind.Unknown;
            var sourceActorKind = string.Empty;
            if (TryReadSpawnKind(designObject, out var designKind, out var designKindText))
            {
                explicitSpawnKind = designKind;
                sourceActorKind = designKindText;
            }

            if (TryGetPropertyIgnoreCase(designObject, "Customize", out var customize) ||
                TryGetPropertyIgnoreCase(designObject, "Customization", out customize))
            {
                ReadGlamourerCustomize(customize, data);
                if (TryReadSpawnKind(customize, out var customizeKind, out var customizeKindText))
                {
                    explicitSpawnKind = customizeKind;
                    sourceActorKind = customizeKindText;
                }
            }

            if (TryGetPropertyIgnoreCase(designObject, "Equipment", out var equipment))
                ReadGlamourerEquipment(equipment, data);

            NormalizeAppearanceKind(data, explicitSpawnKind, sourceActorKind);
            data.Summary = BuildLocalizedSummary("Glamourer design", data, $"file={design.FilePath}");
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Failed to localize Glamourer design: {ex.Message}";
            data.Summary = reason;
            this.log.Warning(ex, "Failed to localize Glamourer design {DesignId} from {Path}", design.Identifier, design.FilePath);
            return false;
        }
    }

    public bool TryCreateFromGlamourerNpc(GameNpcCatalogEntry entry, out ActorAppearanceData data, out string reason)
    {
        var appearance = new CustomNpcAppearance
        {
            SourceType = CustomNpcAppearanceSourceType.GameNpc,
            DisplayName = entry.Name,
            GameNpcName = entry.Name,
            GameNpcKind = ToGameNpcKind(entry.Kind),
            GameNpcBaseId = entry.ResidentRowId != 0 ? entry.ResidentRowId : entry.RowId,
            GameNpcModelId = entry.Kind == GameNpcCatalogKind.ModelChara ? entry.ModelCharaId : 0,
            GameNpcCustomizeId = entry.CustomizeId ?? 0,
            Notes = $"{entry.DebugInfo}\n{entry.RawDebugInfo}",
        };

        return this.TryCreateFromGameNpcAppearance(appearance, ActorAppearanceSourceKind.GlamourerNpc, out data, out reason);
    }

    public bool TryCreateFromGameNpcAppearance(CustomNpcAppearance appearance, ActorAppearanceSourceKind sourceKind, out ActorAppearanceData data, out string reason)
    {
        data = new ActorAppearanceData
        {
            SourceKind = sourceKind,
            SourceId = appearance.GameNpcBaseId.ToString(),
            SourceName = string.IsNullOrWhiteSpace(appearance.GameNpcName) ? appearance.DisplayName : appearance.GameNpcName,
        };

        var resolution = this.gameNpcResolver.Resolve(appearance);
        var chain = string.Join(" -> ", resolution.Chain);
        if (!resolution.Success)
        {
            reason = $"Game NPC appearance resolve failed: {resolution.FailureStep}; {resolution.Message}; {chain}";
            data.Summary = reason;
            return false;
        }

        data.SpawnKind = NormalizeGameNpcSpawnKind(appearance.GameNpcKind, resolution.Appearance);
        data.SourceActorKind = appearance.GameNpcKind.ToString();
        data.ObjectKind = resolution.Appearance.ObjectKind;
        data.IsHumanoid = data.SpawnKind == ActorSpawnKind.Character && resolution.Appearance.Kind == GameNpcResolvedAppearanceKind.Humanoid;
        data.ModelCharaId = resolution.Appearance.Kind == GameNpcResolvedAppearanceKind.Monster
            ? resolution.Appearance.ModelCharaId
            : resolution.ModelCharaId;

        if (data.IsHumanoid)
        {
            CopyCustomize(resolution.Appearance.Customize, data.Customize);
            CopyEquipment(resolution.Appearance.Equipment, data.Equipment);
        }

        data.Summary = BuildLocalizedSummary(sourceKind.ToString(), data, $"kind={resolution.Appearance.Kind}, sourceActorKind={appearance.GameNpcKind}");
        reason = data.Summary;
        return true;
    }

    private static void ReadGlamourerCustomize(JsonElement customize, ActorAppearanceData data)
    {
        data.ModelCharaId = ReadUInt(customize, "ModelId", "ModelCharaId", "ModelChara");
        data.ModelSkeletonId = ReadUInt(customize, "ModelSkeletonId", "SkeletonId");
        data.IsHumanoid = data.ModelCharaId == 0 || HasAnyProperty(customize, "Race", "Gender", "Sex", "Clan", "Tribe");

        data.Customize.Race = ReadByte(customize, "Race");
        data.Customize.Sex = ReadByte(customize, "Gender", "Sex");
        data.Customize.BodyType = ReadByte(customize, "BodyType", fallback: 1);
        data.Customize.Height = ReadByte(customize, "Height");
        data.Customize.Tribe = ReadByte(customize, "Clan", "Tribe");
        data.Customize.Face = ReadByte(customize, "Face");
        data.Customize.HairStyle = ReadByte(customize, "Hairstyle", "HairStyle");
        data.Customize.Highlights = ReadByte(customize, "Highlights", "HairHighlight");
        data.Customize.SkinColor = ReadByte(customize, "SkinColor");
        data.Customize.EyeColorRight = ReadByte(customize, "EyeColorRight", "EyeHeterochromia");
        data.Customize.HairColor = ReadByte(customize, "HairColor");
        data.Customize.HighlightsColor = ReadByte(customize, "HighlightsColor", "HairHighlightColor");
        data.Customize.FacialFeatures = ReadFacialFeatureMask(customize);
        data.Customize.FacialFeaturesColor = ReadByte(customize, "FacialFeaturesColor", "FacialFeatureColor", "TattooColor");
        data.Customize.Eyebrows = ReadByte(customize, "Eyebrows");
        data.Customize.EyeColorLeft = ReadByte(customize, "EyeColorLeft", "EyeColor");
        data.Customize.EyeShape = ReadCompositeBitfield(customize, "EyeShape", "SmallIris");
        data.Customize.Nose = ReadByte(customize, "Nose");
        data.Customize.Jaw = ReadByte(customize, "Jaw");
        data.Customize.Lipstick = ReadCompositeBitfield(customize, "Mouth", "Lipstick");
        data.Customize.LipColorFurPattern = ReadByte(customize, "LipColor", "LipColorFurPattern");
        data.Customize.MuscleMass = ReadByte(customize, "MuscleMass", "BustOrTone1");
        data.Customize.TailShape = ReadByte(customize, "TailShape", "ExtraFeature1");
        data.Customize.BustSize = ReadByte(customize, "BustSize", "ExtraFeature2OrBust", "Bust");
        data.Customize.FacePaint = ReadCompositeBitfield(customize, "FacePaint", "FacePaintReversed");
        data.Customize.FacePaintColor = ReadByte(customize, "FacePaintColor");

        if (TryGetString(customize, out var rawArray, "Array", "CustomizeArray") && !string.IsNullOrWhiteSpace(rawArray))
            data.Customize.RawCustomizeBase64 = rawArray;
    }

    private static void CopyCustomize(GameNpcResolvedCustomize source, ActorCustomizeData target)
    {
        target.Race = ToByte(source.Race);
        target.Sex = ToByte(source.Gender);
        target.Tribe = ToByte(source.Tribe);
        target.BodyType = ToByte(source.BodyType == 0 ? 1 : source.BodyType);
        target.Height = ToByte(source.Height);
        target.Face = ToByte(source.Face);
        target.HairStyle = ToByte(source.HairStyle);
        target.Highlights = ToByte(source.HairHighlight);
        target.SkinColor = ToByte(source.SkinColor);
        target.EyeColorRight = ToByte(source.EyeHeterochromia);
        target.HairColor = ToByte(source.HairColor);
        target.HighlightsColor = ToByte(source.HairHighlightColor);
        target.FacialFeatures = ToByte(source.FacialFeature);
        target.FacialFeaturesColor = ToByte(source.FacialFeatureColor);
        target.Eyebrows = ToByte(source.Eyebrows);
        target.EyeColorLeft = ToByte(source.EyeColor);
        target.EyeShape = ToByte(source.EyeShape);
        target.Nose = ToByte(source.Nose);
        target.Jaw = ToByte(source.Jaw);
        target.Lipstick = ToByte(source.Mouth);
        target.LipColorFurPattern = ToByte(source.LipColor);
        target.MuscleMass = ToByte(source.BustOrTone1);
        target.TailShape = ToByte(source.ExtraFeature1);
        target.BustSize = ToByte(source.ExtraFeature2OrBust);
        target.FacePaint = ToByte(source.FacePaint);
        target.FacePaintColor = ToByte(source.FacePaintColor);
    }

    private static void CopyEquipment(GameNpcResolvedEquipment source, ActorEquipmentData target)
    {
        target.MainHand = DecodeWeapon(source.MainHand);
        target.OffHand = DecodeWeapon(source.OffHand);
        target.Head = DecodeGear(source.Head);
        target.Body = DecodeGear(source.Body);
        target.Hands = DecodeGear(source.Hands);
        target.Legs = DecodeGear(source.Legs);
        target.Feet = DecodeGear(source.Feet);
        target.Ears = DecodeGear(source.Ears);
        target.Neck = DecodeGear(source.Neck);
        target.Wrists = DecodeGear(source.Wrists);
        target.LeftRing = DecodeGear(source.LeftRing);
        target.RightRing = DecodeGear(source.RightRing);
    }

    private static ActorWeaponModelData? DecodeWeapon(ulong raw)
    {
        if (raw == 0)
            return null;

        return new ActorWeaponModelData
        {
            ModelSetId = (ushort)(raw & 0xFFFF),
            Base = (ushort)((raw >> 16) & 0xFFFF),
            Variant = (ushort)((raw >> 32) & 0xFFFF),
            Stain0 = (byte)((raw >> 48) & 0xFF),
            Stain1 = (byte)((raw >> 56) & 0xFF),
        };
    }

    private static ActorEquipmentModelData? DecodeGear(ulong raw)
    {
        if (raw == 0)
            return null;

        return new ActorEquipmentModelData
        {
            ModelId = (ushort)(raw & 0xFFFF),
            Variant = (byte)((raw >> 16) & 0xFF),
            Stain0 = (byte)((raw >> 24) & 0xFF),
            Stain1 = (byte)((raw >> 32) & 0xFF),
        };
    }

    private void ReadGlamourerEquipment(JsonElement equipment, ActorAppearanceData data)
    {
        data.Equipment.MainHand = this.ReadGlamourerWeaponSlot(equipment, "MainHand");
        data.Equipment.OffHand = this.ReadGlamourerWeaponSlot(equipment, "OffHand");
        data.Equipment.Head = this.ReadGlamourerGearSlot(equipment, "Head");
        data.Equipment.Body = this.ReadGlamourerGearSlot(equipment, "Body");
        data.Equipment.Hands = this.ReadGlamourerGearSlot(equipment, "Hands");
        data.Equipment.Legs = this.ReadGlamourerGearSlot(equipment, "Legs");
        data.Equipment.Feet = this.ReadGlamourerGearSlot(equipment, "Feet");
        data.Equipment.Ears = this.ReadGlamourerGearSlot(equipment, "Ears");
        data.Equipment.Neck = this.ReadGlamourerGearSlot(equipment, "Neck");
        data.Equipment.Wrists = this.ReadGlamourerGearSlot(equipment, "Wrist", "Wrists");
        data.Equipment.RightRing = this.ReadGlamourerGearSlot(equipment, "RFinger", "RightRing");
        data.Equipment.LeftRing = this.ReadGlamourerGearSlot(equipment, "LFinger", "LeftRing");

        if (TryGetPropertyIgnoreCase(equipment, "Hat", out var hat))
            data.Equipment.HideHeadgear = !ReadBool(hat, true, "Show", "Visible");
        if (TryGetPropertyIgnoreCase(equipment, "Weapon", out var weapon))
            data.Equipment.HideWeapons = !ReadBool(weapon, true, "Show", "Visible");
    }

    private ActorWeaponModelData? ReadGlamourerWeaponSlot(JsonElement equipment, params string[] slotNames)
    {
        if (!TryGetAnyProperty(equipment, out var slot, slotNames))
            return null;

        var stain0 = ReadByte(slot, "Stain", "Dye", "Stain0");
        var stain1 = ReadByte(slot, "Stain2", "Dye2", "Stain1");
        var itemId = ReadUInt64(slot, "ItemId", "Item", "Id");
        ActorWeaponModelData? weapon;
        if (itemId != 0 && this.TryResolveItemModel(itemId, isWeapon: true, stain0, stain1, out weapon, out _))
            return weapon;

        var modelSet = ReadUShort(slot, "ModelSetId", "ModelSet", "Id");
        var modelBase = ReadUShort(slot, "Base", "Type");
        var variant = ReadUShort(slot, "Variant");
        return modelSet == 0 && !ReadBool(slot, false, "Apply")
            ? null
            : new ActorWeaponModelData { ModelSetId = modelSet, Base = modelBase, Variant = variant, Stain0 = stain0, Stain1 = stain1 };
    }

    private ActorEquipmentModelData? ReadGlamourerGearSlot(JsonElement equipment, params string[] slotNames)
    {
        if (!TryGetAnyProperty(equipment, out var slot, slotNames))
            return null;

        var stain0 = ReadByte(slot, "Stain", "Dye", "Stain0");
        var stain1 = ReadByte(slot, "Stain2", "Dye2", "Stain1");
        var itemId = ReadUInt64(slot, "ItemId", "Item", "Id");
        ActorEquipmentModelData? gear;
        if (itemId != 0 && this.TryResolveItemModel(itemId, isWeapon: false, stain0, stain1, out gear, out _))
            return gear;

        var modelId = ReadUShort(slot, "ModelId", "Model", "Id");
        var variant = ReadByte(slot, "Variant");
        return modelId == 0 && !ReadBool(slot, false, "Apply")
            ? null
            : new ActorEquipmentModelData { ModelId = modelId, Variant = variant, Stain0 = stain0, Stain1 = stain1 };
    }

    private bool TryResolveItemModel(ulong itemId, bool isWeapon, byte stain0, byte stain1, out ActorWeaponModelData? weapon, out string reason)
    {
        weapon = null;
        reason = string.Empty;
        if (!this.TryGetItemRow(itemId, out var row))
        {
            reason = $"Item row not found: {itemId}";
            return false;
        }

        var raw = ReadFirstUInt64(row, "ModelMain", "Model", "ModelId");
        if (raw == 0)
            raw = ReadFirstUInt64(row, "ModelSub", "SubModel");
        if (raw == 0)
        {
            reason = $"Item row has no model field: {itemId}";
            return false;
        }

        if (isWeapon)
        {
            weapon = new ActorWeaponModelData
            {
                ModelSetId = (ushort)(raw & 0xFFFF),
                Base = (ushort)((raw >> 16) & 0xFFFF),
                Variant = (ushort)((raw >> 32) & 0xFFFF),
                Stain0 = stain0,
                Stain1 = stain1,
            };
            return true;
        }

        reason = "Requested gear model with weapon resolver.";
        return false;
    }

    private bool TryResolveItemModel(ulong itemId, bool isWeapon, byte stain0, byte stain1, out ActorEquipmentModelData? gear, out string reason)
    {
        gear = null;
        reason = string.Empty;
        if (!this.TryGetItemRow(itemId, out var row))
        {
            reason = $"Item row not found: {itemId}";
            return false;
        }

        var raw = ReadFirstUInt64(row, "ModelMain", "Model", "ModelId");
        if (raw == 0)
            raw = ReadFirstUInt64(row, "ModelSub", "SubModel");
        if (raw == 0)
        {
            reason = $"Item row has no model field: {itemId}";
            return false;
        }

        if (!isWeapon)
        {
            gear = new ActorEquipmentModelData
            {
                ModelId = (ushort)(raw & 0xFFFF),
                Variant = (byte)((raw >> 16) & 0xFF),
                Stain0 = stain0,
                Stain1 = stain1,
            };
            return true;
        }

        reason = "Requested weapon model with gear resolver.";
        return false;
    }

    private bool TryGetItemRow(ulong itemId, out object row)
    {
        this.itemCache ??= this.BuildItemCache();
        return this.itemCache.TryGetValue(itemId, out row!);
    }

    private Dictionary<ulong, object> BuildItemCache()
    {
        var cache = new Dictionary<ulong, object>();
        try
        {
            foreach (var row in this.dataManager.GetExcelSheet<Item>())
            {
                var rowId = ReadFirstUInt64(row, "RowId");
                if (rowId != 0)
                    cache[rowId] = row;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to build item cache for Glamourer design localization.");
        }

        return cache;
    }

    private static bool TryFindDesignObject(JsonElement element, string identifier, out JsonElement found, int depth = 0)
    {
        if (depth > 16)
        {
            found = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasDesignShape = HasAnyProperty(element, "Equipment", "Customize", "Customization");
            if (hasDesignShape)
            {
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    found = element;
                    return true;
                }

                if (TryGetString(element, out var id, "Identifier", "Guid", "GUID", "Id", "ID") &&
                    string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase))
                {
                    found = element;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, identifier, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    found = property.Value;
                    return true;
                }

                if (TryFindDesignObject(property.Value, identifier, out found, depth + 1))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDesignObject(item, identifier, out found, depth + 1))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static bool TryGetAnyProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names)
        => names.Any(name => TryGetPropertyIgnoreCase(element, name, out _));

    private static bool TryGetString(JsonElement element, out string value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(property, "Value", out var nested))
                property = nested;

            value = property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool ReadBool(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(property, "Value", out var nested))
                property = nested;
            if (property.ValueKind == JsonValueKind.True)
                return true;
            if (property.ValueKind == JsonValueKind.False)
                return false;
            if (bool.TryParse(property.ToString(), out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static byte ReadByte(JsonElement element, params string[] names)
        => ReadByte(element, names, 0);

    private static byte ReadByte(JsonElement element, string[] names, byte fallback)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
                continue;

            var value = ReadUInt64FromElement(property);
            return ToByte(value == 0 ? fallback : value);
        }

        return fallback;
    }

    private static byte ReadByte(JsonElement element, string name, byte fallback)
        => ReadByte(element, [name], fallback);

    private static uint ReadUInt(JsonElement element, params string[] names)
        => (uint)Math.Min(uint.MaxValue, ReadUInt64(element, names));

    private static ushort ReadUShort(JsonElement element, params string[] names)
        => (ushort)Math.Min(ushort.MaxValue, ReadUInt64(element, names));

    private static ulong ReadUInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
                continue;
            return ReadUInt64FromElement(property);
        }

        return 0;
    }

    private static ulong ReadUInt64FromElement(JsonElement element, ulong trueValue = 1)
    {
        if (element.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(element, "Value", out var value))
            element = value;
        if (element.ValueKind == JsonValueKind.True)
            return trueValue;
        if (element.ValueKind == JsonValueKind.False)
            return 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var number))
            return number;
        if (bool.TryParse(element.ToString(), out var parsedBool))
            return parsedBool ? trueValue : 0;
        return ulong.TryParse(element.ToString(), out var parsed) ? parsed : 0;
    }

    private static ulong ReadFirstUInt64(object source, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadMember(source, memberName);
            var number = ToUInt64(value);
            if (number != 0)
                return number;
        }

        return 0;
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

    private static ulong ToUInt64(object? value)
    {
        if (value == null)
            return 0;

        return value switch
        {
            ulong u64 => u64,
            long i64 when i64 >= 0 => (ulong)i64,
            uint u32 => u32,
            int i32 when i32 >= 0 => (uint)i32,
            ushort u16 => u16,
            short i16 when i16 >= 0 => (ushort)i16,
            byte u8 => u8,
            sbyte i8 when i8 >= 0 => (byte)i8,
            _ => ulong.TryParse(value.ToString(), out var parsed) ? parsed : 0,
        };
    }

    private static byte ToByte(ulong value)
        => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);

    private static byte ToByte(uint value)
        => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);

    private static byte ReadFacialFeatureMask(JsonElement customize)
    {
        var mask = ReadByte(customize, "FacialFeatures", "FacialFeature");
        for (var index = 1; index <= 7; index++)
        {
            if (TryGetPropertyIgnoreCase(customize, $"FacialFeature{index}", out var feature))
                mask |= ToByte(ReadUInt64FromElement(feature, 1UL << (index - 1)));
        }

        if (TryGetPropertyIgnoreCase(customize, "LegacyTattoo", out var tattoo))
            mask |= ToByte(ReadUInt64FromElement(tattoo, 0x80));

        return mask;
    }

    private static byte ReadCompositeBitfield(JsonElement customize, string lowName, string highName)
    {
        var value = (byte)(ReadByte(customize, lowName) & 0x7F);
        if (TryGetPropertyIgnoreCase(customize, highName, out var high) &&
            ReadUInt64FromElement(high, 0x80) != 0)
        {
            value |= 0x80;
        }

        return value;
    }

    private static string BuildLocalizedSummary(string source, ActorAppearanceData data, string extra)
    {
        var customizeCount = CountCustomizeFields(data.Customize);
        var equipmentCount = CountEquipment(data.Equipment);
        var missing = new List<string>();
        if (data.SpawnKind == ActorSpawnKind.Character && customizeCount == 0)
            missing.Add("customize");
        if (data.SpawnKind == ActorSpawnKind.Character && equipmentCount == 0)
            missing.Add("equipment");
        if (data.SpawnKind is ActorSpawnKind.Demihuman or ActorSpawnKind.Mount or ActorSpawnKind.Minion && data.ModelCharaId == 0)
            missing.Add("modelChara");

        return $"Localized {source}. spawnKind={data.SpawnKind}, sourceActorKind={data.SourceActorKind}, objectKind={data.ObjectKind}, humanoid={data.IsHumanoid}, modelChara={data.ModelCharaId}, modelSkeleton={data.ModelSkeletonId}, customizeFields={customizeCount}/26, equipmentSlots={equipmentCount}/12, missing={(missing.Count == 0 ? "none" : string.Join(',', missing))}, {extra}";
    }

    private static int CountCustomizeFields(ActorCustomizeData data)
    {
        if (!string.IsNullOrWhiteSpace(data.RawCustomizeBase64))
            return 26;

        var count = 0;
        if (data.Race != 0) count++;
        if (data.Sex != 0) count++;
        if (data.BodyType is not 0 and not 1) count++;
        if (data.Height != 0) count++;
        if (data.Tribe != 0) count++;
        if (data.Face != 0) count++;
        if (data.HairStyle != 0) count++;
        if (data.Highlights != 0) count++;
        if (data.SkinColor != 0) count++;
        if (data.EyeColorRight != 0) count++;
        if (data.HairColor != 0) count++;
        if (data.HighlightsColor != 0) count++;
        if (data.FacialFeatures != 0) count++;
        if (data.FacialFeaturesColor != 0) count++;
        if (data.Eyebrows != 0) count++;
        if (data.EyeColorLeft != 0) count++;
        if (data.EyeShape != 0) count++;
        if (data.Nose != 0) count++;
        if (data.Jaw != 0) count++;
        if (data.Lipstick != 0) count++;
        if (data.LipColorFurPattern != 0) count++;
        if (data.MuscleMass != 0) count++;
        if (data.TailShape != 0) count++;
        if (data.BustSize != 0) count++;
        if (data.FacePaint != 0) count++;
        if (data.FacePaintColor != 0) count++;
        return count;
    }

    private static int CountEquipment(ActorEquipmentData data)
    {
        var count = 0;
        if (data.MainHand != null) count++;
        if (data.OffHand != null) count++;
        if (data.Head != null) count++;
        if (data.Body != null) count++;
        if (data.Hands != null) count++;
        if (data.Legs != null) count++;
        if (data.Feet != null) count++;
        if (data.Ears != null) count++;
        if (data.Neck != null) count++;
        if (data.Wrists != null) count++;
        if (data.LeftRing != null) count++;
        if (data.RightRing != null) count++;
        return count;
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

    private static void NormalizeAppearanceKind(ActorAppearanceData data, ActorSpawnKind explicitSpawnKind, string sourceActorKind)
    {
        if (explicitSpawnKind != ActorSpawnKind.Unknown)
        {
            data.SpawnKind = explicitSpawnKind;
            data.SourceActorKind = sourceActorKind;
        }
        else if (data.IsHumanoid)
        {
            data.SpawnKind = ActorSpawnKind.Character;
            data.SourceActorKind = string.IsNullOrWhiteSpace(data.SourceActorKind) ? "Character" : data.SourceActorKind;
        }
        else
        {
            data.SpawnKind = ActorSpawnKind.Demihuman;
            data.SourceActorKind = string.IsNullOrWhiteSpace(data.SourceActorKind) ? "Demihuman" : data.SourceActorKind;
        }

        data.IsHumanoid = data.SpawnKind == ActorSpawnKind.Character;
        data.ObjectKind = data.SpawnKind switch
        {
            ActorSpawnKind.Mount => "Mount",
            ActorSpawnKind.Minion => "Companion",
            ActorSpawnKind.Demihuman => "BattleNpc",
            ActorSpawnKind.Character => "BattleNpc",
            _ => data.ObjectKind,
        };
    }

    private static ActorSpawnKind NormalizeGameNpcSpawnKind(GameNpcKind sourceKind, GameNpcResolvedAppearance appearance)
    {
        if (appearance.SpawnKind != ActorSpawnKind.Unknown)
            return appearance.SpawnKind;

        return sourceKind switch
        {
            GameNpcKind.Mount => ActorSpawnKind.Mount,
            GameNpcKind.Companion => ActorSpawnKind.Minion,
            GameNpcKind.Monster or GameNpcKind.ModelChara => ActorSpawnKind.Demihuman,
            _ when appearance.Kind == GameNpcResolvedAppearanceKind.Humanoid => ActorSpawnKind.Character,
            _ when appearance.ModelCharaId != 0 => ActorSpawnKind.Demihuman,
            _ => ActorSpawnKind.Unknown,
        };
    }

    private static bool TryReadSpawnKind(JsonElement element, out ActorSpawnKind kind, out string rawKind)
    {
        kind = ActorSpawnKind.Unknown;
        rawKind = string.Empty;
        if (!TryGetString(element, out rawKind, "SpawnKind", "ActorKind", "ObjectKind", "Kind", "Type", "ObjectType"))
            return false;

        kind = MapSpawnKind(rawKind);
        return kind != ActorSpawnKind.Unknown;
    }

    private static ActorSpawnKind MapSpawnKind(string rawKind)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
            return ActorSpawnKind.Unknown;

        var value = rawKind.Trim();
        if (uint.TryParse(value, out var numeric))
        {
            return numeric switch
            {
                8 => ActorSpawnKind.Mount,
                9 => ActorSpawnKind.Minion,
                _ => ActorSpawnKind.Unknown,
            };
        }

        if (value.Contains("Mount", StringComparison.OrdinalIgnoreCase))
            return ActorSpawnKind.Mount;
        if (value.Contains("Companion", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Minion", StringComparison.OrdinalIgnoreCase))
            return ActorSpawnKind.Minion;
        if (value.Contains("Demi", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Monster", StringComparison.OrdinalIgnoreCase))
            return ActorSpawnKind.Demihuman;
        if (value.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Human", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("BattleNpc", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("EventNpc", StringComparison.OrdinalIgnoreCase))
            return ActorSpawnKind.Character;

        return ActorSpawnKind.Unknown;
    }
}
