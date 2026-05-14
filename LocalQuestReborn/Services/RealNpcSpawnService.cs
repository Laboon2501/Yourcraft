using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class RealNpcSpawnService
{
    private readonly IClientState clientState;
    private readonly ITargetManager targetManager;
    private readonly QuestDatabase database;
    private readonly RuntimeActorRegistry registry;
    private readonly SpawnIntentRegistry spawnIntentRegistry = new();
    private readonly BrioNpcBridgeService brioBridge;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly BrioCapabilityBridgeService brioCapabilityBridge;
    private readonly AppearanceApplyService appearanceApplyService;
    private readonly AppearanceApplyQueue appearanceApplyQueue;
    private readonly ActorAnimationService animationService;
    private readonly ActorAnimationRigService animationRigService;
    private readonly ActorActionSequenceService actionSequenceService;
    private readonly ActorLookAtService lookAtService;
    private readonly PlayerLookAtActorService playerLookAtActorService;
    private readonly ActorValidityMonitorService validityMonitorService;
    private readonly ActorNameplateService nameplateService;
    private readonly ActorTargetabilityService targetabilityService;
    private readonly TargetProbeService targetProbeService;
    private readonly NativeNpcProbeService nativeNpcProbeService;
    private readonly NativeGameObjectDumpService nativeGameObjectDumpService;
    private readonly ExperimentalEventNpcService experimentalEventNpcService;
    private readonly NativeTalkProbeService nativeTalkProbeService;
    private EventNpcHostService? eventNpcHostService;
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly IPluginLog log;
    private uint lastTerritoryType;
    private int gposeRebuildQueueCount;
    private DateTime lastSpawnIntentFallbackCheckAt = DateTime.MinValue;
    private long nextRuntimeActorSequence;
    private readonly Queue<PostSpawnApplyPlan> postSpawnApplyQueue = new();
    private PostSpawnApplyPlan? activePostSpawnApply;
    private readonly Queue<GposeActorRebuildSnapshot> pendingGposeRebuilds = new();
    private string activeGposeRebuildRuntimeId = string.Empty;
    private int gposeRebuildTotalCount;
    private int gposeRebuildSuccessCount;

    public RealNpcSpawnService(
        IClientState clientState,
        ITargetManager targetManager,
        QuestDatabase database,
        RuntimeActorRegistry registry,
        BrioNpcBridgeService brioBridge,
        BrioAssemblyBridgeService brioAssemblyBridge,
        BrioCapabilityBridgeService brioCapabilityBridge,
        AppearanceApplyService appearanceApplyService,
        AppearanceApplyQueue appearanceApplyQueue,
        ActorAnimationService animationService,
        ActorAnimationRigService animationRigService,
        ActorActionSequenceService actionSequenceService,
        ActorLookAtService lookAtService,
        PlayerLookAtActorService playerLookAtActorService,
        ActorValidityMonitorService validityMonitorService,
        ActorNameplateService nameplateService,
        ActorTargetabilityService targetabilityService,
        TargetProbeService targetProbeService,
        NativeNpcProbeService nativeNpcProbeService,
        NativeGameObjectDumpService nativeGameObjectDumpService,
        ExperimentalEventNpcService experimentalEventNpcService,
        NativeTalkProbeService nativeTalkProbeService,
        GlamourerIpcProbeService glamourerIpcProbe,
        GlamourerIpcBridgeService glamourerIpcBridge,
        PenumbraIpcService penumbraIpc,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.targetManager = targetManager;
        this.database = database;
        this.registry = registry;
        this.brioBridge = brioBridge;
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.brioCapabilityBridge = brioCapabilityBridge;
        this.appearanceApplyService = appearanceApplyService;
        this.appearanceApplyQueue = appearanceApplyQueue;
        this.animationService = animationService;
        this.animationRigService = animationRigService;
        this.actionSequenceService = actionSequenceService;
        this.lookAtService = lookAtService;
        this.playerLookAtActorService = playerLookAtActorService;
        this.validityMonitorService = validityMonitorService;
        this.nameplateService = nameplateService;
        this.targetabilityService = targetabilityService;
        this.targetProbeService = targetProbeService;
        this.nativeNpcProbeService = nativeNpcProbeService;
        this.nativeGameObjectDumpService = nativeGameObjectDumpService;
        this.experimentalEventNpcService = experimentalEventNpcService;
        this.nativeTalkProbeService = nativeTalkProbeService;
        this.glamourerIpcProbe = glamourerIpcProbe;
        this.glamourerIpcBridge = glamourerIpcBridge;
        this.penumbraIpc = penumbraIpc;
        this.log = log;
        this.lastTerritoryType = clientState.TerritoryType;
    }

    public string LastMessage { get; private set; } = "真实 Actor 管理器已就绪。";
    public string BridgeStatus => this.brioBridge.StatusText;
    public bool IsBrioIpcAvailable => this.brioBridge.IsIpcAvailable;
    public (int Major, int Minor)? BrioIpcVersion => this.brioBridge.CurrentApiVersion;
    public bool IsBrioIpcCompatible => this.brioBridge.IsCompatible;
    public string LastSpawnError => this.brioBridge.LastFailureReason;
    public string BrioAssemblyStatus => this.brioAssemblyBridge.StatusText;
    public bool IsBrioAssemblyLoaded => this.brioAssemblyBridge.IsBrioAssemblyLoaded;
    public bool HasBrioActorSpawnService => this.brioAssemblyBridge.HasActorSpawnService;
    public bool CanSpawnRealActor => this.IsBrioAssemblyLoaded && this.HasBrioActorSpawnService;
    public bool EnableUnsafeNativeWrites
    {
        get => this.brioAssemblyBridge.EnableUnsafeNativeWrites;
        set => this.brioAssemblyBridge.EnableUnsafeNativeWrites = value;
    }

    public bool IsSafeMode => !this.EnableUnsafeNativeWrites;
    public string CurrentSelectedActorRuntimeId { get; private set; } = string.Empty;
    public bool PlayerHeadLookAtSelectedActorEnabled
    {
        get => this.playerLookAtActorService.Enabled;
        set
        {
            this.playerLookAtActorService.Enabled = value;
            if (!value)
                this.playerLookAtActorService.Stop();
        }
    }

    public bool IsPlayerLookingAtSelectedActor => this.playerLookAtActorService.IsLookingAtSelectedActor;
    public string LastPlayerLookAtError => this.playerLookAtActorService.LastError;
    public string LastPlayerLookAtResult => this.playerLookAtActorService.LastResult;
    public string LastBrioAssemblyReflectionError => this.brioAssemblyBridge.LastReflectionError;
    public string LastBrioAssemblyMessage => this.brioAssemblyBridge.LastMessage;
    public IReadOnlyList<SpawnFlagInfo> BrioSpawnFlags => this.brioAssemblyBridge.SpawnFlags;
    public SpawnFlagInfo? SelectedBrioSpawnFlag => this.brioAssemblyBridge.SelectedSpawnFlag;
    public string LastBrioCreateCharacterSignatureKind => this.brioAssemblyBridge.LastCreateCharacterSignatureKind;
    public string LastBrioCreateCharacterResult => this.brioAssemblyBridge.LastCreateCharacterResult;
    public bool LastBrioCreateCharacterReturnedNull => this.brioAssemblyBridge.LastCreateCharacterReturnedNull;
    public string LastBrioSpawnedObjectIndex => this.brioAssemblyBridge.LastSpawnedObjectIndex;
    public string LastBrioSpawnedAddress => this.brioAssemblyBridge.LastSpawnedAddress;
    public string LastBrioSpawnedPosition => this.brioAssemblyBridge.LastSpawnedPosition;
    public IReadOnlyList<RuntimeActorInstance> Actors => this.registry.GetAll();
    public string CurrentTargetObjectIndex => ReadTargetObjectIndex(this.targetManager.Target);
    public string MatchedTargetNpcId { get; private set; } = string.Empty;
    public string MatchedTargetRuntimeId { get; private set; } = string.Empty;
    public string LastPositionError => this.brioAssemblyBridge.LastPositionError;
    public string LastDespawnError => this.brioAssemblyBridge.LastDespawnError;
    public string LastBrioCapabilityMoveError => this.brioCapabilityBridge.LastMoveError;
    public IReadOnlyList<string> BrioCapabilityDebugTypes => this.brioCapabilityBridge.DebugTypeNames;
    public IReadOnlyList<BrioIpcProbeResult> BrioIpcProbeResults => this.brioBridge.IpcProbe.Results;
    public string BrioIpcProbeMessage => this.brioBridge.IpcProbe.LastProbeMessage;
    public IReadOnlyList<GlamourerIpcProbeResult> GlamourerIpcProbeResults => this.glamourerIpcProbe.Results;
    public string GlamourerIpcProbeMessage => this.glamourerIpcProbe.LastProbeMessage;
    public string GlamourerVersion => this.glamourerIpcProbe.GlamourerVersion;
    public string GlamourerVersionError => this.glamourerIpcProbe.GlamourerVersionError;
    public IReadOnlyList<GlamourerIpcBindingDetail> GlamourerIpcBindingDetails => this.glamourerIpcBridge.Details;
    public string GlamourerIpcBridgeMessage => this.glamourerIpcBridge.LastMessage;
    public string GlamourerIpcBridgeLastError => this.glamourerIpcBridge.LastError;
    public string GlamourerIpcBridgeLastInvocationParameters => this.glamourerIpcBridge.LastInvocationParameters;
    public string GlamourerIpcBridgeLastReturnCode => this.glamourerIpcBridge.LastReturnCode;
    public GlamourerIpcBindingDetail? SelectedGlamourerApplyDesign => this.glamourerIpcBridge.SelectedApplyDesign;
    public bool IsGlamourerApplyDesignRegistered => this.glamourerIpcBridge.IsApplyDesignRegistered;
    public bool IsGlamourerApplyDesignTwoParameterBindable => this.glamourerIpcBridge.IsTwoParameterApplyDesignBindable;
    public bool IsGlamourerApplyDesignFourParameterBindable => this.glamourerIpcBridge.IsFourParameterApplyDesignBindable;
    public bool IsPenumbraIpcAvailable => this.penumbraIpc.IsAvailable;
    public bool IsPenumbraEnabled => this.penumbraIpc.IsEnabled;
    public string PenumbraIpcStatus => this.penumbraIpc.LastStatus;
    public string PenumbraIpcLastError => this.penumbraIpc.LastError;
    public string PenumbraIpcApiVersion => this.penumbraIpc.ApiVersionText;
    public IReadOnlyList<PenumbraCollectionInfo> PenumbraCollections => this.penumbraIpc.Collections;
    public int AppearanceQueueLength => this.appearanceApplyQueue.Count;
    public string AppearanceQueueCurrentActor => this.appearanceApplyQueue.CurrentActorRuntimeId;
    public long AppearanceQueueLastElapsedMilliseconds => this.appearanceApplyQueue.LastElapsedMilliseconds;
    public string AppearanceQueueLastError => this.appearanceApplyQueue.LastError;
    public string AppearanceQueueStatus => this.appearanceApplyQueue.LastStatus;
    public string ActorValidityMonitorStatus => this.validityMonitorService.LastStatus;
    public bool CurrentIsGposing => this.validityMonitorService.CurrentIsGposing;
    public bool PreviousFrameIsGposing => this.validityMonitorService.PreviousFrameIsGposing;
    public DateTime? LastGposeExitedAt => this.validityMonitorService.LastGposeExitedAt;
    public bool IsGposeRebuildScheduled => this.validityMonitorService.IsRebuildScheduled;
    public TimeSpan GposeRebuildWaitRemaining => this.validityMonitorService.GposeExitWaitRemaining;
    public int GposeRebuildQueueCount => this.gposeRebuildQueueCount;
    public string LastGposeRebuildResult { get; private set; } = "尚未重建。";
    public int SpawnIntentCount => this.spawnIntentRegistry.Count;
    public IReadOnlyList<SpawnIntent> SpawnIntents => this.spawnIntentRegistry.GetAll();
    public bool IsHumanoidGlamourerApplyStateAvailable => this.appearanceApplyService.IsHumanoidGlamourerApplyStateAvailable;
    public string HumanoidGlamourerApplyStateSignature => this.appearanceApplyService.HumanoidGlamourerApplyStateSignature;
    public bool IsHumanoidBrioActorAppearanceAvailable => this.appearanceApplyService.IsHumanoidBrioActorAppearanceAvailable;
    public string HumanoidBrioActorAppearanceSignature => this.appearanceApplyService.HumanoidBrioActorAppearanceSignature;
    public bool IsHumanoidAppearanceManagerAvailable => false;
    public string HumanoidAppearanceCurrentPath => this.appearanceApplyService.HumanoidAppearanceCurrentPath;
    public string HumanoidAppearanceLastResult => this.appearanceApplyService.HumanoidAppearanceLastResult;
    public string HumanoidAppearanceLastException => this.appearanceApplyService.HumanoidAppearanceLastException;
    public string HumanoidGlamourerApplyStateLastResult => this.appearanceApplyService.HumanoidGlamourerApplyStateLastResult;
    public string HumanoidGlamourerApplyStateLastException => this.appearanceApplyService.HumanoidGlamourerApplyStateLastException;
    public string HumanoidBrioActorAppearanceLastResult => this.appearanceApplyService.HumanoidBrioActorAppearanceLastResult;
    public string HumanoidBrioActorAppearanceLastException => this.appearanceApplyService.HumanoidBrioActorAppearanceLastException;
    public TargetProbeSnapshot? ReferenceNpcSnapshot => this.targetProbeService.ReferenceNpcSnapshot;
    public TargetProbeSnapshot? BrioActorSnapshot => this.targetProbeService.BrioActorSnapshot;
    public string TargetProbeComparison => this.targetProbeService.LastComparison;
    public NativeNpcProbeSnapshot? NativeReferenceEventNpcSnapshot => this.nativeNpcProbeService.ReferenceEventNpcSnapshot;
    public NativeNpcProbeSnapshot? NativeBrioActorSnapshot => this.nativeNpcProbeService.BrioActorSnapshot;
    public string NativeNpcProbeComparison => this.nativeNpcProbeService.LastComparison;
    public NativeNpcProbeSnapshot? NativeGameObjectReferenceSnapshot => this.nativeGameObjectDumpService.ReferenceSnapshot;
    public NativeNpcProbeSnapshot? NativeGameObjectActorSnapshot => this.nativeGameObjectDumpService.ActorSnapshot;
    public string NativeGameObjectDumpComparison => this.nativeGameObjectDumpService.LastComparison;
    public string NativeManualTargetTestResult => this.nativeGameObjectDumpService.LastManualTestResult;
    public string ExperimentalEventNpcLastResult => this.experimentalEventNpcService.LastResult;
    public IReadOnlyList<string> NativeTalkProbeEvents => this.nativeTalkProbeService.Events;
    public string NativeTalkProbeLastEvent => this.nativeTalkProbeService.LastEvent;
    public string HostMatchedCustomNpcId => this.eventNpcHostService?.MatchedHostNpcId ?? string.Empty;
    public string HostMatchDebug => this.eventNpcHostService?.LastHostMatchDebug ?? "EventNpcHostService 未初始化。";
    public DateTime? LastHostInteractionAt => this.eventNpcHostService?.LastHostInteractionAt;
    public string LastNativeAddonName => this.eventNpcHostService?.LastNativeAddonName ?? "无";
    public bool LastHostIntercepted => this.eventNpcHostService?.LastHostIntercepted ?? false;
    public string LastHostInterceptSource => this.eventNpcHostService?.LastHostInterceptSource ?? "无";
    public string LastHostInterceptResult => this.eventNpcHostService?.LastHostInterceptResult ?? "EventNpcHostService 未初始化。";
    public string LastNativeAddonCloseResult => this.eventNpcHostService?.LastNativeAddonCloseResult ?? "EventNpcHostService 未初始化。";

    public void SetEventNpcHostService(EventNpcHostService service)
        => this.eventNpcHostService = service;

    private Vector3 GetTemplateSpawnPosition(CustomNpc npc)
    {
        var basePosition = this.TryReadLocalPlayerPosition(out var playerPosition) ? playerPosition : Vector3.Zero;
        var offset = ToVector3(npc.DefaultSpawnOffset);
        return basePosition + offset;
    }

    private bool TryReadLocalPlayerPosition(out Vector3 position)
    {
        position = Vector3.Zero;
        try
        {
            var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
            var raw = localPlayer?.GetType().GetProperty("Position")?.GetValue(localPlayer);
            if (raw is Vector3 vector)
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

    private static Vector3 ToVector3(Vector3Data data)
        => new(data.X, data.Y, data.Z);

    private static Vector3 NormalizeScale(Vector3 scale)
    {
        return new Vector3(
            MathF.Max(0.01f, float.IsFinite(scale.X) && scale.X != 0f ? scale.X : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Y) && scale.Y != 0f ? scale.Y : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Z) && scale.Z != 0f ? scale.Z : 1f));
    }

    public void ProbeBrioIpc()
        => this.RunSafely("探测 Brio IPC", () =>
        {
            this.brioBridge.IpcProbe.Probe();
            this.LastMessage = this.brioBridge.IpcProbe.LastProbeMessage;
        });

    public void ProbeGlamourerIpc()
        => this.RunSafely("探测 Glamourer/Penumbra IPC", () =>
        {
            this.glamourerIpcProbe.Probe();
            this.glamourerIpcBridge.Probe();
            this.penumbraIpc.TryConnectOrRefresh("manual probe");
            this.appearanceApplyService.RefreshHumanoidAppearancePaths();
            this.LastMessage = $"{this.glamourerIpcProbe.LastProbeMessage} | {this.penumbraIpc.LastStatus}";
        });

    public RuntimeActorInstance? SpawnNew(CustomNpc npc)
    {
        try
        {
            var runtimeId = Guid.NewGuid().ToString("N");
            var spawnPosition = this.GetTemplateSpawnPosition(npc);
            var spawnRotation = ToVector3(npc.DefaultRotationEuler);
            var spawnScale = NormalizeScale(ToVector3(npc.DefaultScale));
            if (!this.CanSpawnRealActor)
            {
                this.LastMessage = "Brio Assembly 不可用，未生成真实 Actor。";
                return null;
            }

            if (!this.brioAssemblyBridge.TrySpawnActor(npc, runtimeId, out var instance, out var reason))
            {
                this.LastMessage = $"生成真实 Actor 失败：{reason}";
                return null;
            }

            this.registry.Add(instance);
            instance.TemplateNpcId = npc.Id;
            instance.NpcId = npc.Id;
            instance.NpcName = npc.Name;
            instance.DisplayName = npc.Name;
            instance.SortOrder = this.GetNpcSortOrder(npc.Id);
            instance.SpawnSequence = ++this.nextRuntimeActorSequence;
            instance.SpawnedTerritoryType = (ushort)Math.Clamp((int)this.clientState.TerritoryType, 0, ushort.MaxValue);
            instance.SpawnedTerritoryName = this.clientState.TerritoryType.ToString();
            instance.SpawnPosition = spawnPosition;
            instance.SpawnRotationEuler = spawnRotation;
            instance.SpawnScale = spawnScale;
            instance.TransformEditPosition = spawnPosition;
            instance.TransformEditRotationEuler = spawnRotation;
            instance.TransformEditScale = spawnScale;
            instance.DefaultAnimationId = npc.DefaultAnimationId;
            instance.CurrentAnimationId = npc.DefaultAnimationId;
            instance.LookAtPlayerEnabled = npc.LookAtPlayerEnabled;
            instance.LookAtRadius = Math.Max(0.1f, npc.LookAtRadius);
            instance.LookAtMode = NpcLookAtMode.NativeLookAt;
            instance.PenumbraMode = npc.PenumbraMode;
            instance.PenumbraCollectionId = npc.PenumbraCollectionId;
            instance.PenumbraCollectionNameCache = npc.PenumbraCollectionNameCache;
            instance.PostSpawnBehaviorReady = false;
            instance.PostSpawnPipelineState = "SpawnedPointerAcquired";
            instance.PostSpawnPipelineStatus = "Spawn returned; waiting for DrawObject before applying appearance.";
            this.actionSequenceService.Reset(instance);
            this.nameplateService.TryReadActorName(instance);
            this.targetabilityService.TryReadTargetability(instance);
            this.ApplyActorTransform(instance.RuntimeId, spawnPosition, spawnRotation, spawnScale);
            this.QueuePostSpawnApply(instance.RuntimeId, "spawn");

            this.LastMessage = $"{reason} 外观应用已加入队列。";
            return instance;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RealNpcSpawnService] SpawnNew failed for {NpcId}", npc.Id);
            this.LastMessage = $"生成真实 Actor 失败：{ex.Message}";
            return null;
        }
    }

    public RuntimeActorInstance? SpawnUnique(CustomNpc npc)
    {
        this.spawnIntentRegistry.MarkShouldSpawn(npc);
        this.DespawnAllForNpc(npc.Id, DespawnReason.InvalidActorCleanup, updateIntent: false);
        var instance = this.SpawnNew(npc);
        if (instance != null)
            this.spawnIntentRegistry.UpdateLastRuntime(npc, instance);
        return instance;
    }

    public bool Despawn(string runtimeId, DespawnReason reason)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return true;

        this.PrepareActorForDespawn(instance);
        var success = this.brioAssemblyBridge.TryDespawnActor(instance, out var despawnMessage);
        this.spawnIntentRegistry.MarkDespawned(instance.NpcId, reason);
        this.registry.Remove(runtimeId);
        if (string.Equals(this.CurrentSelectedActorRuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
            this.ClearSelectedActorForPlayerLookAt();
        this.LastMessage = success ? despawnMessage : $"删除 Actor 失败：{despawnMessage}";
        return success;
    }

    public void DespawnAllForNpc(string npcId)
        => this.DespawnAllForNpc(npcId, DespawnReason.UserRequested, updateIntent: true);

    private void PrepareActorForDespawn(RuntimeActorInstance instance)
    {
        this.lookAtService.Stop(instance, out _);
        this.actionSequenceService.Stop(instance);
        if (instance.HasAnimationRigNativeOverride)
            this.animationRigService.RestoreAnimationRig(instance, out _);
        this.appearanceApplyQueue.RemoveJobsForActor(instance.RuntimeId);
        this.penumbraIpc.CleanupActorAssignment(instance);

        if (instance.AnimationEnabled && instance.IsValid && instance.CharacterObject != null)
        {
            try
            {
                this.animationService.Stop(instance, out _);
            }
            catch (Exception ex)
            {
                instance.LastAnimationError = $"删除前停止动画失败：{ex.Message}";
                this.log.Warning(ex, "Failed to stop animation before despawn. RuntimeId={RuntimeId}", instance.RuntimeId);
            }
        }
    }

    private void DespawnAllForNpc(string npcId, DespawnReason reason, bool updateIntent)
    {
        if (updateIntent)
            this.spawnIntentRegistry.MarkDespawned(npcId, reason);

        foreach (var instance in this.registry.GetByNpcId(npcId).ToList())
            this.Despawn(instance.RuntimeId, reason);
    }

    public void DespawnAll()
        => this.DespawnAll(DespawnReason.UserRequested);

    private void DespawnAll(DespawnReason reason)
        => this.RunSafely("删除全部 Actor", () =>
        {
            foreach (var instance in this.registry.GetAll().ToList())
                this.Despawn(instance.RuntimeId, reason);
            if (reason == DespawnReason.UserRequested)
            {
                foreach (var intent in this.spawnIntentRegistry.GetAll())
                    this.spawnIntentRegistry.MarkDespawned(intent.NpcId, reason);
            }

            this.LastMessage = "已删除全部 Runtime Actor。";
        });

    public bool MoveActor(string runtimeId, Vector3 position)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        var success = this.brioAssemblyBridge.TryMoveActorWithNativeSetPosition(instance, position, out var reason);
        if (success)
        {
            instance.SpawnPosition = instance.LastKnownPosition;
            instance.TransformEditPosition = instance.LastKnownPosition;
            this.brioCapabilityBridge.TrySyncTransformAfterNativeMove(instance, instance.LastKnownPosition, out _);
        }

        this.LastMessage = success ? reason : $"移动 Actor 失败：{reason}";
        return success;
    }

    public bool RefreshActorTransform(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        if (!instance.IsValid || instance.CharacterObject == null)
        {
            instance.LastTransformError = "当前 Actor 无效或已删除。";
            this.LastMessage = instance.LastTransformError;
            return false;
        }

        this.brioAssemblyBridge.RefreshActor(instance);
        if (this.brioCapabilityBridge.TryReadModelTransform(instance, out var reason))
        {
            this.LastMessage = reason;
            return true;
        }

        instance.TransformEditPosition = instance.LastKnownPosition;
        instance.TransformEditRotationEuler = instance.LastKnownRotationEuler;
        instance.TransformEditScale = instance.LastKnownScale == Vector3.Zero ? Vector3.One : instance.LastKnownScale;
        instance.LastTransformError = reason;
        this.LastMessage = reason;
        return false;
    }

    public bool ApplyActorTransform(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        if (!instance.IsValid || instance.CharacterObject == null)
        {
            instance.LastTransformError = "当前 Actor 无效或已删除。";
            this.LastMessage = instance.LastTransformError;
            return false;
        }

        var safeScale = new Vector3(
            MathF.Max(0.01f, float.IsFinite(scale.X) ? scale.X : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Y) ? scale.Y : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Z) ? scale.Z : 1f));
        if (!this.MoveActor(runtimeId, position))
        {
            instance.LastTransformError = this.LastMessage;
            return false;
        }

        var readPosition = instance.LastKnownPosition;
        if (!this.brioCapabilityBridge.TryApplyModelTransform(instance, readPosition, rotationEuler, safeScale, out var transformReason))
        {
            instance.LastTransformError = transformReason;
            this.LastMessage = transformReason;
            return false;
        }

        this.brioAssemblyBridge.RefreshActor(instance);
        this.brioCapabilityBridge.TryReadModelTransform(instance, out var readReason);
        instance.SpawnPosition = instance.LastKnownPosition;
        instance.SpawnRotationEuler = instance.LastKnownRotationEuler;
        instance.SpawnScale = instance.LastKnownScale == Vector3.Zero ? safeScale : instance.LastKnownScale;
        this.LastMessage = string.IsNullOrWhiteSpace(readReason) ? transformReason : readReason;
        return true;
    }

    public void SaveActorTransformSnapshot(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return;
        }

        instance.TransformEditPosition = position;
        instance.TransformEditRotationEuler = rotationEuler;
        instance.TransformEditScale = scale;
        instance.SavedTransformSnapshot = $"position={position}; rotationEuler={rotationEuler}; scale={scale}";
        this.LastMessage = $"已保存当前 Transform 到运行态缓存：{instance.SavedTransformSnapshot}";
    }

    public void MoveAllForNpc(string npcId, Vector3 position)
    {
        foreach (var instance in this.registry.GetByNpcId(npcId))
            this.MoveActor(instance.RuntimeId, position);
    }

    public bool SaveActorPositionToNpc(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        this.brioAssemblyBridge.RefreshActor(instance);
        var npc = this.database.GetNpcById(instance.NpcId);
        if (npc == null)
            return false;

        npc.Position = new Vector3Data { X = instance.LastKnownPosition.X, Y = instance.LastKnownPosition.Y, Z = instance.LastKnownPosition.Z };
        npc.TerritoryType = (ushort)Math.Clamp((int)this.clientState.TerritoryType, 0, ushort.MaxValue);
        this.database.Save();
        this.LastMessage = $"已保存 Actor 当前位置到 NPC 配置：{npc.Name}";
        return true;
    }

    public bool ApplyAppearance(string runtimeId)
        => this.ApplyNpcAppearance(runtimeId);

    public bool ApplyNpcAppearance(string runtimeId)
        => this.EnqueueNpcAppearance(runtimeId);

    public bool EnqueueNpcAppearance(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        this.appearanceApplyQueue.Enqueue(runtimeId, "单个 Actor 应用 NPC 外观");
        this.LastMessage = $"已加入外观应用队列：{runtimeId}";
        return true;
    }

    public void ApplyNpcAppearanceForNpc(string npcId)
        => this.RunSafely("对 NPC 的全部 Actor 应用外观", () =>
        {
            this.appearanceApplyQueue.EnqueueForNpc(npcId);
            this.LastMessage = this.appearanceApplyQueue.LastStatus;
        });

    public void ApplyAllNpcAppearances()
        => this.RunSafely("对全部 Actor 应用外观", () =>
        {
            this.appearanceApplyQueue.EnqueueAll();
            this.LastMessage = this.appearanceApplyQueue.LastStatus;
        });

    public RuntimeActorInstance? RegenerateAndApplyAppearance(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return null;

        var npc = this.database.GetNpcById(instance.NpcId);
        if (npc == null)
            return null;

        this.Despawn(runtimeId, DespawnReason.InvalidActorCleanup);
        return this.SpawnUnique(npc);
    }

    public bool PlayAnimation(string runtimeId, uint animationId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        var success = this.animationService.Play(instance, animationId, out var reason);
        this.LastMessage = success ? $"已播放动画：{animationId}" : $"播放动画失败：{reason}";
        return success;
    }

    public bool StopAnimation(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        var success = this.animationService.Stop(instance, out var reason);
        this.LastMessage = success ? "已停止动画并尝试恢复 idle。" : $"停止动画失败：{reason}";
        return success;
    }

    public bool ApplyActorAnimationRig(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return false;
        }

        var success = this.animationRigService.ApplyAnimationRigOverride(instance, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool RestoreActorAnimationRig(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return false;
        }

        var success = this.animationRigService.RestoreAnimationRig(instance, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool ReapplyActorCurrentAnimation(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return false;
        }

        var success = this.animationRigService.ReapplyCurrentAnimationWithRig(instance, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public void ResetActionSequence(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return;
        }

        this.actionSequenceService.Reset(instance);
        this.LastMessage = $"Reset Actor action sequence: {ShortRuntimeId(runtimeId)}";
    }

    public bool TestActionSequenceStep(string runtimeId, Guid stepId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return false;
        }

        var step = instance.ActionSequence.FirstOrDefault(item => item.Id == stepId);
        if (step == null)
        {
            this.LastMessage = "Action sequence step not found.";
            return false;
        }

        var success = this.actionSequenceService.TestStep(instance, step, out var reason);
        this.LastMessage = success ? $"Tested action sequence step: {step.Name}" : $"Action sequence step test failed: {reason}";
        return success;
    }

    public void UpdateActorLookAtSettings(string runtimeId, bool enabled, float radius)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return;
        }

        instance.LookAtPlayerEnabled = enabled;
        instance.LookAtRadius = Math.Max(0.1f, radius);
        instance.LookAtMode = NpcLookAtMode.NativeLookAt;
        instance.LastLookAtUpdateAt = DateTime.MinValue;
        if (!enabled)
        {
            this.lookAtService.Stop(instance, out var stopReason);
            this.LastMessage = $"已关闭 Actor 看向玩家：{ShortRuntimeId(runtimeId)}";
            if (!string.IsNullOrWhiteSpace(stopReason))
                instance.LookAtTargetDebug = "none";
            return;
        }

        this.lookAtService.Update([instance], this.database);
        this.LastMessage = $"已更新 Actor NativeLookAt：{ShortRuntimeId(runtimeId)}，半径={instance.LookAtRadius:F1}";
    }

    public bool SetActorName(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        var npc = this.database.GetNpcById(instance.NpcId);
        var displayName = npc == null ? instance.NpcName : FormatDisplayName(npc);
        var success = this.nameplateService.TrySetActorName(instance, displayName);
        this.LastMessage = instance.NameSetResult;
        return success;
    }

    public bool ReadActorName(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        this.nameplateService.TryReadActorName(instance);
        this.LastMessage = $"读取原生名称：{instance.NativeNameReadback}";
        return true;
    }

    public bool RefreshActorNameplate(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        var success = this.nameplateService.TryRefreshNameplate(instance);
        this.LastMessage = instance.NameSetResult;
        return success;
    }

    public bool MakeActorTargetable(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        var success = this.targetabilityService.TryMakeTargetable(instance);
        this.LastMessage = instance.HoverOrTargetDebugInfo;
        return success;
    }

    public bool ReadActorTargetability(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        this.targetabilityService.TryReadTargetability(instance);
        this.targetabilityService.TryMatchCurrentTarget(instance);
        this.LastMessage = "已读取 Targetable 状态。";
        return true;
    }

    public bool SetActorAsCurrentTarget(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        var success = this.targetabilityService.TrySetCurrentTarget(instance);
        this.LastMessage = instance.HoverOrTargetDebugInfo;
        return success;
    }

    public bool RefreshTargetMatch(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
            return false;

        var success = this.targetabilityService.TryMatchCurrentTarget(instance);
        this.LastMessage = instance.HoverOrTargetDebugInfo;
        return success;
    }

    public void SaveCurrentTargetAsReferenceSnapshot()
        => this.RunSafely("保存当前 Target 为参考 NPC 快照", () =>
        {
            this.targetProbeService.SaveReferenceFromCurrentTarget();
            this.LastMessage = "已保存当前 Target 为参考 NPC 快照。";
        });

    public bool SaveActorProbeSnapshot(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        var success = this.targetProbeService.SaveActorSnapshot(instance);
        this.LastMessage = success ? "已保存生成 Actor 快照。" : "保存生成 Actor 快照失败。";
        return success;
    }

    public string CompareTargetProbeSnapshots()
    {
        var result = this.targetProbeService.CompareSnapshots();
        this.LastMessage = "已对比 Target Probe 快照。";
        return result;
    }

    public void SaveCurrentTargetAsNativeEventNpcSnapshot()
        => this.RunSafely("保存真实 EventNpc 快照", () =>
        {
            this.nativeNpcProbeService.SaveCurrentTargetAsReference();
            this.LastMessage = "已保存当前 Target 为真实 EventNpc 快照。";
        });

    public bool SaveActorNativeNpcSnapshot(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        this.nativeNpcProbeService.SaveActorSnapshot(instance);
        this.LastMessage = "已保存 Brio actor 原生字段快照。";
        return true;
    }

    public string CompareNativeNpcSnapshots()
    {
        var result = this.nativeNpcProbeService.Compare();
        this.LastMessage = "已对比 EventNpc / Brio actor 原生字段。";
        return result;
    }

    public void DumpCurrentTargetNativeGameObject()
        => this.RunSafely("读取当前 Target native GameObject", () =>
        {
            this.nativeGameObjectDumpService.DumpCurrentTarget();
            this.LastMessage = "已读取当前 Target 的 FFXIVClientStructs GameObject 字段。";
        });

    public bool DumpActorNativeGameObject(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        this.nativeGameObjectDumpService.DumpActor(instance);
        this.LastMessage = "已读取选中 Brio Actor 的 FFXIVClientStructs GameObject 字段。";
        return true;
    }

    public string CompareNativeGameObjectDumps()
    {
        var result = this.nativeGameObjectDumpService.Compare();
        this.LastMessage = "已对比 native GameObject dump。";
        return result;
    }

    public bool TryWriteEventNpcObjectKind(string runtimeId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteObjectKind(actor));

    public bool TryWriteEventNpcSubKind(string runtimeId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteSubKind(actor));

    public bool TryWriteEventNpcSubKind(string runtimeId, byte subKind) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteSubKind(actor, subKind));

    public bool TryWriteEventNpcTargetable(string runtimeId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteIsTargetable(actor));

    public bool TryWriteEventNpcTargetableStatus(string runtimeId, byte targetableStatus) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteTargetableStatus(actor, targetableStatus));

    public bool TryWriteEventNpcDataId(string runtimeId, uint dataId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteDataId(actor, dataId));

    public bool TryWriteEventNpcGameObjectId(string runtimeId, uint objectId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteGameObjectId(actor, objectId));

    public bool TryWriteEventNpcEntityId(string runtimeId, uint entityId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteEntityId(actor, entityId));

    public bool TryWriteEventNpcBaseId(string runtimeId, uint baseId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteDataId(actor, baseId));

    public bool TryWriteEventNpcHitbox(string runtimeId, float radius) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteHitbox(actor, radius));

    public bool TryWriteEventNpcRenderFlags(string runtimeId, ulong flags) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteRenderFlags(actor, flags));

    public bool TryWriteEventNpcNamePlateIconId(string runtimeId, uint iconId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteNamePlateIconId(actor, iconId));

    public bool TryWriteEventNpcNamePlateColorType(string runtimeId, uint colorType) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryWriteNamePlateColorType(actor, colorType));

    public bool TryCopyEventNpcEventHandler(string runtimeId, nint eventHandler) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.TryCopyEventHandler(actor, eventHandler));

    public void RecordManualTargetTest(string runtimeId, string result)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        this.nativeGameObjectDumpService.RecordManualTargetTest(actor, result);
        this.LastMessage = this.nativeGameObjectDumpService.LastManualTestResult;
    }

    public bool TryRefreshEventNpcNameplate(string runtimeId) => this.RunEventNpcExperiment(runtimeId, actor => this.experimentalEventNpcService.RefreshNameplate(actor));

    public void RecordNativeTalkConfirmProbe()
        => this.RunSafely("记录 Confirm/Interact Probe", () =>
        {
            this.nativeTalkProbeService.RecordConfirmProbe();
            this.LastMessage = this.nativeTalkProbeService.LastEvent;
        });

    public void SetNpcHostFromCurrentTarget(CustomNpc npc)
        => this.RunSafely("从当前选中 NPC 设置为 Host", () =>
        {
            if (this.eventNpcHostService == null)
            {
                this.LastMessage = "EventNpcHostService 未初始化。";
                return;
            }

            this.eventNpcHostService.SetCurrentTargetAsHost(npc, out var message);
            this.LastMessage = message;
        });

    public void ClearNpcHost(CustomNpc npc)
        => this.RunSafely("清除 Host", () =>
        {
            this.eventNpcHostService?.ClearHost(npc);
            this.LastMessage = $"已清除 Host：{npc.Id}";
        });

    public bool TestNpcHost(CustomNpc npc)
    {
        if (this.eventNpcHostService == null)
        {
            this.LastMessage = "EventNpcHostService 未初始化。";
            return false;
        }

        var result = this.eventNpcHostService.TestHost(npc, out var message);
        this.LastMessage = message;
        return result;
    }

    public bool IsCurrentTargetMatchingHost(CustomNpc npc)
        => this.eventNpcHostService?.IsCurrentTargetMatchingHost(npc) ?? false;

    public void RespawnInvalidActors()
        => this.RunSafely("重建所有失效 Actor", () =>
        {
            foreach (var instance in this.registry.GetAll().Where(actor => !actor.IsValid).ToList())
                this.RespawnActor(instance);
        });

    public void RespawnActor(string runtimeId)
        => this.RunSafely("重建失效 Actor", () =>
        {
            var instance = this.registry.GetByRuntimeId(runtimeId);
            if (instance != null)
                this.RespawnActor(instance);
        });

    public void RefreshActors()
    {
        foreach (var instance in this.registry.GetAll())
        {
            this.brioAssemblyBridge.RefreshActor(instance);
            if (this.targetabilityService.TryMatchCurrentTarget(instance))
                this.CurrentSelectedActorRuntimeId = instance.RuntimeId;
        }
    }

    public void CleanupActorsForMissingNpcs()
    {
        var npcIds = this.database.Npcs.Select(npc => npc.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.spawnIntentRegistry.RemoveMissingNpcs(npcIds);
        foreach (var instance in this.registry.GetAll().Where(instance => !npcIds.Contains(instance.NpcId)).ToList())
            this.Despawn(instance.RuntimeId, DespawnReason.ReloadCleanup);
    }

    public int GetActorCountForNpc(string npcId)
        => this.registry.GetByNpcId(npcId).Count;

    public RuntimeActorInstance? GetLatestActorForNpc(string npcId)
        => this.registry.GetLatestByNpcId(npcId);

    public RuntimeActorInstance? GetActor(string runtimeId)
        => this.registry.GetByRuntimeId(runtimeId);

    public void SelectActorForPlayerLookAt(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) == null)
        {
            this.CurrentSelectedActorRuntimeId = string.Empty;
            this.playerLookAtActorService.Stop();
            this.LastMessage = $"无法选中 Actor：{runtimeId}";
            return;
        }

        this.CurrentSelectedActorRuntimeId = runtimeId;
        this.LastMessage = $"已选中 Actor：{runtimeId}";
    }

    public void ClearSelectedActorForPlayerLookAt()
    {
        this.CurrentSelectedActorRuntimeId = string.Empty;
        this.playerLookAtActorService.Stop();
        this.LastMessage = "已取消选中 Actor，并停止玩家头部注视。";
    }

    public bool PlayerLookAtSelectedActorNow()
    {
        var actor = string.IsNullOrWhiteSpace(this.CurrentSelectedActorRuntimeId)
            ? null
            : this.registry.GetByRuntimeId(this.CurrentSelectedActorRuntimeId);
        if (actor == null)
        {
            this.LastMessage = "没有选中的 Actor。";
            return false;
        }

        var success = this.playerLookAtActorService.LookAt(this.CurrentSelectedActorRuntimeId, actor);
        this.LastMessage = this.playerLookAtActorService.LastResult;
        return success;
    }

    public void StopPlayerLookAt()
    {
        this.playerLookAtActorService.Stop();
        this.LastMessage = this.playerLookAtActorService.LastResult;
    }

    public CustomNpc? RefreshHostMatch()
    {
        var hostNpc = this.eventNpcHostService?.TryGetHostByTarget();
        if (hostNpc == null)
            return null;

        this.MatchedTargetNpcId = hostNpc.Id;
        this.MatchedTargetRuntimeId = "ExistingEventNpcHost";
        return hostNpc;
    }

    public CustomNpc? FindInteractableNpc(IEnumerable<CustomNpc> npcs, Vector3? playerPosition, uint territoryType)
    {
        this.RefreshActors();
        this.MatchedTargetNpcId = string.Empty;
        this.MatchedTargetRuntimeId = string.Empty;

        var npcList = npcs.ToList();
        var serviceHostNpc = this.eventNpcHostService?.TryGetHostByTarget();
        if (serviceHostNpc != null)
        {
            this.MatchedTargetNpcId = serviceHostNpc.Id;
            this.MatchedTargetRuntimeId = "ExistingEventNpcHost";
            return serviceHostNpc;
        }

        var legacyHostNpc = this.FindHostNpcForCurrentTarget(npcList, territoryType);
        if (legacyHostNpc != null)
            return legacyHostNpc;

        var targetObjectIndex = this.CurrentTargetObjectIndex;
        if (!string.IsNullOrWhiteSpace(targetObjectIndex) && targetObjectIndex != "不可用")
        {
            foreach (var instance in this.registry.GetAll())
            {
                if (!ObjectIndexMatches(targetObjectIndex, instance.ObjectIndex))
                    continue;

                var targetNpc = npcList.FirstOrDefault(item => string.Equals(item.Id, instance.NpcId, StringComparison.OrdinalIgnoreCase));
                if (targetNpc == null)
                    continue;

                this.MatchedTargetNpcId = targetNpc.Id;
                this.MatchedTargetRuntimeId = instance.RuntimeId;
                return targetNpc;
            }
        }

        if (playerPosition == null)
            return null;

        var nearest = this.registry.GetAll()
            .Where(instance => instance.IsValid)
            .Select(instance => new { Instance = instance, Npc = npcList.FirstOrDefault(npc => string.Equals(npc.Id, instance.NpcId, StringComparison.OrdinalIgnoreCase)) })
            .Where(item => item.Npc != null && item.Npc.TerritoryType == territoryType)
            .Select(item => new { item.Instance, Npc = item.Npc!, Distance = CalculateXZDistance(playerPosition.Value, item.Instance.LastKnownPosition) })
            .Where(item => item.Distance <= item.Npc.InteractRadius)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();

        if (nearest == null)
            return null;

        this.MatchedTargetNpcId = nearest.Npc.Id;
        this.MatchedTargetRuntimeId = nearest.Instance.RuntimeId;
        return nearest.Npc;
    }

    private CustomNpc? FindHostNpcForCurrentTarget(IEnumerable<CustomNpc> npcs, uint territoryType)
    {
        var target = this.targetManager.Target;
        if (target == null)
            return null;

        var targetObjectIndex = ReadTargetObjectIndex(target);
        var targetDataId = ParseUInt(ReadMember(target, "DataId"));
        foreach (var npc in npcs)
        {
            if (npc.NativeHostMode != NativeHostMode.ExistingEventNpcHost || !npc.UseLocalDialogueOnInteract)
                continue;

            if (npc.HostTerritoryType != 0 && npc.HostTerritoryType != territoryType)
                continue;

            if (npc.HostDataId != 0 && npc.HostDataId == targetDataId)
            {
                this.MatchedTargetNpcId = npc.Id;
                this.MatchedTargetRuntimeId = "ExistingEventNpcHost";
                return npc;
            }

            if (!string.IsNullOrWhiteSpace(npc.HostObjectIndex) && ObjectIndexMatches(targetObjectIndex, npc.HostObjectIndex))
            {
                this.MatchedTargetNpcId = npc.Id;
                this.MatchedTargetRuntimeId = "ExistingEventNpcHost";
                return npc;
            }
        }

        return null;
    }

    public void Update()
    {
        this.appearanceApplyQueue.Update();
        this.validityMonitorService.Update(this.registry.GetAll());
        this.actionSequenceService.Update(this.registry.GetAll());
        this.lookAtService.Update(this.registry.GetAll(), this.database);
        var selectedActor = string.IsNullOrWhiteSpace(this.CurrentSelectedActorRuntimeId)
            ? null
            : this.registry.GetByRuntimeId(this.CurrentSelectedActorRuntimeId);
        this.playerLookAtActorService.Update(this.CurrentSelectedActorRuntimeId, selectedActor, this.validityMonitorService.CurrentIsGposing);
        if (this.validityMonitorService.ConsumeGposeExitReady())
            this.RebuildActorsAfterGposeExit();
        this.RebuildMissingIntentActorsIfNeeded();

        var territoryType = this.clientState.TerritoryType;
        if (territoryType == this.lastTerritoryType)
        {
            this.RefreshActors();
            return;
        }

        this.lastTerritoryType = territoryType;
        this.DespawnAll(DespawnReason.TerritoryChanged);
    }

    public void SetMessage(string message)
        => this.LastMessage = message;

    public void ReportUiException(Exception ex, string action)
    {
        this.log.Error(ex, "[RealNpcSpawnService] UI button failed while running {Action}", action);
        this.LastMessage = $"{action}失败：{ex.Message}";
    }

    private void RunSafely(string action, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[RealNpcSpawnService] {Action} failed", action);
            this.LastMessage = $"{action}失败：{ex.Message}";
        }
    }

    public void EnableGposeRespawnForNpc(string npcId)
        => this.RunSafely("标记 GPose 自动重建", () =>
        {
            var npc = this.database.GetNpcById(npcId);
            if (npc == null)
            {
                this.LastMessage = $"找不到 NPC 配置：{npcId}";
                return;
            }

            npc.RespawnAfterGpose = true;
            this.database.Save();
            this.spawnIntentRegistry.MarkShouldSpawn(npc);
            this.LastMessage = $"已标记退出 GPose 后自动重建：{npc.Name}";
        });

    public void DisableGposeRespawnForNpc(string npcId)
        => this.RunSafely("取消 GPose 自动重建", () =>
        {
            var npc = this.database.GetNpcById(npcId);
            if (npc != null)
            {
                npc.RespawnAfterGpose = false;
                this.database.Save();
            }

            var intent = this.spawnIntentRegistry.Get(npcId);
            if (intent != null)
                intent.RespawnAfterGpose = false;

            this.LastMessage = $"已取消退出 GPose 后自动重建：{npcId}";
        });

    public void RunGposeRebuildNow()
        => this.RunSafely("手动执行 GPose 重建流程", this.RebuildActorsAfterGposeExit);

    private void RespawnActor(RuntimeActorInstance instance)
    {
        var npc = this.database.GetNpcById(instance.NpcId);
        if (npc == null)
        {
            this.LastMessage = $"无法重建 Actor，NPC 配置不存在：{instance.NpcId}";
            return;
        }

        this.Despawn(instance.RuntimeId, DespawnReason.InvalidActorCleanup);
        this.SpawnUnique(npc);
    }

    private void RebuildActorsAfterGposeExit()
    {
        var oldActors = this.registry.GetAll()
            .Where(actor => string.Equals(actor.SpawnSource, "BrioAssembly", StringComparison.OrdinalIgnoreCase) || actor.CharacterObject != null)
            .ToList();

        this.log.Information("[RealNpcSpawnService] GPose exit detected. Clearing old actor pointers and queuing rebuild. Count={Count}", oldActors.Count);
        this.actionSequenceService.StopAll();
        var rebuildSnapshots = oldActors
            .Select(this.CreateGposeRebuildSnapshot)
            .Where(snapshot => snapshot != null)
            .Cast<GposeActorRebuildSnapshot>()
            .ToList();

        foreach (var actor in oldActors)
        {
            actor.IsValid = false;
            actor.LastError = "Actor 已失效，可能由 GPose 切换清理";
            this.spawnIntentRegistry.MarkDespawned(actor.NpcId, DespawnReason.GposeCleanup);
            this.registry.Remove(actor.RuntimeId);
        }

        this.gposeRebuildQueueCount = rebuildSnapshots.Count;
        var successCount = 0;
        foreach (var snapshot in rebuildSnapshots)
        {
            try
            {
                this.log.Information("[RealNpcSpawnService] Rebuild actor after GPose begin. RuntimeId={OldRuntimeId}, NpcId={NpcId}, Name={Name}", snapshot.OldRuntimeId, snapshot.Npc.Id, snapshot.Npc.Name);
                var rebuilt = this.SpawnNew(snapshot.Npc);
                if (rebuilt == null)
                    continue;

                this.ApplyGposeRebuildSnapshot(rebuilt, snapshot);
                this.spawnIntentRegistry.UpdateLastRuntime(snapshot.Npc, rebuilt);
                successCount++;
                this.log.Information("[RealNpcSpawnService] Rebuild actor after GPose end. OldRuntimeId={OldRuntimeId}, NewRuntimeId={NewRuntimeId}", snapshot.OldRuntimeId, rebuilt.RuntimeId);
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "[RealNpcSpawnService] Failed to rebuild actor after GPose. NpcId={NpcId}", snapshot.Npc.Id);
            }
            finally
            {
                this.gposeRebuildQueueCount = Math.Max(0, this.gposeRebuildQueueCount - 1);
            }
        }

        this.LastGposeRebuildResult = $"GPose 退出后重建完成：旧 Actor {oldActors.Count} 个，重建 {successCount}/{rebuildSnapshots.Count} 个。";
        this.LastMessage = this.LastGposeRebuildResult;
    }

    private GposeActorRebuildSnapshot? CreateGposeRebuildSnapshot(RuntimeActorInstance actor)
    {
        var npc = this.database.GetNpcById(actor.NpcId);
        if (npc == null)
            return null;

        return new GposeActorRebuildSnapshot
        {
            OldRuntimeId = actor.RuntimeId,
            Npc = npc,
            Position = actor.TransformEditPosition == Vector3.Zero ? actor.SpawnPosition : actor.TransformEditPosition,
            RotationEuler = actor.TransformEditRotationEuler,
            Scale = NormalizeScale(actor.TransformEditScale == Vector3.Zero ? actor.SpawnScale : actor.TransformEditScale),
            DefaultAnimationId = actor.DefaultAnimationId,
            CurrentAnimationId = actor.CurrentAnimationId,
            AnimationEnabled = actor.AnimationEnabled,
            EnableActionSequence = actor.EnableActionSequence,
            ActionSequenceLoop = actor.ActionSequenceLoop,
            ActionSequenceLoopDelay = actor.ActionSequenceLoopDelay,
            ActionSequence = actor.ActionSequence.Select(CloneActionSequenceStep).ToList(),
            LookAtPlayerEnabled = actor.LookAtPlayerEnabled,
            LookAtRadius = actor.LookAtRadius,
            PenumbraMode = actor.PenumbraMode,
            PenumbraCollectionId = actor.PenumbraCollectionId,
            PenumbraCollectionNameCache = actor.PenumbraCollectionNameCache,
            AnimationRigMode = actor.AnimationRigMode,
            AnimationRigPreset = actor.AnimationRigPreset,
            CustomRigRace = actor.CustomRigRace,
            CustomRigSex = actor.CustomRigSex,
            CustomRigTribe = actor.CustomRigTribe,
        };
    }

    private void ApplyGposeRebuildSnapshot(RuntimeActorInstance actor, GposeActorRebuildSnapshot snapshot)
    {
        actor.DefaultAnimationId = snapshot.DefaultAnimationId;
        actor.CurrentAnimationId = snapshot.CurrentAnimationId;
        actor.AnimationEnabled = snapshot.AnimationEnabled;
        actor.EnableActionSequence = snapshot.EnableActionSequence;
        actor.ActionSequenceLoop = snapshot.ActionSequenceLoop;
        actor.ActionSequenceLoopDelay = snapshot.ActionSequenceLoopDelay;
        actor.ActionSequence = snapshot.ActionSequence.Select(CloneActionSequenceStep).ToList();
        actor.LookAtPlayerEnabled = snapshot.LookAtPlayerEnabled;
        actor.LookAtRadius = Math.Max(0.1f, snapshot.LookAtRadius);
        actor.LookAtMode = NpcLookAtMode.NativeLookAt;
        actor.PenumbraMode = snapshot.PenumbraMode;
        actor.PenumbraCollectionId = snapshot.PenumbraCollectionId;
        actor.PenumbraCollectionNameCache = snapshot.PenumbraCollectionNameCache;
        actor.AnimationRigMode = snapshot.AnimationRigMode;
        actor.AnimationRigPreset = snapshot.AnimationRigPreset;
        actor.CustomRigRace = snapshot.CustomRigRace;
        actor.CustomRigSex = snapshot.CustomRigSex;
        actor.CustomRigTribe = snapshot.CustomRigTribe;
        actor.VisibilityRuntimeState = ActorVisibilityRuntimeState.Visible;
        actor.LookAtPausedByActionSequence = false;

        this.ApplyActorTransform(actor.RuntimeId, snapshot.Position, snapshot.RotationEuler, snapshot.Scale);
        this.EnqueueNpcAppearance(actor.RuntimeId);
        this.actionSequenceService.Reset(actor);

        if (actor.AnimationRigMode == ActorAnimationRigMode.Override && actor.AnimationRigPreset != ActorAnimationRigPreset.Current)
            this.animationRigService.ApplyAnimationRigOverride(actor, out _);

        if (!actor.EnableActionSequence && actor.CurrentAnimationId > 0)
            this.animationService.PlayTransientTimeline(actor, actor.CurrentAnimationId, out _);
    }

    private static ActorActionSequenceStep CloneActionSequenceStep(ActorActionSequenceStep step)
        => new()
        {
            Id = step.Id,
            Name = step.Name,
            Kind = step.Kind,
            DurationSeconds = step.DurationSeconds,
            AnimationId = step.AnimationId,
            LoopAnimation = step.LoopAnimation,
            StayInPose = step.StayInPose,
            RepeatAfterSeconds = step.RepeatAfterSeconds,
            ExpressionId = step.ExpressionId,
            PlayExpressionWithAction = step.PlayExpressionWithAction,
            ExpressionDelaySeconds = step.ExpressionDelaySeconds,
            ExpressionDurationSeconds = step.ExpressionDurationSeconds,
            LoopExpression = step.LoopExpression,
            ExpressionWeight = step.ExpressionWeight,
            ExpressionLayer = step.ExpressionLayer,
            BubbleText = step.BubbleText,
            BubbleDurationSeconds = step.BubbleDurationSeconds,
            BubbleUseAutoDuration = step.BubbleUseAutoDuration,
            ShowBubbleOnEnter = step.ShowBubbleOnEnter,
            HideBubbleOnDespawn = step.HideBubbleOnDespawn,
            AllowLookAtDuringStep = step.AllowLookAtDuringStep,
        };

    private void RebuildMissingIntentActorsIfNeeded()
    {
        if (this.validityMonitorService.CurrentIsGposing || this.validityMonitorService.IsRebuildScheduled)
            return;

        var now = DateTime.UtcNow;
        if (now - this.lastSpawnIntentFallbackCheckAt < TimeSpan.FromSeconds(2))
            return;

        this.lastSpawnIntentFallbackCheckAt = now;
        var rebuilt = 0;
        foreach (var intent in this.spawnIntentRegistry.GetAll().Where(intent => intent.ShouldBeSpawned && !intent.SuppressedUntilUserSpawn))
        {
            var hasValidActor = this.registry.GetByNpcId(intent.NpcId).Any(actor => actor.IsValid);
            if (hasValidActor)
                continue;

            var npc = this.database.GetNpcById(intent.NpcId);
            if (npc == null)
                continue;

            if (this.SpawnUnique(npc) != null)
                rebuilt++;
        }

        if (rebuilt > 0)
        {
            this.LastGposeRebuildResult = $"备用检查已重建缺失 Actor：{rebuilt} 个。";
            this.LastMessage = this.LastGposeRebuildResult;
        }
    }

    private sealed class GposeActorRebuildSnapshot
    {
        public string OldRuntimeId { get; init; } = string.Empty;

        public required CustomNpc Npc { get; init; }

        public Vector3 Position { get; init; }

        public Vector3 RotationEuler { get; init; }

        public Vector3 Scale { get; init; }

        public uint DefaultAnimationId { get; init; }

        public uint CurrentAnimationId { get; init; }

        public bool AnimationEnabled { get; init; }

        public bool EnableActionSequence { get; init; }

        public bool ActionSequenceLoop { get; init; }

        public float ActionSequenceLoopDelay { get; init; }

        public List<ActorActionSequenceStep> ActionSequence { get; init; } = [];

        public bool LookAtPlayerEnabled { get; init; }

        public float LookAtRadius { get; init; }

        public PenumbraCollectionMode PenumbraMode { get; init; }

        public Guid? PenumbraCollectionId { get; init; }

        public string PenumbraCollectionNameCache { get; init; } = string.Empty;

        public ActorAnimationRigMode AnimationRigMode { get; init; }

        public ActorAnimationRigPreset AnimationRigPreset { get; init; }

        public byte CustomRigRace { get; init; }

        public byte CustomRigSex { get; init; }

        public byte CustomRigTribe { get; init; }
    }

    private static float CalculateXZDistance(Vector3 playerPosition, Vector3 targetPosition)
    {
        var deltaX = playerPosition.X - targetPosition.X;
        var deltaZ = playerPosition.Z - targetPosition.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static bool ObjectIndexMatches(string targetObjectIndex, string spawnedObjectIndex)
    {
        if (string.Equals(targetObjectIndex, spawnedObjectIndex, StringComparison.OrdinalIgnoreCase))
            return true;

        return int.TryParse(targetObjectIndex, out var targetIndex)
               && int.TryParse(spawnedObjectIndex, out var spawnedIndex)
               && targetIndex == spawnedIndex;
    }

    private static string ReadTargetObjectIndex(object? target)
    {
        if (target == null)
            return "不可用";

        foreach (var memberName in new[] { "ObjectIndex", "ObjectTableIndex", "Index" })
        {
            try
            {
                var type = target.GetType();
                var propertyValue = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)?.ToString();
                if (!string.IsNullOrWhiteSpace(propertyValue))
                    return propertyValue;

                var fieldValue = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)?.ToString();
                if (!string.IsNullOrWhiteSpace(fieldValue))
                    return fieldValue;
            }
            catch
            {
            }
        }

        return "不可用";
    }

    private bool RunEventNpcExperiment(string runtimeId, Func<RuntimeActorInstance, bool> action)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        var success = action(instance);
        this.targetabilityService.TryReadTargetability(instance);
        this.LastMessage = this.experimentalEventNpcService.LastResult;
        return success;
    }

    private static uint ParseUInt(string? raw)
        => uint.TryParse(raw, out var value) ? value : 0;

    private static string ReadMember(object source, string name)
    {
        try
        {
            var type = source.GetType();
            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)?.ToString()
                ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)?.ToString()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatDisplayName(CustomNpc npc)
    {
        var template = string.IsNullOrWhiteSpace(npc.NameTemplate) ? "{name}" : npc.NameTemplate;
        var name = string.IsNullOrWhiteSpace(npc.Name) ? npc.Id : npc.Name;
        return template.Replace("{name}", name, StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortRuntimeId(string runtimeId)
        => string.IsNullOrWhiteSpace(runtimeId) ? "无" : runtimeId[..Math.Min(8, runtimeId.Length)];
}
