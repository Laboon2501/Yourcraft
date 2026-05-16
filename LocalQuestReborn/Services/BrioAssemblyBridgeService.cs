using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace LocalQuestReborn.Services;

public sealed class BrioAssemblyBridgeService
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly RealNpcNameService nameService;
    private readonly Dictionary<string, object> spawnedCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RealSpawnedNpc> spawnedNpcs = new(StringComparer.OrdinalIgnoreCase);

    public BrioAssemblyBridgeService(IObjectTable objectTable, IPluginLog log, RealNpcNameService nameService)
    {
        this.objectTable = objectTable;
        this.log = log;
        this.nameService = nameService;
    }

    public string LastReflectionError { get; private set; } = string.Empty;

    public string LastMessage { get; private set; } = "尚未尝试 Brio assembly 反射生成。";

    public IReadOnlyList<SpawnFlagInfo> SpawnFlags { get; private set; } = [];

    public SpawnFlagInfo? SelectedSpawnFlag { get; private set; }

    public string LastCreateCharacterSignatureKind { get; private set; } = "未调用";

    public string LastCreateCharacterResult { get; private set; } = "未调用";

    public bool LastCreateCharacterReturnedNull { get; private set; } = true;

    public string LastSpawnedObjectIndex { get; private set; } = "不可用";

    public string LastSpawnedAddress { get; private set; } = "不可用";

    public string LastSpawnedPosition { get; private set; } = "不可用";

    public string LastPositionError { get; private set; } = string.Empty;

    public string LastDespawnError { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, RealSpawnedNpc> SpawnedNpcs => this.spawnedNpcs;

    public bool EnableUnsafeNativeWrites { get; set; }

    public bool IsBrioAssemblyLoaded => this.FindBrioAssembly() != null;

    public unsafe bool CanSpawnNativeActor => ClientObjectManager.Instance() != null;

    public string ActorSpawnStatusText => this.CanSpawnNativeActor
        ? "ARR-style native ClientObjectManager actor spawn is available."
        : "Native actor spawn is waiting for ClientObjectManager.";

    public string LocalPlayerStatusText
    {
        get
        {
            if (this.TryReadLocalPlayerPosition(out var position, out var reason))
            {
                var localPlayer = this.objectTable.LocalPlayer;
                return $"ObjectTable.LocalPlayer available at {position}; objectIndex={ReadProperty(localPlayer!, "ObjectIndex")}; address={ReadProperty(localPlayer!, "Address")}";
            }

            return reason;
        }
    }

    public bool TryReadLocalPlayerPosition(out Vector3 position, out string reason)
    {
        position = Vector3.Zero;
        var localPlayer = this.objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            reason = "ObjectTable.LocalPlayer is null.";
            return false;
        }

        position = localPlayer.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            reason = $"ObjectTable.LocalPlayer position is invalid: {position}";
            return false;
        }

        reason = $"ObjectTable.LocalPlayer position available. objectIndex={ReadProperty(localPlayer, "ObjectIndex")}; address={ReadProperty(localPlayer, "Address")}.";
        return true;
    }

    public bool HasActorSpawnService
    {
        get
        {
            return this.TryGetActorSpawnService(out _, out _);
        }
    }

    public string StatusText
    {
        get
        {
            if (!this.IsBrioAssemblyLoaded)
                return "未发现已加载的 Brio assembly。请确认 Brio 插件已启用。";

            return this.HasActorSpawnService
                ? "已找到 Brio assembly 和 ActorSpawnService。"
                : $"已找到 Brio assembly，但无法取得 ActorSpawnService：{this.LastReflectionError}";
        }
    }

    public bool TrySpawnActor(PersistentActorConfig config, out RuntimeActorInstance instance, out string reason, Vector3? initialWorldPosition = null, float? initialYawRadians = null)
    {
        var displayName = string.IsNullOrWhiteSpace(config.DisplayName)
            ? string.IsNullOrWhiteSpace(config.NpcNameSnapshot) ? "Actor" : config.NpcNameSnapshot
            : config.DisplayName;
        instance = new RuntimeActorInstance
        {
            RuntimeId = config.RuntimeId,
            ConfigId = config.ConfigId,
            NpcId = config.SourceNpcPresetId,
            TemplateNpcId = config.SourceNpcPresetId,
            NpcName = config.NpcNameSnapshot,
            DisplayName = displayName,
            ExpectedName = displayName,
            DesiredDisplayName = displayName,
            SpawnSource = "ARRNative",
            SpawnTime = DateTime.Now,
            LastKnownPosition = initialWorldPosition ?? new Vector3(config.WorldPosition.X, config.WorldPosition.Y, config.WorldPosition.Z),
            SpawnKind = config.SpawnKind == ActorSpawnKind.Unknown ? config.Appearance.SpawnKind : config.SpawnKind,
            SourceActorKind = string.IsNullOrWhiteSpace(config.SourceActorKind) ? config.Appearance.SourceActorKind : config.SourceActorKind,
        };

        try
        {
            var spawnPosition = initialWorldPosition ?? instance.LastKnownPosition;
            var spawnYaw = initialYawRadians ?? 0f;
            var localPlayerStatus = this.LocalPlayerStatusText;
            this.log.Information(
                "[ActorSpawn] Native spawn attempt config={ConfigId}, runtime={RuntimeId}, territory={Territory}, position={Position}, yaw={Yaw}, localPlayer={LocalPlayer}",
                config.ConfigId,
                config.RuntimeId,
                config.TerritoryType,
                spawnPosition,
                spawnYaw,
                localPlayerStatus);

            if (!this.TrySpawnNativeActor(instance, displayName, spawnPosition, spawnYaw, config.Appearance, out reason))
            {
                instance.LastError = reason;
                this.log.Warning(
                    "[ActorSpawn] Native spawn failed config={ConfigId}, runtime={RuntimeId}, territory={Territory}, position={Position}, reason={Reason}, localPlayer={LocalPlayer}",
                    config.ConfigId,
                    config.RuntimeId,
                    config.TerritoryType,
                    spawnPosition,
                    reason,
                    localPlayerStatus);
                return false;
            }

            this.LastCreateCharacterSignatureKind = "ClientObjectManager.CreateBattleCharacter";
            this.LastCreateCharacterResult = "native";
            this.LastCreateCharacterReturnedNull = instance.CharacterObject == null;
            this.LastSpawnedObjectIndex = instance.ObjectIndex;
            this.LastSpawnedAddress = instance.Address;
            this.LastSpawnedPosition = instance.LastKnownPosition.ToString();
            this.LastPositionError = string.Empty;
            this.LastMessage = reason;
            this.LastReflectionError = string.Empty;
            this.log.Information("[ActorSpawn] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Native actor spawn failed: {ex.Message}";
            instance.LastError = reason;
            this.LastReflectionError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to spawn runtime actor via native ClientObjectManager. RuntimeId={RuntimeId}", config.RuntimeId);
            return false;
        }
    }

    public bool TrySpawnActor(CustomNpc npc, string runtimeId, out RuntimeActorInstance instance, out string reason, Vector3? initialWorldPosition = null, float? initialYawRadians = null)
    {
        instance = new RuntimeActorInstance
        {
            RuntimeId = runtimeId,
            NpcId = npc.Id,
            TemplateNpcId = npc.Id,
            NpcName = npc.Name,
            DisplayName = npc.Name,
            ExpectedName = npc.Name,
            DesiredDisplayName = FormatDisplayName(npc),
            SpawnSource = "BrioAssembly",
            SpawnTime = DateTime.Now,
            LastKnownPosition = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z),
        };

        try
        {
            if (!this.TryGetActorSpawnService(out var actorSpawnService, out reason) || actorSpawnService == null)
            {
                instance.LastError = reason;
                return false;
            }

            var localPlayer = this.objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                reason = "当前 LocalPlayer 不可用。";
                instance.LastError = reason;
                return false;
            }

            var brioAssembly = this.FindBrioAssembly();
            var spawnFlagsType = brioAssembly?.GetType("Brio.Game.Actor.SpawnFlags");
            if (spawnFlagsType == null)
            {
                reason = "无法找到 Brio.Game.Actor.SpawnFlags。";
                instance.LastError = reason;
                return false;
            }

            var spawnFlags = this.ReadSpawnFlags(spawnFlagsType);
            this.SpawnFlags = spawnFlags;
            var selectedSpawnFlag = this.SelectSpawnFlag(spawnFlagsType, spawnFlags, out var selectedSpawnFlagInfo);
            this.SelectedSpawnFlag = selectedSpawnFlagInfo;

            var spawnPosition = initialWorldPosition ?? localPlayer.Position + new Vector3(npc.DefaultSpawnOffset.X, npc.DefaultSpawnOffset.Y, npc.DefaultSpawnOffset.Z);
            instance.LastKnownPosition = spawnPosition;
            var createMethods = actorSpawnService.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "CreateCharacter")
                .ToList();
            var createCharacter = createMethods.FirstOrDefault(method => IsSixParameterCreateCharacter(method)) ??
                createMethods.FirstOrDefault(method => IsThreeParameterCreateCharacter(method));

            if (createCharacter == null)
            {
                reason = $"未找到签名匹配的 ActorSpawnService.CreateCharacter。实际候选：{string.Join(" | ", createMethods.Select(DescribeMethod))}";
                instance.LastError = reason;
                return false;
            }

            this.LogCreateCharacterDiagnostics(createCharacter, spawnFlagsType);

            var isSixParameter = IsSixParameterCreateCharacter(createCharacter);
            this.LastCreateCharacterSignatureKind = isSixParameter ? "6 参数 CreateCharacter" : "3 参数 CreateCharacter";
            var args = isSixParameter
                ? new object?[] { null, selectedSpawnFlag, true, spawnPosition, initialYawRadians ?? 0f, string.IsNullOrWhiteSpace(instance.DesiredDisplayName) ? npc.Id : instance.DesiredDisplayName }
                : new object?[] { null, selectedSpawnFlag, true };

            var result = createCharacter.Invoke(actorSpawnService, args);
            var success = result is bool b && b;
            this.LastCreateCharacterResult = result?.ToString() ?? "null";
            this.LastCreateCharacterReturnedNull = args[0] == null;
            if (!success || args[0] == null)
            {
                reason = $"CreateCharacter 调用失败。Result={result ?? "null"}，CharacterIsNull={args[0] == null}，Method={DescribeMethod(createCharacter)}，Args={DescribeArgs(args)}";
                instance.LastError = reason;
                this.log.Warning("[BrioAssemblyBridgeService] {Reason}", reason);
                return false;
            }

            var character = args[0]!;
            if (IsLocalPlayerCharacter(character, localPlayer, out var localPlayerReason))
            {
                reason = $"CreateCharacter returned LocalPlayer instead of a new runtime actor; refusing to bind/apply. {localPlayerReason}";
                instance.LastError = reason;
                this.LastReflectionError = reason;
                this.LastMessage = reason;
                this.log.Error("[ActorSpawn] rejected LocalPlayer fallback. runtime={RuntimeId}, npc={NpcId}, reason={Reason}", runtimeId, npc.Id, reason);
                return false;
            }

            var nativeName = this.nameService.TryReadNativeName(character);
            var positionMessage = "安全模式：Spawn 后未设置位置、名称或外观。";

            this.FillInstanceFromCharacter(instance, character);
            instance.NativeNameSet = false;
            instance.CurrentNativeName = nativeName;
            instance.NativeNameReadback = nativeName;
            instance.LastError = "安全模式：未设置原生名称。";

            this.LastSpawnedObjectIndex = instance.ObjectIndex;
            this.LastSpawnedAddress = instance.Address;
            this.LastSpawnedPosition = instance.LastKnownPosition.ToString();
            this.LastPositionError = string.Empty;
            reason = $"Brio Assembly 已生成 Actor：runtimeId={runtimeId[..Math.Min(8, runtimeId.Length)]}，npc={npc.Name}，ObjectIndex={instance.ObjectIndex}，Address={instance.Address}。{positionMessage}";
            this.LastMessage = reason;
            this.LastReflectionError = string.Empty;
            this.log.Information("[BrioAssemblyBridgeService] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio Assembly 生成 Actor 失败：{ex.Message}";
            instance.LastError = reason;
            this.LastReflectionError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to spawn runtime actor via Brio assembly. RuntimeId={RuntimeId}, NpcId={NpcId}", runtimeId, npc.Id);
            return false;
        }
    }

    public bool TrySpawnProp(CustomProp prop, string runtimeId, bool usePropFlags, out RuntimePropInstance instance, out string reason)
    {
        instance = new RuntimePropInstance
        {
            RuntimeId = runtimeId,
            PropId = prop.Id,
            PropName = prop.Name,
            ModelPath = prop.ModelPath,
            Position = new Vector3(prop.Position.X, prop.Position.Y, prop.Position.Z),
            Rotation = prop.Rotation,
            Scale = prop.Scale,
            SpawnMethod = usePropFlags ? "Brio SpawnFlags.Prop/IsProp" : "Brio 默认 Character clone",
        };

        try
        {
            if (!this.TryGetActorSpawnService(out var actorSpawnService, out reason) || actorSpawnService == null)
            {
                instance.LastError = reason;
                return false;
            }

            var brioAssembly = this.FindBrioAssembly();
            var spawnFlagsType = brioAssembly?.GetType("Brio.Game.Actor.SpawnFlags");
            if (spawnFlagsType == null)
            {
                reason = "无法找到 Brio.Game.Actor.SpawnFlags。";
                instance.LastError = reason;
                return false;
            }

            var spawnFlags = this.ReadSpawnFlags(spawnFlagsType);
            this.SpawnFlags = spawnFlags;
            var selectedSpawnFlag = usePropFlags
                ? this.SelectSpawnFlagByNames(spawnFlagsType, spawnFlags, out var selectedSpawnFlagInfo, "Prop", "IsProp", "Default", "CopyPosition")
                : this.SelectSpawnFlag(spawnFlagsType, spawnFlags, out selectedSpawnFlagInfo);
            this.SelectedSpawnFlag = selectedSpawnFlagInfo;

            var createMethods = actorSpawnService.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "CreateCharacter")
                .ToList();
            var createCharacter = createMethods.FirstOrDefault(method => IsSixParameterCreateCharacter(method)) ??
                createMethods.FirstOrDefault(method => IsThreeParameterCreateCharacter(method));

            if (createCharacter == null)
            {
                reason = $"未找到可用的 ActorSpawnService.CreateCharacter。候选：{string.Join(" | ", createMethods.Select(DescribeMethod))}";
                instance.LastError = reason;
                return false;
            }

            this.LogCreateCharacterDiagnostics(createCharacter, spawnFlagsType);

            var spawnPosition = new Vector3(prop.Position.X, prop.Position.Y, prop.Position.Z);
            var isSixParameter = IsSixParameterCreateCharacter(createCharacter);
            this.LastCreateCharacterSignatureKind = isSixParameter ? "6 参数 CreateCharacter" : "3 参数 CreateCharacter";
            var args = isSixParameter
                ? new object?[] { null, selectedSpawnFlag, true, spawnPosition, prop.Rotation, string.IsNullOrWhiteSpace(prop.Name) ? prop.Id : prop.Name }
                : new object?[] { null, selectedSpawnFlag, true };

            var result = createCharacter.Invoke(actorSpawnService, args);
            var success = result is bool b && b;
            this.LastCreateCharacterResult = result?.ToString() ?? "null";
            this.LastCreateCharacterReturnedNull = args[0] == null;
            if (!success || args[0] == null)
            {
                reason = $"CreateCharacter Prop 调用失败。Result={result ?? "null"}，CharacterIsNull={args[0] == null}，Method={DescribeMethod(createCharacter)}，Args={DescribeArgs(args)}";
                instance.LastError = reason;
                this.log.Warning("[BrioAssemblyBridgeService] {Reason}", reason);
                return false;
            }

            var character = args[0]!;
            this.FillPropInstanceFromCharacter(instance, character);

            this.LastSpawnedObjectIndex = instance.ObjectIndex;
            this.LastSpawnedAddress = instance.Address;
            this.LastSpawnedPosition = instance.Position.ToString();
            this.LastPositionError = string.Empty;

            reason = $"Brio Assembly 已生成 Prop 实验对象：runtimeId={runtimeId[..Math.Min(8, runtimeId.Length)]}，prop={prop.Name}，flag={selectedSpawnFlagInfo.Name}={selectedSpawnFlagInfo.Value}，objectIndex={instance.ObjectIndex}。模型路径尚未写入 DrawObject。";
            this.LastMessage = reason;
            this.LastReflectionError = string.Empty;
            this.log.Information("[BrioAssemblyBridgeService] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio Assembly 生成 Prop 失败：{ex.Message}";
            instance.LastError = reason;
            this.LastReflectionError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to spawn runtime prop via Brio assembly. RuntimeId={RuntimeId}, PropId={PropId}", runtimeId, prop.Id);
            return false;
        }
    }

    public bool TryDespawnProp(RuntimePropInstance instance, out string reason)
    {
        try
        {
            if (instance.CharacterObject == null)
            {
                reason = $"Runtime Prop {instance.RuntimeId} 没有 characterObject。";
                instance.IsValid = false;
                instance.LastError = reason;
                return true;
            }

            if (this.TryGetActorSpawnService(out var actorSpawnService, out reason) && actorSpawnService != null && this.TryInvokeDestroyObject(actorSpawnService, instance.CharacterObject, out reason))
            {
                instance.IsValid = false;
                instance.LastError = string.Empty;
                this.LastDespawnError = string.Empty;
                this.LastMessage = reason;
                return true;
            }

            reason = "DestroyObject 不可用或失败。安全模式下不会尝试 native fallback；已从 Prop registry 移除。";
            instance.IsValid = false;
            instance.LastError = reason;
            this.LastDespawnError = reason;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"删除 Prop 失败：{ex.Message}";
            instance.LastError = reason;
            this.LastDespawnError = reason;
            this.log.Error(ex, "Failed to despawn runtime prop {RuntimeId}", instance.RuntimeId);
            return false;
        }
    }

    public bool TryMoveProp(RuntimePropInstance instance, Vector3 position, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "Prop native 移动已禁用，避免 AccessViolation 崩溃。";
            instance.LastError = reason;
            this.LastPositionError = reason;
            return false;
        }

        if (instance.CharacterObject == null)
        {
            reason = "characterObject 不可用。";
            instance.LastError = reason;
            return false;
        }

        if (!this.TrySetActorPositionFromAddress(instance.CharacterObject, position, out var error))
        {
            reason = $"Prop 位置设置失败：{error}";
            instance.LastError = reason;
            this.LastPositionError = reason;
            return false;
        }

        this.RefreshProp(instance);
        instance.Position = position;
        instance.LastError = string.Empty;
        this.LastPositionError = string.Empty;
        reason = $"已移动 Prop 到 X {position.X:F2}, Y {position.Y:F2}, Z {position.Z:F2}";
        return true;
    }

    public void RefreshProp(RuntimePropInstance instance)
    {
        if (instance.CharacterObject == null)
        {
            instance.IsValid = false;
            return;
        }

        this.FillPropInstanceFromCharacter(instance, instance.CharacterObject);
    }

    public bool TryDespawnActor(RuntimeActorInstance instance, out string reason)
    {
        try
        {
            if (instance.CharacterObject == null)
            {
                reason = $"Runtime Actor {instance.RuntimeId} 没有 characterObject。";
                instance.IsValid = false;
                instance.LastError = reason;
                return true;
            }

            if (this.TryDeleteNativeActor(instance, out reason))
            {
                instance.IsValid = false;
                instance.LastError = string.Empty;
                this.LastDespawnError = string.Empty;
                this.LastMessage = reason;
                return true;
            }

            if (this.TryGetActorSpawnService(out var actorSpawnService, out reason) && actorSpawnService != null && this.TryInvokeDestroyObject(actorSpawnService, instance.CharacterObject, out reason))
            {
                instance.IsValid = false;
                instance.LastError = string.Empty;
                this.LastDespawnError = string.Empty;
                this.LastMessage = reason;
                return true;
            }

            reason = "DestroyObject 不可用或失败。安全模式下不会移动到地下；已从 registry 移除，可能需要重载 Brio/切图清理残留 actor。";
            instance.IsValid = false;
            instance.LastError = reason;
            this.LastDespawnError = reason;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"删除 Actor 失败：{ex.Message}";
            instance.LastError = reason;
            this.LastDespawnError = reason;
            this.log.Error(ex, "Failed to despawn runtime actor {RuntimeId}", instance.RuntimeId);
            return false;
        }
    }

    public bool TryMoveActor(RuntimeActorInstance instance, Vector3 position, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "native 移动已禁用，避免 AccessViolation 崩溃。";
            instance.LastError = reason;
            this.LastPositionError = reason;
            return false;
        }

        if (instance.CharacterObject == null)
        {
            reason = "characterObject 不可用。";
            instance.LastError = reason;
            return false;
        }

        if (!this.TrySetActorPosition(instance.CharacterObject, position, out var error))
        {
            reason = $"位置设置失败：{error}";
            instance.LastError = reason;
            this.LastPositionError = reason;
            return false;
        }

        this.RefreshActor(instance);
        instance.LastKnownPosition = position;
        instance.LastError = string.Empty;
        this.LastPositionError = string.Empty;
        reason = $"已移动 Actor 到 X {position.X:F2}, Y {position.Y:F2}, Z {position.Z:F2}";
        return true;
    }

    public bool TryMoveActorWithNativeSetPosition(RuntimeActorInstance instance, Vector3 position, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "native 移动已禁用，避免 AccessViolation 崩溃。Brio Capability 移动失败，需 native SetPosition 路径。";
            instance.LastError = reason;
            instance.LastMoveMethod = "Disabled";
            instance.LastMoveError = reason;
            this.LastPositionError = reason;
            return false;
        }

        if (instance.CharacterObject == null)
        {
            reason = "characterObject 不可用。";
            instance.LastError = reason;
            instance.LastMoveMethod = "Native SetPosition Failed";
            instance.LastMoveError = reason;
            return false;
        }

        var targetPosition = this.NormalizeMoveTarget(position);
        var beforePosition = instance.LastKnownPosition;
        if (TryReadVector3Property(instance.CharacterObject, "Position", out var readableBefore))
            beforePosition = readableBefore;

        instance.LastMoveBeforePosition = beforePosition;
        instance.LastMoveTargetPosition = targetPosition;

        if (!this.TrySetActorPositionFromAddress(instance.CharacterObject, targetPosition, out var error))
        {
            reason = $"位置设置失败：{error}";
            instance.LastError = reason;
            instance.LastMoveMethod = "Native SetPosition Failed";
            instance.LastMoveError = reason;
            this.LastPositionError = reason;
            return false;
        }

        this.RefreshActor(instance);
        var afterPosition = instance.LastKnownPosition;
        instance.LastMoveAfterPosition = afterPosition;
        instance.LastMoveActorValidAfter = instance.IsValid;
        instance.LastMoveDistanceReasonable = IsReasonableMoveResult(afterPosition, targetPosition);

        if (!instance.LastMoveDistanceReasonable)
        {
            this.TrySetActorPositionFromAddress(instance.CharacterObject, beforePosition, out _);
            this.RefreshActor(instance);
            instance.LastMoveAfterPosition = instance.LastKnownPosition;
            reason = $"移动后坐标异常，已尝试回滚。before={FormatPosition(beforePosition)}, target={FormatPosition(targetPosition)}, after={FormatPosition(afterPosition)}";
            instance.LastError = reason;
            instance.LastMoveMethod = "Rollback";
            instance.LastMoveError = reason;
            this.LastPositionError = reason;
            this.log.Warning("[BrioAssemblyBridgeService] Suspicious actor move result. {Reason}", reason);
            return false;
        }

        instance.LastKnownPosition = afterPosition;
        instance.LastMoveMethod = "Native SetPosition";
        instance.LastMoveError = string.Empty;
        instance.LastError = string.Empty;
        this.LastPositionError = string.Empty;
        reason = $"已通过 native SetPosition 移动 Actor。before={FormatPosition(beforePosition)}, target={FormatPosition(targetPosition)}, after={FormatPosition(afterPosition)}";
        return true;
    }

    public unsafe bool TrySetActorNativeYaw(RuntimeActorInstance instance, float yawRadians, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "native root yaw write disabled.";
            return false;
        }

        if (instance.IsStale || instance.CharacterObject == null)
        {
            reason = "actor stale or CharacterObject unavailable.";
            return false;
        }

        if (!TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = $"unable to read valid Address. Address={ReadProperty(instance.CharacterObject, "Address")}";
            return false;
        }

        if (!float.IsFinite(yawRadians))
        {
            reason = $"invalid yaw radians: {yawRadians}";
            return false;
        }

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
            native->GameObject.SetRotation(yawRadians);
            native->GameObject.RotationModified();
            reason = $"native root yaw set to {yawRadians:F4}.";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            this.log.Error(ex, "Native SetRotation failed. Address={Address}, Yaw={Yaw}", address, yawRadians);
            return false;
        }
    }

    public unsafe bool TrySetActorNativeScale(RuntimeActorInstance instance, Vector3 scale, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "native root scale write disabled.";
            return false;
        }

        if (!TryValidateSpawnedActorIndex(instance, out reason))
            return false;

        if (instance.IsStale || instance.CharacterObject == null)
        {
            reason = "actor stale or CharacterObject unavailable.";
            return false;
        }

        if (!TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = $"unable to read valid Address. Address={ReadProperty(instance.CharacterObject, "Address")}";
            return false;
        }

        var normalizedScale = NormalizeActorScale(scale);
        try
        {
            var native = (Character*)address;
            native->GameObject.Scale = normalizedScale.Y;
            reason = $"native root scale set to {normalizedScale.Y:F4}.";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            this.log.Error(ex, "Native SetScale failed. Address={Address}, Scale={Scale}", address, scale);
            return false;
        }
    }

    public unsafe bool TryApplyActorNativeRootTransform(RuntimeActorInstance instance, Vector3 position, float yawRadians, Vector3 scale, out string reason)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            reason = "native root transform write disabled.";
            return false;
        }

        if (!TryValidateSpawnedActorIndex(instance, out reason))
            return false;

        if (instance.IsStale || instance.CharacterObject == null)
        {
            reason = "actor stale or CharacterObject unavailable.";
            return false;
        }

        if (!TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = $"unable to read valid Address. Address={ReadProperty(instance.CharacterObject, "Address")}";
            return false;
        }

        if (!IsFiniteVector(position) || !float.IsFinite(yawRadians))
        {
            reason = $"invalid native transform. position={position}, yaw={yawRadians}";
            return false;
        }

        var normalizedScale = NormalizeActorScale(scale);
        try
        {
            var native = (Character*)address;
            native->GameObject.SetPosition(position.X, position.Y, position.Z);
            native->GameObject.SetRotation(yawRadians);
            native->GameObject.RotationModified();
            native->GameObject.Scale = normalizedScale.Y;

            if (!this.TryReadActorNativeTransform(instance, out var readPosition, out var readRotationEuler, out var readScale, out var readReason))
            {
                reason = $"native root transform wrote but readback failed: {readReason}";
                return false;
            }

            instance.LastKnownPosition = readPosition;
            instance.LastKnownRotationEuler = readRotationEuler;
            instance.LastKnownRotation = Quaternion.CreateFromYawPitchRoll(readRotationEuler.Y, readRotationEuler.X, readRotationEuler.Z);
            instance.LastKnownScale = readScale;
            reason = $"native root transform applied; readback position={readPosition}; rotationEuler={readRotationEuler}; scale={readScale}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            this.log.Error(ex, "Native root transform failed. Address={Address}, Position={Position}, Yaw={Yaw}, Scale={Scale}", address, position, yawRadians, scale);
            return false;
        }
    }

    public unsafe bool TryReadActorNativeTransform(RuntimeActorInstance instance, out Vector3 position, out Vector3 rotationEuler, out Vector3 scale, out string reason)
    {
        position = Vector3.Zero;
        rotationEuler = Vector3.Zero;
        scale = Vector3.One;

        if (!TryValidateSpawnedActorIndex(instance, out reason))
            return false;

        if (instance.IsStale || instance.CharacterObject == null)
        {
            reason = "actor stale or CharacterObject unavailable.";
            return false;
        }

        if (!TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = $"unable to read valid Address. Address={ReadProperty(instance.CharacterObject, "Address")}";
            return false;
        }

        try
        {
            var native = (Character*)address;
            position = native->GameObject.Position;
            var yaw = native->GameObject.Rotation;
            rotationEuler = new Vector3(0f, yaw, 0f);
            var nativeScale = MathF.Max(0.01f, native->GameObject.Scale);
            scale = new Vector3(nativeScale, nativeScale, nativeScale);

            instance.LastKnownPosition = position;
            instance.LastKnownRotationEuler = rotationEuler;
            instance.LastKnownRotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
            instance.LastKnownScale = scale;
            reason = $"native readback ok; yaw-only rotation readback. objectIndex={instance.ObjectIndex}, address={instance.Address}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            this.log.Warning(ex, "Native transform readback failed. RuntimeId={RuntimeId}, Address={Address}", instance.RuntimeId, instance.Address);
            return false;
        }
    }

    public bool RefreshActor(RuntimeActorInstance instance)
    {
        if (instance.IsStale)
        {
            instance.IsValid = false;
            return false;
        }

        if (instance.CharacterObject == null)
        {
            instance.IsValid = false;
            return false;
        }

        this.FillInstanceFromCharacter(instance, instance.CharacterObject);
        return instance.IsValid;
    }

    public bool SpawnSelectedNpcReflection(CustomNpc npc, out string reason)
    {
        try
        {
            if (!this.TryGetActorSpawnService(out var actorSpawnService, out reason) || actorSpawnService == null)
                return this.Fail(reason);

            var localPlayer = this.objectTable.LocalPlayer;
            if (localPlayer == null)
                return this.Fail("当前 LocalPlayer 不可用。", out reason);

            var brioAssembly = this.FindBrioAssembly();
            var spawnFlagsType = brioAssembly?.GetType("Brio.Game.Actor.SpawnFlags");
            if (spawnFlagsType == null)
                return this.Fail("无法找到 Brio.Game.Actor.SpawnFlags。", out reason);

            var spawnFlags = this.ReadSpawnFlags(spawnFlagsType);
            this.SpawnFlags = spawnFlags;
            var selectedSpawnFlag = this.SelectSpawnFlag(spawnFlagsType, spawnFlags, out var selectedSpawnFlagInfo);
            this.SelectedSpawnFlag = selectedSpawnFlagInfo;
            if (this.spawnedCharacters.ContainsKey(npc.Id))
                this.DespawnSelectedNpcReflection(npc.Id, out _);

            var spawnPosition = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);

            var createMethods = actorSpawnService.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "CreateCharacter")
                .ToList();
            var createCharacter = createMethods.FirstOrDefault(method => IsSixParameterCreateCharacter(method)) ??
                createMethods.FirstOrDefault(method => IsThreeParameterCreateCharacter(method));

            if (createCharacter == null)
            {
                var signatures = string.Join(" | ", createMethods.Select(DescribeMethod));
                return this.Fail($"未找到签名匹配的 ActorSpawnService.CreateCharacter。实际候选：{signatures}", out reason);
            }

            this.LogCreateCharacterDiagnostics(createCharacter, spawnFlagsType);

            var isSixParameter = IsSixParameterCreateCharacter(createCharacter);
            this.LastCreateCharacterSignatureKind = isSixParameter ? "6 参数 CreateCharacter" : "3 参数 CreateCharacter";
            var args = isSixParameter
                ? new object?[]
                {
                    null,
                    selectedSpawnFlag,
                    true,
                    spawnPosition,
                    0f,
                    string.IsNullOrWhiteSpace(npc.Name) ? npc.Id : npc.Name,
                }
                : new object?[]
                {
                    null,
                    selectedSpawnFlag,
                    true,
                };

            var result = createCharacter.Invoke(actorSpawnService, args);
            var success = result is bool b && b;
            this.LastCreateCharacterResult = result?.ToString() ?? "null";
            this.LastCreateCharacterReturnedNull = args[0] == null;
            if (!success || args[0] == null)
            {
                var failure = $"CreateCharacter 调用失败。Result={result ?? "null"}，CharacterIsNull={args[0] == null}，Method={DescribeMethod(createCharacter)}，Args={DescribeArgs(args)}";
                this.log.Warning("[BrioAssemblyBridgeService] {Failure}", failure);
                return this.Fail(failure, out reason);
            }

            var character = args[0]!;
            this.spawnedCharacters[npc.Id] = character;
            var nativeNameSet = false;
            var nativeName = this.nameService.TryReadNativeName(character);
            var objectIndex = ReadProperty(character, "ObjectIndex");
            var address = ReadProperty(character, "Address");
            var positionText = ReadPosition(character);
            this.LastSpawnedObjectIndex = objectIndex;
            this.LastSpawnedAddress = address;
            this.LastSpawnedPosition = positionText;

            var positionMessage = isSixParameter
                ? "位置已通过 6 参数 CreateCharacter 传入。"
                : "安全模式：Spawn 后未设置位置、名称或外观。";
            this.LastPositionError = positionMessage.Contains("失败原因", StringComparison.OrdinalIgnoreCase) ? positionMessage : string.Empty;
            this.LastSpawnedPosition = ReadPosition(character);
            var realNpc = this.CreateRealSpawnedNpc(npc, character, nativeNameSet, nativeName);
            this.spawnedNpcs[npc.Id] = realNpc;
            var nameMessage = nativeNameSet
                ? $"原生名称已尝试设置为 {npc.Name}，当前读取：{nativeName}。"
                : $"原生 NamePlate 名称未能设置，需后续研究 native name 字段。当前读取：{nativeName}。错误：{this.nameService.LastNameError}";
            reason = $"Brio assembly 反射生成成功：{npc.Name}，签名={this.LastCreateCharacterSignatureKind}，Result={this.LastCreateCharacterResult}，CharacterIsNull={this.LastCreateCharacterReturnedNull}，ObjectIndex={objectIndex}，Address={address}，Position={this.LastSpawnedPosition}。{positionMessage}{nameMessage}";
            this.LastMessage = reason;
            this.LastReflectionError = string.Empty;
            this.log.Information("[BrioAssemblyBridgeService] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio assembly 反射生成失败：{ex.Message}";
            this.LastReflectionError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to spawn NPC via Brio assembly reflection. NpcId={NpcId}", npc.Id);
            return false;
        }
    }

    public bool DespawnSelectedNpcReflection(string npcId, out string reason)
    {
        try
        {
            if (!this.spawnedCharacters.TryGetValue(npcId, out var character))
            {
                reason = $"没有记录到 NPC {npcId} 的 Brio assembly 生成对象。";
                this.LastMessage = reason;
                return true;
            }

            if (!this.TryGetActorSpawnService(out var actorSpawnService, out reason) || actorSpawnService == null)
                return this.Fail(reason);

            if (this.TryInvokeDestroyObject(actorSpawnService, character, out reason))
            {
                this.spawnedCharacters.Remove(npcId);
                this.spawnedNpcs.Remove(npcId);
                this.LastDespawnError = string.Empty;
                this.LastMessage = reason;
                this.LastReflectionError = string.Empty;
                this.log.Information("[BrioAssemblyBridgeService] {Reason}", reason);
                return true;
            }

            this.spawnedCharacters.Remove(npcId);
            this.spawnedNpcs.Remove(npcId);
            reason = $"未找到可用 DestroyObject 重载。安全模式下不会移动到地下；已移除 NPC {npcId} 的本地记录，可能需要重载 Brio/切图清理残留 actor。";
            this.LastDespawnError = reason;
            this.LastMessage = reason;
            this.log.Warning("[BrioAssemblyBridgeService] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio assembly 反射删除失败：{ex.Message}";
            this.LastReflectionError = reason;
            this.LastDespawnError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to despawn NPC via Brio assembly reflection. NpcId={NpcId}", npcId);
            return false;
        }
    }

    public bool DespawnAll(out string reason)
    {
        var failures = new List<string>();
        foreach (var npcId in this.spawnedCharacters.Keys.ToList())
        {
            if (!this.DespawnSelectedNpcReflection(npcId, out var singleReason))
                failures.Add(singleReason);
        }

        if (failures.Count == 0)
        {
            reason = "已清理全部 Brio assembly 实验 NPC。";
            this.LastMessage = reason;
            return true;
        }

        reason = string.Join("；", failures);
        this.LastReflectionError = reason;
        this.LastMessage = reason;
        return false;
    }

    public bool MoveNpcToPosition(string npcId, Vector3 position, out string reason)
    {
        try
        {
            if (!this.spawnedCharacters.TryGetValue(npcId, out var character))
                return this.Fail($"没有记录到 NPC {npcId} 的 Brio assembly 生成对象。", out reason);

            if (!this.TrySetActorPosition(character, position, out var error))
            {
                reason = $"位置设置失败：{error}";
                this.LastPositionError = reason;
                this.LastMessage = reason;
                this.log.Warning("[BrioAssemblyBridgeService] {Reason}", reason);
                return false;
            }

            this.RefreshRuntimeNpc(npcId, character);
            reason = $"已移动 NPC {npcId} 到 X {position.X:F2}, Y {position.Y:F2}, Z {position.Z:F2}";
            this.LastPositionError = string.Empty;
            this.LastMessage = reason;
            this.log.Information("[BrioAssemblyBridgeService] {Reason}", reason);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"移动 NPC 失败：{ex.Message}";
            this.LastPositionError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Failed to move Brio assembly NPC {NpcId}", npcId);
            return false;
        }
    }

    public bool HideNpcUnderground(string npcId, out string reason)
        => this.MoveNpcToPosition(npcId, new Vector3(0f, -5000f, 0f), out reason);

    public bool MoveNpcToLocalPlayerPosition(string npcId, out string reason)
    {
        var player = this.objectTable.LocalPlayer;
        return player == null
            ? this.Fail("当前 LocalPlayer 不可用。", out reason)
            : this.MoveNpcToPosition(npcId, player.Position, out reason);
    }

    public bool TryGetNpcPosition(string npcId, out Vector3 position, out string reason)
    {
        position = Vector3.Zero;
        if (!this.spawnedCharacters.TryGetValue(npcId, out var character))
        {
            reason = $"没有记录到 NPC {npcId} 的 Brio assembly 生成对象。";
            return false;
        }

        if (TryReadVector3Property(character, "Position", out position))
        {
            reason = string.Empty;
            return true;
        }

        reason = "无法读取 character.Position。";
        return false;
    }

    public bool TryGetSpawnedNpc(string npcId, out RealSpawnedNpc spawnedNpc)
    {
        if (this.spawnedNpcs.TryGetValue(npcId, out var existing))
        {
            if (this.spawnedCharacters.TryGetValue(npcId, out var character))
                this.RefreshRuntimeNpc(npcId, character);

            spawnedNpc = existing;
            return true;
        }

        spawnedNpc = new RealSpawnedNpc();
        return false;
    }

    public unsafe bool TryEnsureNativeActorDraw(RuntimeActorInstance instance, out string reason)
    {
        if (instance.CharacterObject == null || !TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = "native actor address unavailable while enabling draw.";
            return false;
        }

        var battleCharacter = (BattleChara*)address;
        battleCharacter->Alpha = 1f;
        if (!battleCharacter->IsReadyToDraw())
        {
            reason = "native actor is not ready to draw yet.";
            return false;
        }

        battleCharacter->EnableDraw();
        instance.HasBoundDrawObject = true;
        reason = "native actor draw enabled.";
        return true;
    }

    public void RefreshSpawnedNpcs()
    {
        foreach (var pair in this.spawnedCharacters.ToList())
            this.RefreshRuntimeNpc(pair.Key, pair.Value);
    }

    private unsafe bool TrySpawnNativeActor(RuntimeActorInstance instance, string displayName, Vector3 position, float yawRadians, ActorAppearanceData appearance, out string reason)
    {
        var objectManager = ClientObjectManager.Instance();
        if (objectManager == null)
        {
            reason = "ClientObjectManager.Instance() returned null.";
            return false;
        }

        var objectIndex = objectManager->CreateBattleCharacter();
        if (objectIndex == 0xffffffff)
        {
            reason = "ClientObjectManager.CreateBattleCharacter failed.";
            return false;
        }

        var gameObject = objectManager->GetObjectByIndex((ushort)objectIndex);
        if (gameObject == null)
        {
            reason = $"ClientObjectManager.GetObjectByIndex returned null for index={objectIndex}.";
            return false;
        }

        var battleCharacter = (BattleChara*)gameObject;
        var character = (Character*)battleCharacter;
        battleCharacter->CharacterSetup.SetupBNpc(0);
        var spawnKind = appearance.SpawnKind == ActorSpawnKind.Unknown
            ? appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman
            : appearance.SpawnKind;
        this.ConfigureNativeSpawnKind(battleCharacter, character, spawnKind, appearance);
        battleCharacter->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        character->GameObject.SetPosition(position.X, position.Y, position.Z);
        character->GameObject.SetRotation(yawRadians);
        character->GameObject.RotationModified();
        character->Alpha = 1f;
        character->GameObject.EnableDraw();
        WriteNativeName(character, displayName);

        var reference = this.objectTable.CreateObjectReference((nint)battleCharacter);
        if (reference == null)
        {
            objectManager->DeleteObjectByIndex((ushort)objectIndex, 0);
            reason = "Dalamud object table could not create an object reference for the native actor.";
            return false;
        }

        this.TryAddActorToBrioGpose(reference, out var gposeReason);

        this.FillInstanceFromCharacter(instance, reference);
        instance.CharacterObject = reference;
        instance.ObjectIndex = objectIndex.ToString();
        instance.Address = $"0x{(nint)battleCharacter:X}";
        instance.LastKnownObjectIndex = (int)objectIndex;
        instance.SpawnKind = spawnKind;
        instance.SourceActorKind = appearance.SourceActorKind;
        instance.SpawnKindStatus = $"native spawnKind={spawnKind}, objectKind={battleCharacter->ObjectKind}, modelChara={appearance.ModelCharaId}; {gposeReason}";
        instance.IsValid = true;
        instance.IsStale = false;
        instance.LastKnownPosition = position;
        instance.NativeNameSet = true;
        instance.CurrentNativeName = displayName;
        instance.NativeNameReadback = displayName;
        reason = $"Native actor spawned. runtime={instance.RuntimeId[..Math.Min(8, instance.RuntimeId.Length)]}, spawnKind={spawnKind}, objectKind={battleCharacter->ObjectKind}, objectIndex={objectIndex}, address={instance.Address}, name={displayName}. {gposeReason}";
        return true;
    }

    private unsafe void ConfigureNativeSpawnKind(BattleChara* battleCharacter, Character* character, ActorSpawnKind spawnKind, ActorAppearanceData appearance)
    {
        battleCharacter->ObjectKind = spawnKind switch
        {
            ActorSpawnKind.Mount => ObjectKind.Mount,
            ActorSpawnKind.Minion => ObjectKind.Companion,
            _ => ObjectKind.BattleNpc,
        };
        battleCharacter->BattleNpcSubKind = (BattleNpcSubKind)4;
        if (appearance.ModelCharaId != 0)
            character->ModelContainer.ModelCharaId = (int)appearance.ModelCharaId;
        if (appearance.ModelSkeletonId != 0)
            character->ModelContainer.ModelSkeletonId = (int)appearance.ModelSkeletonId;
    }

    private bool TryAddActorToBrioGpose(object characterReference, out string reason)
    {
        try
        {
            if (!this.TryGetActorSpawnService(out var actorSpawnService, out var serviceReason) || actorSpawnService == null)
            {
                reason = $"GPose bridge unavailable: {serviceReason}";
                return false;
            }

            var gposeService = actorSpawnService.GetType().GetField("_gPoseService", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(actorSpawnService);
            if (gposeService == null)
            {
                reason = "GPose bridge unavailable: Brio ActorSpawnService has no _gPoseService.";
                return false;
            }

            var method = gposeService.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(item =>
                {
                    if (!string.Equals(item.Name, "AddCharacterToGPose", StringComparison.Ordinal))
                        return false;
                    var parameters = item.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(characterReference);
                });
            if (method == null)
            {
                reason = "GPose bridge unavailable: AddCharacterToGPose(ICharacter) was not found.";
                return false;
            }

            method.Invoke(gposeService, [characterReference]);
            reason = "GPose bridge notified.";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"GPose bridge failed: {ex.Message}";
            this.log.Warning(ex, "Failed to add spawned actor to Brio GPose service.");
            return false;
        }
    }

    private unsafe bool TryDeleteNativeActor(RuntimeActorInstance instance, out string reason)
    {
        if (instance.CharacterObject == null || !TryReadAddress(instance.CharacterObject, out var address) || address == 0)
        {
            reason = "native actor address unavailable.";
            return false;
        }

        var objectManager = ClientObjectManager.Instance();
        if (objectManager == null)
        {
            reason = "ClientObjectManager.Instance() returned null.";
            return false;
        }

        var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        var objectIndex = objectManager->GetIndexByObject(gameObject);
        if (objectIndex < 0)
        {
            reason = $"ClientObjectManager could not resolve object index for {instance.Address}.";
            return false;
        }

        objectManager->DeleteObjectByIndex((ushort)objectIndex, 0);
        reason = $"Native actor deleted by ClientObjectManager at index={objectIndex}.";
        return true;
    }

    private unsafe static void WriteNativeName(Character* character, string name)
    {
        var bytes = Encoding.UTF8.GetBytes((name.Length > 20 ? name[..20] : name).Trim());
        var count = Math.Min(bytes.Length, 63);
        for (var index = 0; index < count; index++)
            character->GameObject.Name[index] = bytes[index];
        character->GameObject.Name[count] = 0;
    }

    private bool TryGetActorSpawnService(out object? actorSpawnService, out string reason)
    {
        actorSpawnService = null;
        reason = string.Empty;

        try
        {
            var brioAssembly = this.FindBrioAssembly();
            if (brioAssembly == null)
            {
                reason = "未发现已加载的 Brio assembly。";
                this.LastReflectionError = reason;
                return false;
            }

            var brioType = brioAssembly.GetType("Brio.Brio");
            var actorSpawnServiceType = brioAssembly.GetType("Brio.Game.Actor.ActorSpawnService");
            if (brioType == null || actorSpawnServiceType == null)
            {
                reason = $"无法找到必要类型。Brio.Brio={brioType != null}，ActorSpawnService={actorSpawnServiceType != null}";
                this.LastReflectionError = reason;
                return false;
            }

            var tryGetService = brioType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "TryGetService" && method.IsGenericMethodDefinition);
            if (tryGetService == null)
            {
                reason = "未找到 Brio.Brio.TryGetService<T>(out T)。";
                this.LastReflectionError = reason;
                return false;
            }

            var generic = tryGetService.MakeGenericMethod(actorSpawnServiceType);
            var args = new object?[] { null };
            var result = generic.Invoke(null, args);
            if (result is bool success && success && args[0] != null)
            {
                actorSpawnService = args[0];
                return true;
            }

            reason = "Brio.Brio.TryGetService<ActorSpawnService> 返回 false 或 null。";
            this.LastReflectionError = reason;
            return false;
        }
        catch (Exception ex)
        {
            reason = $"获取 ActorSpawnService 失败：{ex.Message}";
            this.LastReflectionError = reason;
            this.log.Error(ex, "Failed to get Brio ActorSpawnService via reflection");
            return false;
        }
    }

    private bool TryInvokeDestroyObject(object actorSpawnService, object character, out string reason)
    {
        foreach (var method in actorSpawnService.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(method => method.Name == "DestroyObject"))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                continue;

            try
            {
                object? argument = null;
                if (parameters[0].ParameterType.IsInstanceOfType(character))
                {
                    argument = character;
                }
                else if (parameters[0].ParameterType == typeof(int) && int.TryParse(ReadProperty(character, "ObjectIndex"), out var objectIndex))
                {
                    argument = objectIndex;
                }

                if (argument == null)
                    continue;

                var result = method.Invoke(actorSpawnService, [argument]);
                reason = $"已通过 ActorSpawnService.DestroyObject 删除实验 NPC。Result={result}";
                return true;
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "DestroyObject overload failed: {Method}", method.Name);
            }
        }

        reason = "未找到可用 ActorSpawnService.DestroyObject 重载。";
        return false;
    }

    private static bool IsSixParameterCreateCharacter(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 6 &&
            IsOutParameter(parameters[0]) &&
            IsSpawnFlagsParameter(parameters[1]) &&
            parameters[2].ParameterType == typeof(bool) &&
            parameters[3].ParameterType == typeof(Vector3) &&
            parameters[4].ParameterType == typeof(float) &&
            parameters[5].ParameterType == typeof(string);
    }

    private static bool IsThreeParameterCreateCharacter(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 3 &&
            IsOutParameter(parameters[0]) &&
            IsSpawnFlagsParameter(parameters[1]) &&
            parameters[2].ParameterType == typeof(bool);
    }

    private static bool IsOutParameter(ParameterInfo parameter)
        => parameter.IsOut || parameter.ParameterType.IsByRef;

    private static bool IsSpawnFlagsParameter(ParameterInfo parameter)
        => parameter.ParameterType.IsEnum &&
            parameter.ParameterType.Name.Contains("SpawnFlags", StringComparison.OrdinalIgnoreCase);

    private unsafe bool TrySetActorPositionFromAddress(object character, Vector3 position, out string error)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            error = "native 移动已禁用。";
            return false;
        }

        if (!TryReadAddress(character, out var address) || address == 0)
        {
            error = $"无法读取有效 Address。Address={ReadProperty(character, "Address")}";
            return false;
        }

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
            native->GameObject.SetPosition(position.X, position.Y, position.Z);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            this.log.Error(ex, "Native SetPosition failed. Address={Address}, Position={Position}", address, position);
            return false;
        }
    }

    private Vector3 NormalizeMoveTarget(Vector3 requested)
    {
        return requested;
    }

    private static bool IsReasonableMoveResult(Vector3 after, Vector3 target)
    {
        if (!IsFiniteVector(after))
            return false;

        if (after == Vector3.Zero)
            return false;

        if (MathF.Abs(after.X) > 100000f || MathF.Abs(after.Y) > 100000f || MathF.Abs(after.Z) > 100000f)
            return false;

        return Vector3.Distance(after, target) <= 10f;
    }

    private static bool IsFiniteVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string FormatPosition(Vector3 position)
        => $"X {position.X:F2}, Y {position.Y:F2}, Z {position.Z:F2}";

    private static Vector3 NormalizeActorScale(Vector3 scale)
    {
        if (!IsFiniteVector(scale) || scale == Vector3.Zero)
            return Vector3.One;

        var uniform = MathF.Max(0.01f, scale.Y);
        return new Vector3(uniform, uniform, uniform);
    }

    private static bool TryValidateSpawnedActorIndex(RuntimeActorInstance instance, out string reason)
    {
        var objectIndex = instance.LastKnownObjectIndex;
        if (int.TryParse(instance.ObjectIndex, out var parsedIndex))
            objectIndex = parsedIndex;

        if (objectIndex <= 0)
        {
            reason = $"invalid spawned Actor objectIndex={objectIndex}; objectIndex 0/local player is not a valid Actor target.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryReadAddress(object character, out nint address)
    {
        address = 0;
        var raw = ReadProperty(character, "Address");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexValue))
        {
            address = (nint)hexValue;
            return true;
        }

        if (ulong.TryParse(raw, out var value))
        {
            address = (nint)value;
            return true;
        }

        return false;
    }

    private static bool IsLocalPlayerCharacter(object character, object localPlayer, out string reason)
    {
        if (ReferenceEquals(character, localPlayer))
        {
            reason = "returned object reference equals LocalPlayer";
            return true;
        }

        var characterIndex = ReadProperty(character, "ObjectIndex");
        var localIndex = ReadProperty(localPlayer, "ObjectIndex");
        if (ObjectIndexMatches(characterIndex, localIndex))
        {
            reason = $"returned object index equals LocalPlayer index={localIndex}";
            return true;
        }

        var characterAddress = ReadProperty(character, "Address");
        var localAddress = ReadProperty(localPlayer, "Address");
        if (AddressMatches(characterAddress, localAddress))
        {
            reason = $"returned object address equals LocalPlayer address={localAddress}";
            return true;
        }

        reason = $"spawnedIndex={characterIndex}, localIndex={localIndex}, spawnedAddress={characterAddress}, localAddress={localAddress}";
        return false;
    }

    private static bool ObjectIndexMatches(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        return int.TryParse(left, out var leftIndex) &&
               int.TryParse(right, out var rightIndex) &&
               leftIndex == rightIndex;
    }

    private static bool AddressMatches(string left, string right)
    {
        return TryParseAddress(left, out var leftAddress) &&
               TryParseAddress(right, out var rightAddress) &&
               leftAddress != 0 &&
               leftAddress == rightAddress;
    }

    private static bool TryParseAddress(string rawAddress, out nint address)
    {
        address = 0;
        var raw = rawAddress.Trim();
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

    private bool TrySetActorPosition(object character, Vector3 position, out string error)
    {
        if (!this.EnableUnsafeNativeWrites)
        {
            error = "native 移动已禁用，避免 AccessViolation 崩溃。";
            return false;
        }

        var errors = new List<string>();
        try
        {
            var positionProperty = character.GetType().GetProperty("Position", BindingFlags.Instance | BindingFlags.Public);
            if (positionProperty is { CanWrite: true } && positionProperty.PropertyType == typeof(Vector3))
            {
                positionProperty.SetValue(character, position);
                error = string.Empty;
                return true;
            }
            errors.Add("character.Position 不可写或不存在。");

            errors.Add("Native()/Address/GameObject.SetPosition 写入已永久禁用。");
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            this.log.Warning(ex, "Failed to set Brio assembly actor position after 3-parameter CreateCharacter");
        }

        error = string.Join("；", errors);
        return false;
    }

    private static string ReadPosition(object character)
    {
        try
        {
            var position = character.GetType().GetProperty("Position", BindingFlags.Instance | BindingFlags.Public)?.GetValue(character);
            if (position != null)
                return position.ToString() ?? "不可用";
        }
        catch
        {
        }

        return "不可用";
    }

    private RealSpawnedNpc CreateRealSpawnedNpc(CustomNpc npc, object character, bool nativeNameSet, string nativeName)
    {
        var realNpc = new RealSpawnedNpc
        {
            NpcId = npc.Id,
            Name = npc.Name,
            Character = character,
            ObjectIndex = ReadProperty(character, "ObjectIndex"),
            Address = ReadProperty(character, "Address"),
            SpawnedAt = DateTime.Now,
            IsValid = IsCharacterValid(character),
            Source = "BrioAssembly",
            ExpectedName = npc.Name,
            CurrentNativeName = nativeName,
            NativeNameSet = nativeNameSet,
            LastNameError = nativeNameSet ? string.Empty : this.nameService.LastNameError,
        };

        if (TryReadVector3Property(character, "Position", out var position))
            realNpc.LastKnownPosition = position;

        return realNpc;
    }

    private void RefreshRuntimeNpc(string npcId, object character)
    {
        if (!this.spawnedNpcs.TryGetValue(npcId, out var realNpc))
            return;

        realNpc.ObjectIndex = ReadProperty(character, "ObjectIndex");
        realNpc.Address = ReadProperty(character, "Address");
        realNpc.IsValid = IsCharacterValid(character);
        realNpc.CurrentNativeName = this.nameService.TryReadNativeName(character);
        realNpc.LastNameError = realNpc.NativeNameSet ? string.Empty : this.nameService.LastNameError;
        if (TryReadVector3Property(character, "Position", out var position))
            realNpc.LastKnownPosition = position;
    }

    private void FillInstanceFromCharacter(RuntimeActorInstance instance, object character)
    {
        if (instance.IsStale)
        {
            instance.IsValid = false;
            return;
        }

        instance.CharacterObject = character;
        instance.ObjectIndex = ReadProperty(character, "ObjectIndex");
        instance.Address = ReadProperty(character, "Address");
        instance.LastKnownObjectIndex = int.TryParse(instance.ObjectIndex, out var objectIndex) ? objectIndex : -1;
        instance.IsValid = IsCharacterValid(character);
        instance.CurrentNativeName = this.nameService.TryReadNativeName(character);
        if (TryReadVector3Property(character, "Position", out var position))
            instance.LastKnownPosition = position;
    }

    private static bool IsCharacterValid(object character)
    {
        try
        {
            var isValidMethod = character.GetType().GetMethod("IsValid", BindingFlags.Instance | BindingFlags.Public);
            if (isValidMethod?.Invoke(character, null) is bool valid)
                return valid;
        }
        catch
        {
        }

        return true;
    }

    private static bool TryReadVector3Property(object source, string propertyName, out Vector3 position)
    {
        position = Vector3.Zero;
        try
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
            if (value is Vector3 vector)
            {
                position = vector;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private List<SpawnFlagInfo> ReadSpawnFlags(Type spawnFlagsType)
    {
        var flags = new List<SpawnFlagInfo>();
        try
        {
            var names = Enum.GetNames(spawnFlagsType);
            var values = Enum.GetValues(spawnFlagsType);
            for (var index = 0; index < names.Length; index++)
            {
                var rawValue = values.GetValue(index);
                var integerValue = rawValue == null ? 0L : Convert.ToInt64(rawValue);
                flags.Add(new SpawnFlagInfo(names[index], integerValue));
            }

            this.log.Information(
                "[BrioAssemblyBridgeService] SpawnFlags {SpawnFlagsType}: {Flags}",
                spawnFlagsType.FullName ?? spawnFlagsType.Name,
                string.Join(", ", flags.Select(flag => $"{flag.Name}={flag.Value}")));
        }
        catch (Exception ex)
        {
            this.LastReflectionError = $"读取 SpawnFlags 失败：{ex.Message}";
            this.log.Error(ex, "Failed to read Brio SpawnFlags enum");
        }

        return flags;
    }

    private object SelectSpawnFlag(Type spawnFlagsType, IReadOnlyList<SpawnFlagInfo> spawnFlags, out SpawnFlagInfo selectedFlagInfo)
    {
        var preferredNames = new[]
        {
            "DefinePosition",
            "CopyPosition",
            "Default",
        };

        foreach (var preferredName in preferredNames)
        {
            var match = spawnFlags.FirstOrDefault(flag => flag.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                this.log.Information("[BrioAssemblyBridgeService] Selected SpawnFlags.{Name}={Value}", match.Name, match.Value);
                selectedFlagInfo = match;
                return Enum.ToObject(spawnFlagsType, match.Value);
            }
        }

        selectedFlagInfo = new SpawnFlagInfo("整数 4", 4);
        this.log.Warning("[BrioAssemblyBridgeService] DefinePosition/CopyPosition/Default not found. Falling back to integer 4.");
        return Enum.ToObject(spawnFlagsType, 4);
    }

    private object SelectSpawnFlagByNames(Type spawnFlagsType, IReadOnlyList<SpawnFlagInfo> spawnFlags, out SpawnFlagInfo selectedFlagInfo, params string[] preferredNames)
    {
        foreach (var preferredName in preferredNames)
        {
            var match = spawnFlags.FirstOrDefault(flag => flag.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                this.log.Information("[BrioAssemblyBridgeService] Selected SpawnFlags.{Name}={Value}", match.Name, match.Value);
                selectedFlagInfo = match;
                return Enum.ToObject(spawnFlagsType, match.Value);
            }
        }

        selectedFlagInfo = new SpawnFlagInfo("整数 16", 16);
        this.log.Warning("[BrioAssemblyBridgeService] Prop/IsProp not found. Falling back to integer 16.");
        return Enum.ToObject(spawnFlagsType, 16);
    }

    private void FillPropInstanceFromCharacter(RuntimePropInstance instance, object character)
    {
        instance.CharacterObject = character;
        instance.ObjectIndex = ReadProperty(character, "ObjectIndex");
        instance.Address = ReadProperty(character, "Address");
        instance.IsValid = IsCharacterValid(character);
        instance.DrawObjectAddress = this.ReadDrawObjectAddress(character);
        if (TryReadVector3Property(character, "Position", out var position))
            instance.Position = position;
    }

    private unsafe string ReadDrawObjectAddress(object character)
    {
        if (!TryReadAddress(character, out var address) || address == 0)
            return "不可用";

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
            return native->GameObject.DrawObject == null
                ? "0x0"
                : $"0x{(nint)native->GameObject.DrawObject:X}";
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read DrawObject pointer for prop/actor.");
            return $"读取失败：{ex.Message}";
        }
    }

    private void LogCreateCharacterDiagnostics(MethodInfo createCharacter, Type spawnFlagsType)
    {
        var parameters = createCharacter.GetParameters();
        this.log.Information(
            "[BrioAssemblyBridgeService] CreateCharacter MethodInfo: {Method}",
            createCharacter.ToString() ?? createCharacter.Name);
        this.log.Information(
            "[BrioAssemblyBridgeService] CreateCharacter parameter types: {ParameterTypes}",
            string.Join(", ", parameters.Select(parameter => $"{parameter.Name}:{parameter.ParameterType.FullName ?? parameter.ParameterType.Name} IsOut={parameter.IsOut}")));
        this.log.Information(
            "[BrioAssemblyBridgeService] SpawnFlags actual type: {SpawnFlagsType}",
            spawnFlagsType.FullName ?? spawnFlagsType.Name);
    }

    private static string DescribeMethod(MethodInfo method)
        => $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.FullName ?? parameter.ParameterType.Name} {parameter.Name} IsOut={parameter.IsOut} IsByRef={parameter.ParameterType.IsByRef} Elem={parameter.ParameterType.GetElementType()?.FullName ?? "null"}"))})";

    private static string DescribeArgs(object?[] args)
        => string.Join(", ", args.Select((arg, index) => $"[{index}]={(arg == null ? "null" : $"{arg} ({arg.GetType().FullName})")}"));

    private void TryMoveUnderground(object character)
    {
        try
        {
            this.log.Warning("[BrioAssemblyBridgeService] Move underground disabled in safe mode.");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to move Brio assembly actor underground");
        }
    }

    private bool Fail(string reason)
    {
        this.LastReflectionError = reason;
        this.LastMessage = reason;
        this.log.Warning("[BrioAssemblyBridgeService] {Reason}", reason);
        return false;
    }

    private bool Fail(string reason, out string outReason)
    {
        outReason = reason;
        return this.Fail(reason);
    }

    private Assembly? FindBrioAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly =>
                assembly.GetName().Name?.Equals("Brio", StringComparison.OrdinalIgnoreCase) == true);

    private static string ReadProperty(object source, string propertyName)
    {
        try
        {
            return source.GetType().GetProperty(propertyName)?.GetValue(source)?.ToString() ?? "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    private static string FormatDisplayName(CustomNpc npc)
    {
        var template = string.IsNullOrWhiteSpace(npc.NameTemplate) ? "{name}" : npc.NameTemplate;
        var name = string.IsNullOrWhiteSpace(npc.Name) ? npc.Id : npc.Name;
        return template.Replace("{name}", name, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SpawnFlagInfo(string Name, long Value);
