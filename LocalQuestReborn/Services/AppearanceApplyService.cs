using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using LocalQuestReborn.Models;
using System.Text.Json;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;

namespace LocalQuestReborn.Services;

public sealed class AppearanceApplyService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly GameNpcAppearanceResolver gameNpcResolver;
    private readonly GameNpcAppearanceApplyService gameNpcApplyService;
    private readonly IPluginLog log;

    public AppearanceApplyService(
        IDalamudPluginInterface pluginInterface,
        GlamourerIpcProbeService glamourerIpcProbe,
        GlamourerIpcBridgeService glamourerIpcBridge,
        PenumbraIpcService penumbraIpc,
        GameNpcAppearanceResolver gameNpcResolver,
        GameNpcAppearanceApplyService gameNpcApplyService,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.glamourerIpcProbe = glamourerIpcProbe;
        this.glamourerIpcBridge = glamourerIpcBridge;
        this.penumbraIpc = penumbraIpc;
        this.gameNpcResolver = gameNpcResolver;
        this.gameNpcApplyService = gameNpcApplyService;
        this.log = log;
    }

    public bool IsHumanoidGlamourerApplyStateAvailable => this.gameNpcApplyService.IsGlamourerApplyStateAvailable;
    public string HumanoidGlamourerApplyStateSignature => this.gameNpcApplyService.GlamourerApplyStateSignature;
    public bool IsHumanoidBrioActorAppearanceAvailable => this.gameNpcApplyService.IsBrioActorAppearanceCapabilityAvailable;
    public string HumanoidBrioActorAppearanceSignature => this.gameNpcApplyService.BrioActorAppearanceSignature;
    public string HumanoidAppearanceCurrentPath => this.gameNpcApplyService.CurrentHumanoidApplyPath;
    public string HumanoidAppearanceLastResult => this.gameNpcApplyService.LastHumanoidApplyResult;
    public string HumanoidAppearanceLastException => this.gameNpcApplyService.LastHumanoidApplyException;
    public string HumanoidGlamourerApplyStateLastResult => this.gameNpcApplyService.GlamourerApplyStateLastResult;
    public string HumanoidGlamourerApplyStateLastException => this.gameNpcApplyService.GlamourerApplyStateLastException;
    public string HumanoidBrioActorAppearanceLastResult => this.gameNpcApplyService.BrioActorAppearanceLastResult;
    public string HumanoidBrioActorAppearanceLastException => this.gameNpcApplyService.BrioActorAppearanceLastException;
    public bool HumanoidBrioActorAppearanceApplyPending => this.gameNpcApplyService.BrioActorAppearanceApplyPending;
    public string? HumanoidBrioActorAppearancePendingRuntimeId => this.gameNpcApplyService.BrioActorAppearancePendingRuntimeId;
    public DateTime? HumanoidBrioActorAppearanceApplyCompletedAt => this.gameNpcApplyService.BrioActorAppearanceApplyCompletedAt;

    public bool IsNpcAppearanceApplyPending(RuntimeActorInstance actor)
        => this.gameNpcApplyService.BrioActorAppearanceApplyPending &&
           string.Equals(this.gameNpcApplyService.BrioActorAppearancePendingRuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase);

    public string DescribeNpcPreset(CustomNpc npc)
    {
        var appearance = npc.Appearance ?? new CustomNpcAppearance();
        try
        {
            return appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.GameNpc => this.DescribeGameNpcPreset(appearance),
                CustomNpcAppearanceSourceType.GlamourerDesign => DescribeGlamourerDesignPreset(appearance),
                CustomNpcAppearanceSourceType.CurrentPlayer => "source=CurrentPlayer; hasEquipment=player-clone; fullDesign=false",
                CustomNpcAppearanceSourceType.None => "source=None; hasEquipment=false; fullDesign=false",
                CustomNpcAppearanceSourceType.PenumbraCollection => "source=PenumbraCollection; hasEquipment=false; fullDesign=false; collection-only",
                CustomNpcAppearanceSourceType.MCDF => "source=MCDF; hasEquipment=unknown; fullDesign=unknown; MCDF apply not connected",
                _ => $"source={appearance.SourceType}; hasEquipment=unknown; fullDesign=unknown",
            };
        }
        catch (Exception ex)
        {
            return $"source={appearance.SourceType}; preset diagnostic failed={ex.Message}";
        }
    }

    public void RefreshHumanoidAppearancePaths()
        => this.gameNpcApplyService.RefreshPathStatus();

    public bool ApplyNpcAppearance(CustomNpc npc, RuntimeActorInstance actor)
    {
        try
        {
            var appearance = npc.Appearance ?? new CustomNpcAppearance();
            actor.AppearanceSourceType = appearance.SourceType.ToString();
            if (!this.penumbraIpc.ApplyCollection(npc, actor, out var penumbraReason))
            {
                actor.LastPenumbraCollectionError = penumbraReason;
                this.log.Warning("Penumbra collection stage failed but appearance pipeline will continue. Actor={Actor}, Reason={Reason}", actor.RuntimeId, penumbraReason);
            }

            var success = appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.None => this.ApplyNone(actor),
                CustomNpcAppearanceSourceType.CurrentPlayer => this.ApplyCurrentPlayer(actor),
                CustomNpcAppearanceSourceType.GlamourerDesign => this.ApplyGlamourerDesign(appearance, actor),
                CustomNpcAppearanceSourceType.GameNpc => this.ApplyGameNpc(appearance, actor),
                CustomNpcAppearanceSourceType.MCDF => this.Fail(actor, "MCDF", "MCDF 外观应用暂未接入安全接口。"),
                CustomNpcAppearanceSourceType.PenumbraCollection => this.ApplyPenumbraAppearanceSource(actor),
                _ => this.Fail(actor, appearance.SourceType.ToString(), $"未知外观来源：{appearance.SourceType}"),
            };

            actor.LastAppearanceAppliedAt = DateTime.Now;
            return success;
        }
        catch (Exception ex)
        {
            actor.LastAppearanceMethod = "Failed";
            actor.LastAppearanceError = ex.Message;
            actor.LastAppearanceApplyResult = $"外观应用异常：{ex.Message}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            this.log.Error(ex, "ApplyNpcAppearance failed. RuntimeId={RuntimeId}, NpcId={NpcId}", actor.RuntimeId, npc.Id);
            return false;
        }
    }

    public bool ApplyNpcPresetAppearance(CustomNpc npc, RuntimeActorInstance actor)
    {
        try
        {
            var appearance = npc.Appearance ?? new CustomNpcAppearance();
            actor.AppearanceSourceType = appearance.SourceType.ToString();
            var success = appearance.SourceType switch
            {
                CustomNpcAppearanceSourceType.None => this.ApplyNone(actor),
                CustomNpcAppearanceSourceType.CurrentPlayer => this.ApplyCurrentPlayer(actor),
                CustomNpcAppearanceSourceType.GlamourerDesign => this.ApplyGlamourerDesign(appearance, actor),
                CustomNpcAppearanceSourceType.GameNpc => this.ApplyGameNpc(appearance, actor),
                CustomNpcAppearanceSourceType.MCDF => this.Fail(actor, "MCDF", "MCDF appearance apply is not connected to a safe path yet."),
                CustomNpcAppearanceSourceType.PenumbraCollection => this.ApplyPenumbraAppearanceSource(actor),
                _ => this.Fail(actor, appearance.SourceType.ToString(), $"Unknown appearance source: {appearance.SourceType}"),
            };

            actor.LastAppearanceAppliedAt = DateTime.Now;
            return success;
        }
        catch (Exception ex)
        {
            actor.LastAppearanceMethod = "Failed";
            actor.LastAppearanceError = ex.Message;
            actor.LastAppearanceApplyResult = $"Appearance apply exception: {ex.Message}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            this.log.Error(ex, "ApplyNpcPresetAppearance failed. RuntimeId={RuntimeId}, NpcId={NpcId}", actor.RuntimeId, npc.Id);
            return false;
        }
    }

    public bool ApplyActorConfigAppearance(PersistentActorConfig config, RuntimeActorInstance actor)
    {
        var appearance = config.Appearance ?? new ActorAppearanceData();
        try
        {
            actor.AppearanceSourceType = appearance.SourceKind.ToString();
            actor.GlamourerDesignId = appearance.SourceKind == ActorAppearanceSourceKind.GlamourerDesign ? appearance.SourceId : string.Empty;
            actor.GlamourerDesignName = appearance.SourceName;
            actor.GlamourerDesignPath = appearance.SourcePath;
            actor.GlamourerIpcAvailable = false;
            var effectiveModelCharaId = EffectiveModelCharaId(appearance);
            actor.SourceModelCharaId = appearance.ModelCharaId;
            actor.ModelCharaOverrideId = appearance.ModelCharaOverrideId;
            actor.EditingModelCharaId = effectiveModelCharaId;
            actor.SpawnKind = appearance.SpawnKind == ActorSpawnKind.Unknown ? (appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman) : appearance.SpawnKind;
            actor.SourceActorKind = appearance.SourceActorKind;
            actor.SpawnKindStatus = $"appearance spawnKind={actor.SpawnKind}, objectKind={appearance.ObjectKind}, sourceActorKind={appearance.SourceActorKind}, sourceModelChara={appearance.ModelCharaId}, overrideModelChara={appearance.ModelCharaOverrideId}";
            actor.LastAppearancePresetSummary = appearance.Summary;
            actor.LastAppearanceVerificationState = "PendingAppearance";
            actor.LastAppearanceValidationResult = string.Empty;

            if (appearance.SourceKind == ActorAppearanceSourceKind.None)
            {
                actor.LastAppearanceMethod = "None";
                actor.LastAppearanceError = string.Empty;
                actor.LastAppearanceApplyResult = "No local actor appearance was saved; skipped.";
                actor.LastAppearanceAppliedAt = DateTime.Now;
                return true;
            }

            if (appearance.SourceKind == ActorAppearanceSourceKind.CurrentPlayer)
            {
                actor.LastAppearanceMethod = "CurrentPlayer";
                actor.LastAppearanceError = string.Empty;
                actor.LastAppearanceApplyResult = "CurrentPlayer legacy source; actor keeps its current runtime appearance.";
                actor.LastAppearanceAppliedAt = DateTime.Now;
                return true;
            }

            if (!HasLocalAppearanceSignal(appearance))
                return this.FailActorAppearance(actor, "LocalActorAppearanceFailed", $"AppearanceFailed: local snapshot has no usable model/customize/equipment fields. source={appearance.SourceKind}, summary={appearance.Summary}");

            var spawnKind = actor.SpawnKind;
            var isCharacter = spawnKind == ActorSpawnKind.Character;
            appearance.SpawnKind = spawnKind;
            appearance.IsHumanoid = isCharacter;
            if (!isCharacter && effectiveModelCharaId == 0)
                return this.FailActorAppearance(actor, "MissingModelData", $"AppearanceFailed: {spawnKind} requires modelCharaId. source={appearance.SourceKind}, summary={appearance.Summary}");

            if (actor.CharacterObject == null || actor.IsStale)
                return this.FailActorAppearance(actor, "LocalAppearance", "Runtime actor is not ready for local appearance apply.");

            if (!TryReadAddress(actor, out var address) || address == 0)
                return this.FailActorAppearance(actor, "LocalAppearance", $"Runtime actor address is unavailable: {actor.Address}");

            string beforeSummary;
            string afterSummary;
            string verification;
            string customizeWriteMode = string.Empty;
            unsafe
            {
                var character = (Character*)address;
                beforeSummary = BuildNativeAppearanceSummary(character);
                character->GameObject.DisableDraw();
                character->Scale = 1f;
                if (effectiveModelCharaId != 0 || !isCharacter)
                    character->ModelContainer.ModelCharaId = (int)effectiveModelCharaId;
                if (appearance.ModelSkeletonId != 0)
                    character->ModelContainer.ModelSkeletonId = (int)appearance.ModelSkeletonId;

                if (isCharacter && !ApplyCustomize(character, appearance.Customize, out customizeWriteMode))
                    return this.FailActorAppearance(actor, "LocalActorAppearanceFailed", $"AppearanceFailed: invalid customize snapshot. {customizeWriteMode}");

                if (isCharacter)
                {
                    ApplyEquipment(character, appearance.Equipment);
                    character->DrawData.HideWeapons(appearance.Equipment.HideWeapons);
                    character->DrawData.HideHeadgear(0, appearance.Equipment.HideHeadgear);
                }
                character->Alpha = 1f;
                character->GameObject.EnableDraw();
                afterSummary = BuildNativeAppearanceSummary(character);
                if (!VerifyApplied(character, appearance, out verification))
                    return this.FailActorAppearance(actor, "LocalActorAppearanceFailed", $"AppearanceFailed: {verification}. before={beforeSummary}; after={afterSummary}; sourceSummary={appearance.Summary}");
                actor.LastAppliedModelCharaId = (uint)Math.Max(0, character->ModelContainer.ModelCharaId);
            }

            actor.LastAppearanceMethod = $"LocalActorAppearance:{appearance.SourceKind}:{spawnKind}";
            actor.LastAppearanceError = string.Empty;
            actor.LastGlamourerApplyError = string.Empty;
            actor.LastGlamourerApplyStatus = appearance.SourceKind == ActorAppearanceSourceKind.GlamourerDesign
                ? "Glamourer design data was applied from local ActorConfig, not via Glamourer IPC."
                : string.Empty;
            actor.LastAppearanceBeforeSummary = beforeSummary;
            actor.LastAppearanceAfterSummary = afterSummary;
            actor.LastAppearanceValidationResult = verification;
            actor.LastAppearanceVerificationState = "AppearanceApplied";
            actor.LastAppearanceRedrawFallbackCount = 1;
            actor.LastAppearanceApplyResult = $"AppearanceApplied: source={appearance.SourceKind}, spawnKind={spawnKind}, name={appearance.SourceName}, sourceModelChara={appearance.ModelCharaId}, overrideModelChara={appearance.ModelCharaOverrideId}, effectiveModelChara={effectiveModelCharaId}, actualAppliedModelChara={actor.LastAppliedModelCharaId}, fields={BuildAppearanceFieldSummary(appearance)}, customizeWrite={customizeWriteMode}, verification={verification}, summary={appearance.Summary}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            actor.LastAppearanceMethod = "LocalActorAppearanceFailed";
            actor.LastAppearanceError = ex.Message;
            actor.LastAppearanceApplyResult = $"Local actor appearance apply exception: {ex.Message}";
            actor.LastAppearanceAppliedAt = DateTime.Now;
            this.log.Error(ex, "ApplyActorConfigAppearance failed. RuntimeId={RuntimeId}, ConfigId={ConfigId}", actor.RuntimeId, config.ConfigId);
            return false;
        }
    }

    private enum CustomizeIndex
    {
        Race = 0,
        Sex = 1,
        BodyType = 2,
        Height = 3,
        Tribe = 4,
        Face = 5,
        HairStyle = 6,
        Highlights = 7,
        SkinColor = 8,
        EyeColorRight = 9,
        HairColor = 10,
        HighlightsColor = 11,
        FacialFeatures = 12,
        FacialFeaturesColor = 13,
        Eyebrows = 14,
        EyeColorLeft = 15,
        EyeShape = 16,
        Nose = 17,
        Jaw = 18,
        Lipstick = 19,
        LipColorFurPattern = 20,
        MuscleMass = 21,
        TailShape = 22,
        BustSize = 23,
        FacePaint = 24,
        FacePaintColor = 25,
    }

    private unsafe static bool ApplyCustomize(Character* character, ActorCustomizeData customize, out string reason)
    {
        if (!TryBuildCustomizeBytes(customize, out var bytes, out reason))
            return false;

        for (var index = 0; index < bytes.Length; index++)
            character->DrawData.CustomizeData.Data[index] = bytes[index];

        return true;
    }

    private unsafe static void ApplyEquipment(Character* character, ActorEquipmentData equipment)
    {
        ApplyWeapon(character, WeaponSlot.MainHand, equipment.MainHand);
        ApplyWeapon(character, WeaponSlot.OffHand, equipment.OffHand);
        ApplyGear(character, EquipmentSlot.Head, equipment.Head);
        ApplyGear(character, EquipmentSlot.Body, equipment.Body);
        ApplyGear(character, EquipmentSlot.Hands, equipment.Hands);
        ApplyGear(character, EquipmentSlot.Legs, equipment.Legs);
        ApplyGear(character, EquipmentSlot.Feet, equipment.Feet);
        ApplyGear(character, EquipmentSlot.Ears, equipment.Ears);
        ApplyGear(character, EquipmentSlot.Neck, equipment.Neck);
        ApplyGear(character, EquipmentSlot.Wrists, equipment.Wrists);
        ApplyGear(character, EquipmentSlot.LFinger, equipment.LeftRing);
        ApplyGear(character, EquipmentSlot.RFinger, equipment.RightRing);
    }

    private unsafe static void ApplyWeapon(Character* character, WeaponSlot slot, ActorWeaponModelData? model)
    {
        if (model == null)
            return;

        var weapon = new WeaponModelId
        {
            Id = model.ModelSetId,
            Type = model.Base,
            Variant = model.Variant,
            Stain0 = model.Stain0,
            Stain1 = model.Stain1,
        };

        character->DrawData.LoadWeapon(slot, weapon, 1, 0, 0, 0, false);
        character->DrawData.Weapon(slot).ModelId = weapon;
    }

    private unsafe static void ApplyGear(Character* character, EquipmentSlot slot, ActorEquipmentModelData? model)
    {
        if (model == null)
            return;

        var item = new EquipmentModelId
        {
            Id = model.ModelId,
            Variant = model.Variant,
            Stain0 = model.Stain0,
            Stain1 = model.Stain1,
        };
        character->DrawData.Equipment(slot) = item;
        character->DrawData.LoadEquipment(slot, &item, true);
    }

    private static bool HasLocalAppearanceSignal(ActorAppearanceData appearance)
        => EffectiveModelCharaId(appearance) != 0 ||
           appearance.ModelSkeletonId != 0 ||
           HasCustomizeSignal(appearance.Customize) ||
           CountEquipment(appearance.Equipment) > 0;

    private static uint EffectiveModelCharaId(ActorAppearanceData appearance)
        => appearance.ModelCharaOverrideId != 0 ? appearance.ModelCharaOverrideId : appearance.ModelCharaId;

    private static bool HasCustomizeSignal(ActorCustomizeData data)
        => !string.IsNullOrWhiteSpace(data.RawCustomizeBase64) ||
           data.Race != 0 ||
           data.Tribe != 0 ||
           data.Face != 0 ||
           data.HairStyle != 0 ||
           data.SkinColor != 0 ||
           data.EyeColorRight != 0 ||
           data.HairColor != 0 ||
           data.FacialFeatures != 0 ||
           data.EyeColorLeft != 0 ||
           data.EyeShape != 0 ||
           data.Nose != 0 ||
           data.Jaw != 0 ||
           data.Lipstick != 0 ||
           data.LipColorFurPattern != 0 ||
           data.MuscleMass != 0 ||
           data.TailShape != 0 ||
           data.BustSize != 0 ||
           data.FacePaint != 0;

    private static string BuildAppearanceFieldSummary(ActorAppearanceData appearance)
        => $"spawnKind={appearance.SpawnKind},sourceModelChara={appearance.ModelCharaId},overrideModelChara={appearance.ModelCharaOverrideId},effectiveModelChara={EffectiveModelCharaId(appearance)},customizeFields={CountCustomizeFields(appearance.Customize)}/26,equipmentSlots={CountEquipment(appearance.Equipment)}/12,hideWeapons={appearance.Equipment.HideWeapons},hideHeadgear={appearance.Equipment.HideHeadgear}";

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

    private static bool TryBuildCustomizeBytes(ActorCustomizeData customize, out byte[] bytes, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(customize.RawCustomizeBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(customize.RawCustomizeBase64);
                if (raw.Length < 26)
                {
                    bytes = [];
                    reason = $"raw customize array is too short: {raw.Length}/26";
                    return false;
                }

                bytes = raw.Take(26).ToArray();
                reason = "rawCustomizeBase64";
                return true;
            }
            catch (Exception ex)
            {
                bytes = [];
                reason = $"raw customize array decode failed: {ex.Message}";
                return false;
            }
        }

        bytes =
        [
            customize.Race,
            customize.Sex,
            customize.BodyType == 0 ? (byte)1 : customize.BodyType,
            customize.Height,
            customize.Tribe,
            customize.Face,
            customize.HairStyle,
            customize.Highlights,
            customize.SkinColor,
            customize.EyeColorRight,
            customize.HairColor,
            customize.HighlightsColor,
            customize.FacialFeatures,
            customize.FacialFeaturesColor,
            customize.Eyebrows,
            customize.EyeColorLeft,
            customize.EyeShape,
            customize.Nose,
            customize.Jaw,
            customize.Lipstick,
            customize.LipColorFurPattern,
            customize.MuscleMass,
            customize.TailShape,
            customize.BustSize,
            customize.FacePaint,
            customize.FacePaintColor,
        ];
        reason = "structuredCustomize";
        return true;
    }

    private unsafe static bool VerifyApplied(Character* character, ActorAppearanceData appearance, out string reason)
    {
        var mismatches = new List<string>();
        var isCharacter = appearance.SpawnKind == ActorSpawnKind.Character;
        var effectiveModelCharaId = EffectiveModelCharaId(appearance);
        if ((effectiveModelCharaId != 0 || !isCharacter) &&
            character->ModelContainer.ModelCharaId != (int)effectiveModelCharaId)
        {
            mismatches.Add($"ModelChara expected={effectiveModelCharaId} actual={character->ModelContainer.ModelCharaId}");
        }

        if (appearance.ModelSkeletonId != 0 &&
            character->ModelContainer.ModelSkeletonId != (int)appearance.ModelSkeletonId)
        {
            mismatches.Add($"ModelSkeleton expected={appearance.ModelSkeletonId} actual={character->ModelContainer.ModelSkeletonId}");
        }

        if (isCharacter)
        {
            if (!TryBuildCustomizeBytes(appearance.Customize, out var expectedCustomize, out var customizeReason))
            {
                reason = customizeReason;
                return false;
            }

            for (var index = 0; index < expectedCustomize.Length; index++)
            {
                var actual = character->DrawData.CustomizeData.Data[index];
                if (actual != expectedCustomize[index])
                    mismatches.Add($"Customize[{(CustomizeIndex)index}] expected={expectedCustomize[index]} actual={actual}");
            }
        }

        if (isCharacter)
        {
            VerifyWeapon(character, WeaponSlot.MainHand, appearance.Equipment.MainHand, "MainHand", mismatches);
            VerifyWeapon(character, WeaponSlot.OffHand, appearance.Equipment.OffHand, "OffHand", mismatches);
            VerifyGear(character, EquipmentSlot.Head, appearance.Equipment.Head, "Head", mismatches);
            VerifyGear(character, EquipmentSlot.Body, appearance.Equipment.Body, "Body", mismatches);
            VerifyGear(character, EquipmentSlot.Hands, appearance.Equipment.Hands, "Hands", mismatches);
            VerifyGear(character, EquipmentSlot.Legs, appearance.Equipment.Legs, "Legs", mismatches);
            VerifyGear(character, EquipmentSlot.Feet, appearance.Equipment.Feet, "Feet", mismatches);
            VerifyGear(character, EquipmentSlot.Ears, appearance.Equipment.Ears, "Ears", mismatches);
            VerifyGear(character, EquipmentSlot.Neck, appearance.Equipment.Neck, "Neck", mismatches);
            VerifyGear(character, EquipmentSlot.Wrists, appearance.Equipment.Wrists, "Wrists", mismatches);
            VerifyGear(character, EquipmentSlot.LFinger, appearance.Equipment.LeftRing, "LeftRing", mismatches);
            VerifyGear(character, EquipmentSlot.RFinger, appearance.Equipment.RightRing, "RightRing", mismatches);
        }

        if (mismatches.Count > 0)
        {
            reason = $"readback mismatch count={mismatches.Count}: {string.Join("; ", mismatches.Take(10))}";
            return false;
        }

        reason = $"readback ok; {BuildAppearanceFieldSummary(appearance)}";
        return true;
    }

    private unsafe static void VerifyWeapon(Character* character, WeaponSlot slot, ActorWeaponModelData? expected, string label, List<string> mismatches)
    {
        if (expected == null)
            return;

        var actual = character->DrawData.Weapon(slot).ModelId;
        if (actual.Id != expected.ModelSetId ||
            actual.Type != expected.Base ||
            actual.Variant != expected.Variant ||
            actual.Stain0 != expected.Stain0 ||
            actual.Stain1 != expected.Stain1)
        {
            mismatches.Add($"{label} expected={FormatWeapon(expected)} actual={FormatWeapon(actual)}");
        }
    }

    private unsafe static void VerifyGear(Character* character, EquipmentSlot slot, ActorEquipmentModelData? expected, string label, List<string> mismatches)
    {
        if (expected == null)
            return;

        var actual = character->DrawData.Equipment(slot);
        if (actual.Id != expected.ModelId ||
            actual.Variant != expected.Variant ||
            actual.Stain0 != expected.Stain0 ||
            actual.Stain1 != expected.Stain1)
        {
            mismatches.Add($"{label} expected={FormatGear(expected)} actual={FormatGear(actual)}");
        }
    }

    private unsafe static string BuildNativeAppearanceSummary(Character* character)
    {
        var customize = character->DrawData.CustomizeData.Data;
        return $"modelChara={character->ModelContainer.ModelCharaId}, modelSkeleton={character->ModelContainer.ModelSkeletonId}, customize=R{customize[(int)CustomizeIndex.Race]}/S{customize[(int)CustomizeIndex.Sex]}/T{customize[(int)CustomizeIndex.Tribe]}/Face{customize[(int)CustomizeIndex.Face]}/Hair{customize[(int)CustomizeIndex.HairStyle]}, main={FormatWeapon(character->DrawData.Weapon(WeaponSlot.MainHand).ModelId)}, off={FormatWeapon(character->DrawData.Weapon(WeaponSlot.OffHand).ModelId)}, head={FormatGear(character->DrawData.Equipment(EquipmentSlot.Head))}, body={FormatGear(character->DrawData.Equipment(EquipmentSlot.Body))}";
    }

    private static string FormatWeapon(ActorWeaponModelData model)
        => $"{model.ModelSetId}/{model.Base}/{model.Variant}/{model.Stain0}/{model.Stain1}";

    private static string FormatWeapon(WeaponModelId model)
        => $"{model.Id}/{model.Type}/{model.Variant}/{model.Stain0}/{model.Stain1}";

    private static string FormatGear(ActorEquipmentModelData model)
        => $"{model.ModelId}/{model.Variant}/{model.Stain0}/{model.Stain1}";

    private static string FormatGear(EquipmentModelId model)
        => $"{model.Id}/{model.Variant}/{model.Stain0}/{model.Stain1}";

    private bool FailActorAppearance(RuntimeActorInstance actor, string method, string reason)
    {
        actor.LastAppearanceMethod = method;
        actor.LastAppearanceError = reason;
        actor.LastAppearanceApplyResult = reason;
        actor.LastAppearanceAppliedAt = DateTime.Now;
        actor.LastAppearanceVerificationState = $"AppearanceFailed: {reason}";
        actor.RuntimeAppearanceApplied = false;
        return false;
    }

    private bool ApplyNone(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "None";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = "未设置外观来源，跳过处理。";
        return true;
    }

    private bool ApplyCurrentPlayer(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "CurrentPlayer";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = "保持 Brio CreateCharacter 生成的玩家 clone 外观。";
        return true;
    }

    private bool ApplyPenumbraAppearanceSource(RuntimeActorInstance actor)
    {
        actor.LastAppearanceMethod = "PenumbraCollection";
        actor.LastAppearanceError = actor.LastPenumbraCollectionError;
        actor.LastAppearanceApplyResult = string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionResult)
            ? "Penumbra collection handled by NPC-level collection stage."
            : actor.LastPenumbraCollectionResult;
        return string.IsNullOrWhiteSpace(actor.LastPenumbraCollectionError);
    }

    private bool ApplyGlamourerDesign(CustomNpcAppearance appearance, RuntimeActorInstance actor)
    {
        actor.AppearanceSourceType = CustomNpcAppearanceSourceType.GlamourerDesign.ToString();
        actor.GlamourerDesignId = appearance.GlamourerDesignId;
        actor.GlamourerDesignName = appearance.DisplayName;
        actor.GlamourerDesignPath = appearance.Notes;
        actor.GlamourerIpcAvailable = this.glamourerIpcBridge.IsApplyDesignRegistered;

        if (string.IsNullOrWhiteSpace(appearance.GlamourerDesignId))
            return this.FailGlamourer(actor, "Glamourer design id is empty.");

        if (actor.CharacterObject == null || !actor.IsValid || actor.IsStale)
            return this.FailGlamourer(actor, $"Runtime actor is not ready for Glamourer apply. valid={actor.IsValid}, stale={actor.IsStale}, characterObject={actor.CharacterObject != null}");

        if (!int.TryParse(actor.ObjectIndex, out var objectIndex) || objectIndex < 0)
            return this.FailGlamourer(actor, $"Runtime actor ObjectIndex is unavailable: {actor.ObjectIndex}");

        if (!this.glamourerIpcBridge.IsApplyDesignRegistered)
            this.glamourerIpcBridge.Probe();
        actor.GlamourerIpcAvailable = this.glamourerIpcBridge.IsApplyDesignRegistered;

        if (!this.glamourerIpcBridge.ApplyDesignToObject(appearance.GlamourerDesignId, objectIndex, out var applyReason))
            return this.FailGlamourer(actor, applyReason);

        var redraw = "post-preset redraw skipped; design was applied directly to the spawned runtime actor object index.";
        var ipc = this.glamourerIpcBridge.SelectedApplyDesign;
        actor.LastAppearanceMethod = $"GlamourerDesign via {ipc?.Name ?? "unknown"}";
        actor.LastAppearanceError = string.Empty;
        actor.LastGlamourerApplyError = string.Empty;
        actor.LastGlamourerApplyStatus = $"Applied design={appearance.GlamourerDesignId}, name={appearance.DisplayName}, targetObjectIndex={objectIndex}, targetAddress={actor.Address}, ipcAvailable={actor.GlamourerIpcAvailable}. {applyReason}";
        actor.LastAppearanceApplyResult = $"Glamourer design applied to spawned Actor. {actor.LastGlamourerApplyStatus}. {redraw}";
        return true;
    }

    private bool ApplyGameNpc(CustomNpcAppearance appearance, RuntimeActorInstance actor)
    {
        var resolution = this.gameNpcResolver.Resolve(appearance);
        var chain = string.Join(" -> ", resolution.Chain);
        if (!resolution.Success)
            return this.Fail(actor, "GameNpc", $"GameNpc 外观解析失败。RowId={appearance.GameNpcBaseId}，失败步骤={resolution.FailureStep}，解析链={chain}");

        if (!this.gameNpcApplyService.TryApplyModelChara(actor, resolution, out var applyReason))
        {
            actor.LastAppearanceMethod = "GameNpc";
            actor.LastAppearanceError = applyReason;
            actor.LastAppearanceApplyResult = $"{resolution.Message} Kind={resolution.Appearance.Kind}, ModelCharaId={resolution.ModelCharaId}。解析链={chain}。应用结果：{applyReason}";
            return false;
        }

        var redraw = "post-preset Penumbra redraw skipped; collection redraw happens before preset apply.";
        actor.LastAppearanceMethod = "GameNpc";
        actor.LastAppearanceError = string.Empty;
        actor.LastAppearanceApplyResult = $"GameNpc 外观已应用。Kind={resolution.Appearance.Kind}, ModelCharaId={resolution.ModelCharaId}。{applyReason} {redraw} 解析链={chain}";
        return true;
    }

    private string TryPenumbraRedraw(int objectIndex)
    {
        var ipc = this.glamourerIpcProbe.SelectedPenumbraRedraw;
        if (ipc == null)
            return "尚未发现 Penumbra Redraw IPC。请先手动点击“探测 Glamourer IPC”。";

        try
        {
            var result = ipc.InvocationKind switch
            {
                GlamourerIpcInvocationKind.RedrawIntBool => this.pluginInterface.GetIpcSubscriber<int, bool>(ipc.Name).InvokeFunc(objectIndex),
                GlamourerIpcInvocationKind.RedrawUshortBool => TryObjectIndexToUshort(objectIndex, out var index) && this.pluginInterface.GetIpcSubscriber<ushort, bool>(ipc.Name).InvokeFunc(index),
                GlamourerIpcInvocationKind.RedrawIntObject => this.pluginInterface.GetIpcSubscriber<int, object>(ipc.Name).InvokeFunc(objectIndex),
                GlamourerIpcInvocationKind.RedrawUshortObject => TryObjectIndexToUshort(objectIndex, out var index) ? this.pluginInterface.GetIpcSubscriber<ushort, object>(ipc.Name).InvokeFunc(index) : false,
                _ => false,
            };

            return $"Penumbra redraw={result}，IPC={ipc.Name}，参数类型={ipc.Signature}。";
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Penumbra redraw failed. Ipc={IpcName}, Signature={Signature}", ipc.Name, ipc.Signature);
            return $"Penumbra redraw 调用失败：IPC={ipc.Name}，参数类型={ipc.Signature}，异常={ex.Message}";
        }
    }

    private bool Fail(RuntimeActorInstance actor, string method, string reason)
    {
        actor.LastAppearanceMethod = method;
        actor.LastAppearanceError = reason;
        actor.LastAppearanceApplyResult = reason;
        return false;
    }

    private bool FailGlamourer(RuntimeActorInstance actor, string reason)
    {
        actor.LastAppearanceMethod = "GlamourerDesign";
        actor.LastAppearanceError = reason;
        actor.LastAppearanceApplyResult = reason;
        actor.LastGlamourerApplyError = reason;
        actor.LastGlamourerApplyStatus = $"Failed. design={actor.GlamourerDesignId}, name={actor.GlamourerDesignName}, targetObjectIndex={actor.ObjectIndex}, targetAddress={actor.Address}, ipcAvailable={this.glamourerIpcBridge.IsApplyDesignRegistered}. {reason}";
        actor.GlamourerIpcAvailable = this.glamourerIpcBridge.IsApplyDesignRegistered;
        return false;
    }

    private string DescribeGameNpcPreset(CustomNpcAppearance appearance)
    {
        var resolution = this.gameNpcResolver.Resolve(appearance);
        if (!resolution.Success)
            return $"source=GameNpc; hasEquipment=false; fullDesign=false; resolveFailed={resolution.FailureStep}; message={resolution.Message}";

        if (resolution.Appearance.Kind != GameNpcResolvedAppearanceKind.Humanoid)
            return $"source=GameNpc; kind={resolution.Appearance.Kind}; modelChara={resolution.ModelCharaId}; hasEquipment=false; fullDesign=false";

        var customize = BuildCustomizeParts(resolution.Appearance.Customize);
        var equipment = BuildEquipmentParts(resolution.Appearance.Equipment);
        return $"source=GameNpc; kind=Humanoid; hasEquipment=true; fullDesign=true; customizeHash={StableHash(string.Join(';', customize))}; equipmentHash={StableHash(string.Join(';', equipment))}; equipment={string.Join(',', equipment)}";
    }

    private static string DescribeGlamourerDesignPreset(CustomNpcAppearance appearance)
    {
        var path = appearance.Notes;
        if (string.IsNullOrWhiteSpace(path))
            return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment=unknown; fullDesign=unknown; designPath=missing";

        if (!File.Exists(path))
            return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment=unknown; fullDesign=unknown; designPathNotFound={path}";

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var hasEquipment = TryFindProperty(document.RootElement, "Equipment", out var equipmentElement);
        var hasCustomize = TryFindProperty(document.RootElement, "Customize", out var customizeElement) ||
                           TryFindProperty(document.RootElement, "Customization", out customizeElement);
        var equipmentRaw = hasEquipment ? NormalizeJson(equipmentElement) : string.Empty;
        var customizeRaw = hasCustomize ? NormalizeJson(customizeElement) : string.Empty;
        var slots = hasEquipment ? string.Join(',', ReadJsonSlotNames(equipmentElement)) : "none";
        return $"source=GlamourerDesign; design={appearance.GlamourerDesignId}; hasEquipment={hasEquipment}; hasCustomize={hasCustomize}; fullDesign={hasEquipment && hasCustomize}; equipmentHash={StableHash(equipmentRaw)}; customizeHash={StableHash(customizeRaw)}; slots={slots}; path={path}";
    }

    private static IEnumerable<string> BuildCustomizeParts(GameNpcResolvedCustomize customize)
    {
        yield return $"Race={customize.Race}";
        yield return $"Gender={customize.Gender}";
        yield return $"Tribe={customize.Tribe}";
        yield return $"BodyType={customize.BodyType}";
        yield return $"Height={customize.Height}";
        yield return $"Face={customize.Face}";
        yield return $"HairStyle={customize.HairStyle}";
        yield return $"SkinColor={customize.SkinColor}";
        yield return $"EyeColor={customize.EyeColor}";
    }

    private static IEnumerable<string> BuildEquipmentParts(GameNpcResolvedEquipment equipment)
    {
        yield return $"MainHand={equipment.MainHand}";
        yield return $"OffHand={equipment.OffHand}";
        yield return $"Head={equipment.Head}";
        yield return $"Body={equipment.Body}";
        yield return $"Hands={equipment.Hands}";
        yield return $"Legs={equipment.Legs}";
        yield return $"Feet={equipment.Feet}";
        yield return $"Ears={equipment.Ears}";
        yield return $"Neck={equipment.Neck}";
        yield return $"Wrists={equipment.Wrists}";
        yield return $"LeftRing={equipment.LeftRing}";
        yield return $"RightRing={equipment.RightRing}";
    }

    private static bool TryFindProperty(JsonElement element, string propertyName, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    found = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, propertyName, out found))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyName, out found))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static IEnumerable<string> ReadJsonSlotNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return ["<non-object-equipment>"];

        return element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
    }

    private static string NormalizeJson(JsonElement element)
        => JsonSerializer.Serialize(element);

    private static string StableHash(string text)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= prime;
            }

            return hash.ToString("X16");
        }
    }

    private static bool TryObjectIndexToUshort(int objectIndex, out ushort value)
    {
        if (objectIndex is >= ushort.MinValue and <= ushort.MaxValue)
        {
            value = (ushort)objectIndex;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadAddress(RuntimeActorInstance actor, out nint address)
    {
        address = 0;
        var raw = actor.Address?.Trim() ?? string.Empty;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            address = (nint)hex;
            return true;
        }

        if (ulong.TryParse(raw, out var value))
        {
            address = (nint)value;
            return true;
        }

        return false;
    }
}
