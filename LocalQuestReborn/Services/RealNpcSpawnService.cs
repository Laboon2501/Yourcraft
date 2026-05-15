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
    private readonly Queue<QueuedActorSpawnRequest> pendingActorSpawns = new();
    private readonly HashSet<nint> warmupActorPointers = new();
    private readonly HashSet<int> warmupActorObjectIndexes = new();
    private readonly HashSet<nint> sacrificialClonePointers = new();
    private readonly HashSet<int> sacrificialCloneObjectIndexes = new();
    private RuntimeActorInstance? activeSpawnWarmupActor;
    private bool spawnWarmupDoneForCurrentScene;
    private bool spawnWarmupInProgress;
    private bool spawnWarmupFailed;
    private int spawnWarmupTicks;
    private int scenePrewarmStableTicks;
    private DateTime lastSpawnWarmupFailureAt = DateTime.MinValue;
    private string spawnWarmupReason = string.Empty;
    private string activeGposeRebuildRuntimeId = string.Empty;
    private GposeActorRebuildSnapshot? activeGposeRebuildSnapshot;
    private int gposeRebuildTotalCount;
    private int gposeRebuildSuccessCount;
    private const int SpawnWarmupMinimumTicks = 3;
    private const int MaxLocalPlayerCloneRetries = 3;

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
    public string SpawnPrewarmStatus
        => this.spawnWarmupInProgress
            ? $"Running ticks={this.spawnWarmupTicks} reason={this.spawnWarmupReason}"
            : this.spawnWarmupDoneForCurrentScene
                ? "Ready"
                : this.spawnWarmupFailed
                    ? $"Failed reason={this.spawnWarmupReason}"
                    : "NotStarted";
    public string FormalQueueStatus
        => this.pendingActorSpawns.Count > 0 ||
           this.activePostSpawnApply != null ||
           this.postSpawnApplyQueue.Count > 0 ||
           this.pendingGposeRebuilds.Count > 0 ||
           this.activeGposeRebuildSnapshot != null ||
           !string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId)
            ? $"Running pendingSpawn={this.pendingActorSpawns.Count}, activePostSpawn={this.activePostSpawnApply?.RuntimeId ?? "none"}, postSpawnQueue={this.postSpawnApplyQueue.Count}, gposePending={this.pendingGposeRebuilds.Count}, activeGpose={this.activeGposeRebuildRuntimeId}"
            : "Idle";
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
    public bool IsAnimationRigOverrideSupported => this.animationRigService.IsSupported;
    public string AnimationRigUnsupportedReason => this.animationRigService.UnsupportedReason;
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

    private int GetNpcSortOrder(string npcId)
    {
        var index = this.database.Npcs.FindIndex(npc => string.Equals(npc.Id, npcId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : int.MaxValue;
    }

    public void ProbeBrioIpc()
        => this.RunSafely("探测 Brio IPC", () =>
        {
            this.brioBridge.IpcProbe.Probe();
            this.ResetSpawnWarmup("Brio manual probe/reconnect");
            this.EnsureSpawnWarmup("Brio manual probe/reconnect");
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

    private bool EnsureSpawnWarmup(string reason)
    {
        if (this.spawnWarmupDoneForCurrentScene)
            return true;

        if (this.spawnWarmupFailed && DateTime.UtcNow - this.lastSpawnWarmupFailureAt < TimeSpan.FromSeconds(3))
        {
            this.LastMessage = $"Actor spawn prewarm failed; formal queue is waiting before retry. reason={this.spawnWarmupReason}";
            return false;
        }

        if (!this.spawnWarmupInProgress)
            this.StartSpawnWarmup(reason);

        return this.spawnWarmupDoneForCurrentScene;
    }

    private void StartSpawnWarmup(string reason)
    {
        if (!this.CanSpawnRealActor)
            return;

        var warmupNpc = new CustomNpc
        {
            Id = "__LocalQuestRebornSpawnWarmup",
            Name = "LocalQuestReborn Spawn Warmup",
            DefaultSpawnOffset = new Vector3Data(),
            DefaultScale = new Vector3Data { X = 1f, Y = 1f, Z = 1f },
        };

        this.spawnWarmupReason = reason;
        this.spawnWarmupTicks = 0;
        this.spawnWarmupInProgress = true;
        this.spawnWarmupDoneForCurrentScene = false;
        this.spawnWarmupFailed = false;
        this.activeSpawnWarmupActor = null;
        this.LastMessage = $"正在初始化 Actor 创建器：hidden warm-up ({reason})";
        this.log.Information("[SpawnWarmup] required reason={Reason}", reason);
        this.log.Information("[SpawnWarmup] create begin reason={Reason}", reason);

        if (!this.brioAssemblyBridge.TrySpawnActor(warmupNpc, $"warmup-{Guid.NewGuid():N}", out var warmupActor, out var createReason))
        {
            this.spawnWarmupInProgress = false;
            this.spawnWarmupDoneForCurrentScene = false;
            this.spawnWarmupFailed = true;
            this.lastSpawnWarmupFailureAt = DateTime.UtcNow;
            this.LastMessage = $"Actor spawn prewarm failed; formal spawn queue is waiting. {createReason}";
            this.log.Warning("[Prewarm] dummy create failed; formal spawns remain queued. reason={Reason}", createReason);
            return;
        }

        this.activeSpawnWarmupActor = warmupActor;
        this.TrackWarmupActor(warmupActor);
        this.SetPostSpawnQuarantineVisibility(warmupActor, visible: false, "spawn warm-up hidden sacrificial actor");
        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        var warmupAppearance = BuildAppearanceSignature(warmupActor.CharacterObject).Summary;
        var localAppearance = BuildAppearanceSignature(localPlayer).Summary;
        this.log.Information("[SpawnWarmup] create returned ptr={Ptr} index={Index} appearanceHash={Appearance} localAppearanceHash={LocalAppearance}",
            warmupActor.Address,
            warmupActor.ObjectIndex,
            warmupAppearance,
            localAppearance);
        this.LastMessage = $"已创建 hidden warm-up Actor，等待 {SpawnWarmupMinimumTicks} tick 后开始正式生成。";
    }

    private void UpdateSpawnWarmupLifecycle()
    {
        if (!this.spawnWarmupInProgress)
            return;

        this.spawnWarmupTicks++;
        var actor = this.activeSpawnWarmupActor;
        if (actor != null)
        {
            this.SetPostSpawnQuarantineVisibility(actor, visible: false, $"spawn warm-up tick {this.spawnWarmupTicks}");
            this.TrackWarmupActor(actor);
        }

        if (this.spawnWarmupTicks < SpawnWarmupMinimumTicks)
            return;

        if (actor != null)
        {
            this.log.Information("[SpawnWarmup] hidden ptr={Ptr} index={Index}; attempting stable delete after ticks={Ticks}", actor.Address, actor.ObjectIndex, this.spawnWarmupTicks);
            if (this.brioAssemblyBridge.TryDespawnActor(actor, out var deleteReason))
            {
                this.log.Information("[SpawnWarmup] delete result ptr={Ptr} index={Index} reason={Reason}", actor.Address, actor.ObjectIndex, deleteReason);
                this.UntrackActorIdentity(actor, this.warmupActorPointers, this.warmupActorObjectIndexes);
            }
            else
            {
                this.log.Warning("[SpawnWarmup] delete failed; keeping hidden orphan ptr={Ptr} index={Index} reason={Reason}", actor.Address, actor.ObjectIndex, deleteReason);
                this.TrackWarmupActor(actor);
            }
        }

        this.activeSpawnWarmupActor = null;
        this.spawnWarmupInProgress = false;
        this.spawnWarmupDoneForCurrentScene = true;
        this.spawnWarmupFailed = false;
        this.LastMessage = $"Actor 创建器 warm-up 完成：{this.spawnWarmupReason}";
        this.log.Information("[Prewarm] ready=true reason={Reason}", this.spawnWarmupReason);
    }

    private void UpdateAutomaticSpawnPrewarm()
    {
        if (this.spawnWarmupDoneForCurrentScene ||
            this.spawnWarmupInProgress ||
            this.validityMonitorService.CurrentIsGposing ||
            !this.CanSpawnRealActor)
            return;

        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        if (localPlayer == null)
        {
            this.scenePrewarmStableTicks = 0;
            return;
        }

        this.scenePrewarmStableTicks++;
        if (this.scenePrewarmStableTicks < 8)
            return;

        this.log.Information("[Prewarm] auto scheduled on stable scene. territory={Territory} stableTicks={Ticks}", this.clientState.TerritoryType, this.scenePrewarmStableTicks);
        this.EnsureSpawnWarmup("auto stable scene");
    }

    private void ResetSpawnWarmup(string reason)
    {
        this.spawnWarmupDoneForCurrentScene = false;
        this.spawnWarmupInProgress = false;
        this.spawnWarmupFailed = false;
        this.spawnWarmupTicks = 0;
        this.scenePrewarmStableTicks = 0;
        this.activeSpawnWarmupActor = null;
        this.spawnWarmupReason = reason;
        this.warmupActorPointers.Clear();
        this.warmupActorObjectIndexes.Clear();
        this.sacrificialClonePointers.Clear();
        this.sacrificialCloneObjectIndexes.Clear();
        this.log.Information("[SpawnWarmup] reset reason={Reason}", reason);
    }

    private void TrackWarmupActor(RuntimeActorInstance actor)
        => this.TrackActorIdentity(actor, this.warmupActorPointers, this.warmupActorObjectIndexes);

    private void TrackSacrificialCloneActor(RuntimeActorInstance actor)
        => this.TrackActorIdentity(actor, this.sacrificialClonePointers, this.sacrificialCloneObjectIndexes);

    private void TrackActorIdentity(RuntimeActorInstance actor, HashSet<nint> pointers, HashSet<int> objectIndexes)
    {
        if (TryParseAddress(actor.Address, out var pointer) && pointer != 0)
            pointers.Add(pointer);
        if (int.TryParse(actor.ObjectIndex, out var index) && index >= 0)
            objectIndexes.Add(index);
    }

    private void UntrackActorIdentity(RuntimeActorInstance actor, HashSet<nint> pointers, HashSet<int> objectIndexes)
    {
        if (TryParseAddress(actor.Address, out var pointer) && pointer != 0)
            pointers.Remove(pointer);
        if (int.TryParse(actor.ObjectIndex, out var index) && index >= 0)
            objectIndexes.Remove(index);
    }

    public RuntimeActorInstance? SpawnNew(CustomNpc npc, Vector3? overrideSpawnPosition = null, bool requireWarmup = true, int cloneRetryAttempt = 0)
    {
        try
        {
            var runtimeId = Guid.NewGuid().ToString("N");
            var spawnPosition = overrideSpawnPosition ?? this.GetTemplateSpawnPosition(npc);
            var spawnRotation = ToVector3(npc.DefaultRotationEuler);
            var spawnScale = NormalizeScale(ToVector3(npc.DefaultScale));
            if (!this.CanSpawnRealActor)
            {
                this.LastMessage = "Brio Assembly 不可用，未生成真实 Actor。";
                return null;
            }

            if (requireWarmup && !this.EnsureSpawnWarmup($"formal spawn npc={npc.Id}"))
            {
                this.EnqueueActorSpawnFront(new QueuedActorSpawnRequest(
                    npc,
                    spawnPosition,
                    0,
                    this.GetNpcSortOrder(npc.Id),
                    cloneRetryAttempt,
                    UpdateSpawnIntent: false,
                    Source: "direct-warmup-gate"));
                this.LastMessage = $"正在执行隐藏 Actor 创建 warm-up，正式生成已排队：{npc.Name}";
                return null;
            }

            if (!this.brioAssemblyBridge.TrySpawnActor(npc, runtimeId, out var instance, out var reason))
            {
                this.LastMessage = $"生成真实 Actor 失败：{reason}";
                return null;
            }

            var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
            this.log.Information("[ActorSpawn] requested instance={RuntimeId} order={Order} generation=pending npc={NpcId}/{NpcName} localPlayer ptr={LocalPtr} index={LocalIndex} spawn returned ptr={Ptr} index={Index} name={Name}",
                runtimeId,
                this.GetNpcSortOrder(npc.Id),
                npc.Id,
                npc.Name,
                ReadMember(localPlayer, "Address"),
                ReadTargetObjectIndex(localPlayer),
                instance.Address,
                instance.ObjectIndex,
                instance.CurrentNativeName);
            if (this.IsLocalPlayerActor(instance))
            {
                this.LastMessage = $"生成 Actor 失败：CreateCharacter 返回了 LocalPlayer，已拒绝绑定和外观应用。npc={npc.Name}";
                this.log.Error("[ActorSpawn] rejected returned LocalPlayer target. runtime={RuntimeId}, npc={NpcId}, ptr={Ptr}, index={Index}", runtimeId, npc.Id, instance.Address, instance.ObjectIndex);
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
            instance.HasSavedTransform = true;
            instance.DefaultAnimationId = npc.DefaultAnimationId;
            instance.CurrentAnimationId = npc.DefaultAnimationId;
            instance.LookAtPlayerEnabled = npc.LookAtPlayerEnabled;
            instance.LookAtRadius = Math.Max(0.1f, npc.LookAtRadius);
            instance.LookAtMode = NpcLookAtMode.NativeLookAt;
            instance.PenumbraMode = npc.PenumbraMode;
            instance.PenumbraCollectionId = npc.PenumbraCollectionId;
            instance.PenumbraCollectionNameCache = npc.PenumbraCollectionNameCache;
            instance.PostSpawnBehaviorReady = false;
            instance.PostSpawnPipelineState = "NativeCreated";
            instance.PostSpawnPipelineStatus = "Spawn returned; quarantining actor until non-LocalPlayer target and appearance verify succeed.";
            this.actionSequenceService.Reset(instance);
            this.nameplateService.TryReadActorName(instance);
            this.targetabilityService.TryReadTargetability(instance);
            this.SetPostSpawnQuarantineVisibility(instance, visible: false, "spawn quarantine");
            this.RestoreTransformExact(instance, "after CreateCharacter quarantine");
            this.QueuePostSpawnApply(instance.RuntimeId, "spawn", PostSpawnApplyState.WaitingObjectStable, cloneRetryAttempt);

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
        if (!this.EnsureSpawnWarmup($"unique spawn npc={npc.Id}"))
        {
            this.EnqueueActorSpawnFront(new QueuedActorSpawnRequest(
                npc,
                this.GetTemplateSpawnPosition(npc),
                0,
                this.GetNpcSortOrder(npc.Id),
                CloneRetryAttempt: 0,
                UpdateSpawnIntent: true,
                Source: "unique-warmup-gate"));
            this.LastMessage = $"正在执行隐藏 Actor 创建 warm-up，唯一生成已排队：{npc.Name}";
            return null;
        }

        var instance = this.SpawnNew(npc, requireWarmup: false);
        if (instance != null)
            this.spawnIntentRegistry.UpdateLastRuntime(npc, instance);
        return instance;
    }

    public void QueueSpawnMany(CustomNpc npc, int count, Vector3 basePosition, Vector3 offset)
    {
        var safeCount = Math.Clamp(count, 1, 50);
        for (var index = 0; index < safeCount; index++)
        {
            this.pendingActorSpawns.Enqueue(new QueuedActorSpawnRequest(
                npc,
                basePosition + offset * index,
                index,
                this.GetNpcSortOrder(npc.Id),
                CloneRetryAttempt: 0,
                UpdateSpawnIntent: false,
                Source: "batch"));
        }

        this.LastMessage = $"已加入串行 Actor 生成队列：{npc.Name} x{safeCount}。每个 Actor Ready/Failed 后才生成下一个。";
        this.log.Information("[ActorSpawnQueue] queued count={Count} npc={NpcId}/{NpcName} base={Base} offset={Offset}", safeCount, npc.Id, npc.Name, basePosition, offset);
    }

    private void EnqueueActorSpawnFront(QueuedActorSpawnRequest request)
    {
        var remaining = this.pendingActorSpawns.ToList();
        this.pendingActorSpawns.Clear();
        this.pendingActorSpawns.Enqueue(request);
        foreach (var item in remaining)
            this.pendingActorSpawns.Enqueue(item);
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
            instance.HasSavedTransform = true;
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

        if (!this.brioCapabilityBridge.TryApplyModelTransform(instance, position, rotationEuler, safeScale, out var transformReason))
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
        instance.TransformEditPosition = position;
        instance.TransformEditRotationEuler = rotationEuler;
        instance.TransformEditScale = safeScale;
        instance.SpawnPosition = position;
        instance.SpawnRotationEuler = rotationEuler;
        instance.SpawnScale = safeScale;
        instance.LastKnownPosition = position;
        instance.LastKnownRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(rotationEuler.Y, rotationEuler.X, rotationEuler.Z));
        instance.LastKnownRotationEuler = rotationEuler;
        instance.LastKnownScale = safeScale;
        instance.HasSavedTransform = true;
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
        instance.SpawnPosition = position;
        instance.SpawnRotationEuler = rotationEuler;
        instance.SpawnScale = NormalizeScale(scale);
        instance.HasSavedTransform = true;
        instance.SavedTransformSnapshot = $"worldPosition={position}; worldEulerRadians={rotationEuler}; worldScale={NormalizeScale(scale)}";
        this.LastMessage = $"已保存当前 World Transform：{instance.SavedTransformSnapshot}";
    }

    private bool RestoreTransformExact(RuntimeActorInstance actor, string stage)
    {
        var world = this.GetAuthoritativeWorldTransform(actor);
        var position = world.WorldPosition;
        var rotationEuler = world.WorldEulerRadians;
        var scale = world.WorldScale;
        actor.TransformEditPosition = position;
        actor.TransformEditRotationEuler = rotationEuler;
        actor.TransformEditScale = scale;
        actor.SpawnPosition = position;
        actor.SpawnRotationEuler = rotationEuler;
        actor.SpawnScale = scale;
        actor.HasSavedTransform = true;

        var success = this.ApplyActorTransform(actor.RuntimeId, position, rotationEuler, scale);
        this.log.Information("[ActorTransform] RestoreWorld {Stage} actor={Actor} expectedWorldPos={Position} expectedWorldEuler={Rotation} expectedWorldScale={Scale} success={Success} result={Result}",
            stage,
            actor.RuntimeId,
            position,
            rotationEuler,
            scale,
            success,
            actor.LastTransformError);
        return success;
    }

    private void VerifyNoUnexpectedTransformChange(RuntimeActorInstance actor, string stage)
    {
        var expectedWorld = this.GetAuthoritativeWorldTransform(actor);
        var expectedPosition = expectedWorld.WorldPosition;
        var expectedRotation = expectedWorld.WorldEulerRadians;
        var expectedScale = expectedWorld.WorldScale;
        var preservedReadback = actor.LastTransformReadback;
        if (!actor.IsValid || actor.CharacterObject == null)
            return;

        if (!this.brioCapabilityBridge.TryReadModelTransform(actor, out var readReason))
        {
            actor.TransformEditPosition = expectedPosition;
            actor.TransformEditRotationEuler = expectedRotation;
            actor.TransformEditScale = expectedScale;
            actor.SpawnPosition = expectedPosition;
            actor.SpawnRotationEuler = expectedRotation;
            actor.SpawnScale = expectedScale;
            actor.LastTransformReadback = preservedReadback;
            this.log.Warning("[ActorTransform] VerifyWorld {Stage} read failed actor={Actor} reason={Reason}", stage, actor.RuntimeId, readReason);
            return;
        }

        var actualPosition = actor.LastKnownPosition;
        var actualRotation = actor.LastKnownRotationEuler;
        var actualScale = actor.LastKnownScale;
        var positionDelta = Vector3.Distance(actualPosition, expectedPosition);
        var rotationDelta = Vector3.Distance(actualRotation, expectedRotation);
        var scaleDelta = Vector3.Distance(actualScale, expectedScale);

        actor.TransformEditPosition = expectedPosition;
        actor.TransformEditRotationEuler = expectedRotation;
        actor.TransformEditScale = expectedScale;
        actor.SpawnPosition = expectedPosition;
        actor.SpawnRotationEuler = expectedRotation;
        actor.SpawnScale = expectedScale;
        actor.HasSavedTransform = true;

        var changed = positionDelta > 0.01f || rotationDelta > 0.01f || scaleDelta > 0.01f;
        actor.LastTransformReadback = $"stage={stage}; expected world pos={expectedPosition}, world euler={expectedRotation}, world scale={expectedScale}; actual world pos={actualPosition}, world euler={actualRotation}, world scale={actualScale}; delta pos={positionDelta:F4}, rot={rotationDelta:F4}, scale={scaleDelta:F4}";
        if (!changed)
        {
            this.log.Information("[ActorTransform] VerifyWorld {Stage} result=ok actor={Actor} {Readback}", stage, actor.RuntimeId, actor.LastTransformReadback);
            return;
        }

        this.log.Warning("[ActorTransform] VerifyWorld {Stage} result=changed actor={Actor}; restoring world transform. {Readback}", stage, actor.RuntimeId, actor.LastTransformReadback);
        this.RestoreTransformExact(actor, $"{stage} guard restore");
    }

    private (Vector3 Position, Vector3 RotationEuler, Vector3 Scale) GetAuthoritativeTransform(RuntimeActorInstance actor)
    {
        var world = this.GetAuthoritativeWorldTransform(actor);
        return (world.WorldPosition, world.WorldEulerRadians, world.WorldScale);
    }

    private WorldTransform GetAuthoritativeWorldTransform(RuntimeActorInstance actor)
    {
        var position = actor.HasSavedTransform ? actor.TransformEditPosition : actor.SpawnPosition;
        var rotation = actor.HasSavedTransform ? actor.TransformEditRotationEuler : actor.SpawnRotationEuler;
        var scale = actor.HasSavedTransform ? actor.TransformEditScale : actor.SpawnScale;
        if (scale == Vector3.Zero)
            scale = Vector3.One;

        return WorldTransform.FromEuler(position, rotation, scale);
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
        => this.QueuePostSpawnAppearance(runtimeId, "manual appearance apply");

    private bool EnqueueNpcAppearanceDirect(string runtimeId, string reason)
    {
        if (this.registry.GetByRuntimeId(runtimeId) == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        this.appearanceApplyQueue.Enqueue(runtimeId, reason);
        this.LastMessage = $"已加入外观应用队列：{runtimeId}";
        return true;
    }

    private bool QueuePostSpawnAppearance(string runtimeId, string reason)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return false;
        }

        this.QueuePostSpawnApply(runtimeId, reason);
        this.LastMessage = $"已加入稳定外观应用队列：order={actor.SortOrder}, runtime={runtimeId}";
        return true;
    }

    public void LogActorAppearanceDiagnostics(string runtimeId)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
        {
            this.LastMessage = $"没有找到 Runtime Actor：{runtimeId}";
            return;
        }

        var npc = this.database.GetNpcById(actor.NpcId);
        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        var preset = npc == null ? "npc=<missing>" : this.appearanceApplyService.DescribeNpcPreset(npc);
        var actorSignature = BuildAppearanceSignature(actor.CharacterObject).Summary;
        var actorEquipment = BuildAppearanceComponentSignature(actor.CharacterObject, EquipmentSignatureTokens);
        var localSignature = BuildAppearanceSignature(localPlayer).Summary;
        var localEquipment = BuildAppearanceComponentSignature(localPlayer, EquipmentSignatureTokens);
        actor.LastAppearancePresetSummary = preset;
        actor.LastAppearanceBeforeSummary = $"{actorSignature}; equipment={actorEquipment}";
        actor.LastLocalPlayerAppearanceSummary = $"{localSignature}; equipment={localEquipment}";
        this.log.Information("[Appearance] Manual diagnostics actor={Actor} order={Order} ptr={Ptr} index={Index} preset={Preset} actor={ActorSummary} actorEquip={ActorEquip} local={LocalSummary} localEquip={LocalEquip}",
            actor.RuntimeId,
            actor.SortOrder,
            actor.Address,
            actor.ObjectIndex,
            preset,
            actorSignature,
            actorEquipment,
            localSignature,
            localEquipment);
        this.LastMessage = $"已打印外观摘要：{actor.DisplayName}";
    }

    public void ForceClearAndReapplyAppearance(string runtimeId)
        => this.QueuePostSpawnApply(runtimeId, "manual ClearAllEquipment + ReapplyFullPreset");

    public void ForceTargetedRedrawAndReapplyAppearance(string runtimeId)
        => this.QueuePostSpawnApply(runtimeId, "manual targeted Penumbra redraw + ReapplyFullPreset", PostSpawnApplyState.RequestTargetedRedraw);

    private void QueuePostSpawnApply(string runtimeId, string reason, PostSpawnApplyState initialState = PostSpawnApplyState.WaitingObjectStable, int cloneRetryCount = 0)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
            return;

        RemoveQueuedPostSpawnApply(runtimeId);
        actor.PostSpawnBehaviorReady = false;
        actor.PostSpawnPipelineState = initialState.ToString();
        actor.PostSpawnPipelineStatus = $"Queued post-spawn pipeline: {reason}";
        this.postSpawnApplyQueue.Enqueue(new PostSpawnApplyPlan(runtimeId, actor.SpawnSequence, actor.SortOrder, reason)
        {
            State = initialState,
            CloneRetryCount = cloneRetryCount,
        });
        this.log.Information("[RealNpcSpawnService] PostSpawn queued. order={Order}, runtime={RuntimeId}, state={State}, reason={Reason}", actor.SortOrder, runtimeId, initialState, reason);
    }

    private void RemoveQueuedPostSpawnApply(string runtimeId)
    {
        if (this.postSpawnApplyQueue.Count == 0)
            return;

        var remaining = this.postSpawnApplyQueue
            .Where(plan => !string.Equals(plan.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        this.postSpawnApplyQueue.Clear();
        foreach (var item in remaining)
            this.postSpawnApplyQueue.Enqueue(item);
    }

    private void UpdatePostSpawnApplyPipeline()
    {
        if (this.activePostSpawnApply == null && this.postSpawnApplyQueue.Count > 0)
            this.activePostSpawnApply = this.postSpawnApplyQueue.Dequeue();

        var plan = this.activePostSpawnApply;
        if (plan == null)
            return;

        var actor = this.registry.GetByRuntimeId(plan.RuntimeId);
        if (actor == null || actor.SpawnSequence != plan.SpawnSequence)
        {
            this.activePostSpawnApply = null;
            return;
        }

        switch (plan.State)
        {
            case PostSpawnApplyState.WaitingObjectStable:
                this.UpdatePostSpawnWaitingObjectStable(actor, plan);
                break;
            case PostSpawnApplyState.ApplyingPenumbra:
                this.ApplyPostSpawnPenumbra(actor, plan);
                break;
            case PostSpawnApplyState.WaitingAfterPenumbra:
                plan.PenumbraWaitTicks++;
                actor.PostSpawnPipelineState = "WaitingAfterPenumbraRedraw";
                actor.PostSpawnPipelineStatus = $"Waiting after Penumbra assignment/redraw tick {plan.PenumbraWaitTicks}/3.";
                if (plan.PenumbraWaitTicks >= 3)
                {
                    this.RestoreTransformExact(actor, "after Penumbra wait");
                    this.VerifyNoUnexpectedTransformChange(actor, "after Penumbra wait");
                    plan.State = PostSpawnApplyState.ResetAppearanceToNpcBase;
                }
                break;
            case PostSpawnApplyState.ResetAppearanceToNpcBase:
                this.ResetAppearanceToNpcBase(actor, plan);
                break;
            case PostSpawnApplyState.ClearEquipmentToEmpty:
                this.ClearActorEquipmentToEmpty(actor, plan);
                break;
            case PostSpawnApplyState.ApplyingAppearance:
                this.brioAssemblyBridge.RefreshActor(actor);
                if (!this.TryValidatePostSpawnActor(actor, out var preApplyValidationReason))
                {
                    plan.ElapsedTicks = 0;
                    plan.StableTicks = 0;
                    actor.PostSpawnPipelineState = "WaitingObjectStable";
                    actor.PostSpawnPipelineStatus = $"Target changed before appearance apply: {preApplyValidationReason}";
                    plan.State = PostSpawnApplyState.WaitingObjectStable;
                    break;
                }

                plan.AppearanceEnqueuedAt = DateTime.Now;
                actor.PostSpawnPipelineState = "ApplyingAppearance";
                actor.PostSpawnPipelineStatus = "Object stable; appearance apply queued.";
                this.appearanceApplyQueue.RemoveJobsForActor(actor.RuntimeId);
                this.EnqueueNpcAppearanceDirect(actor.RuntimeId, $"post-spawn stable appearance apply: {plan.Reason}");
                this.log.Information("[RealNpcSpawnService] Applying appearance after stable object. order={Order}, runtime={RuntimeId}, index={Index}, address={Address}", actor.SortOrder, actor.RuntimeId, actor.ObjectIndex, actor.Address);
                plan.State = PostSpawnApplyState.WaitingAfterAppearance;
                break;
            case PostSpawnApplyState.WaitingAfterAppearance:
                plan.AppearanceWaitTicks++;
                if (this.appearanceApplyService.IsNpcAppearanceApplyPending(actor) && plan.AppearanceWaitTicks < 90)
                {
                    actor.PostSpawnPipelineStatus = $"Waiting Brio/Glamourer appearance task completion tick {plan.AppearanceWaitTicks}/90. {this.appearanceApplyService.HumanoidBrioActorAppearanceLastResult}";
                }
                else if (plan.AppearanceWaitTicks >= 5 &&
                    ((actor.LastAppearanceAppliedAt.HasValue && actor.LastAppearanceAppliedAt.Value >= plan.AppearanceEnqueuedAt) || plan.AppearanceWaitTicks >= 90))
                {
                    this.RestoreTransformExact(actor, "after appearance apply");
                    this.VerifyNoUnexpectedTransformChange(actor, "after appearance apply");
                    plan.State = PostSpawnApplyState.VerifyAppearance;
                }
                else
                    actor.PostSpawnPipelineStatus = $"Waiting appearance/redraw settle tick {plan.AppearanceWaitTicks}/90.";
                break;
            case PostSpawnApplyState.VerifyAppearance:
                this.VerifyPostSpawnAppearance(actor, plan);
                break;
            case PostSpawnApplyState.RequestTargetedRedraw:
                this.RequestPostSpawnTargetedRedraw(actor, plan);
                break;
            case PostSpawnApplyState.WaitingAfterTargetedRedraw:
                this.WaitAfterPostSpawnTargetedRedraw(actor, plan);
                break;
            case PostSpawnApplyState.EnableDraw:
                this.EnablePostSpawnActorDraw(actor, plan);
                break;
            case PostSpawnApplyState.ApplyingBehavior:
                this.ApplyPostSpawnBehavior(actor);
                this.VerifyNoUnexpectedTransformChange(actor, "after behavior apply");
                actor.PostSpawnBehaviorReady = true;
                actor.PostSpawnPipelineState = "Ready";
                actor.PostSpawnPipelineStatus = "Post-spawn appearance and behavior pipeline complete.";
                this.log.Information("[RealNpcSpawnService] PostSpawn ready. order={Order}, runtime={RuntimeId}", actor.SortOrder, actor.RuntimeId);
                this.activePostSpawnApply = null;
                break;
        }
    }

    private void UpdatePostSpawnWaitingObjectStable(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        plan.ElapsedTicks++;
        this.brioAssemblyBridge.RefreshActor(actor);
        if (!this.TryValidatePostSpawnActor(actor, out var validationReason))
        {
            actor.PostSpawnPipelineState = "WaitingObjectStable";
            actor.PostSpawnPipelineStatus = $"{validationReason}; retry tick={plan.ElapsedTicks}/90";
            if (plan.ElapsedTicks >= 90)
            {
                actor.PostSpawnPipelineState = "Failed";
                actor.PostSpawnPipelineStatus = $"Post-spawn target validation timeout: {validationReason}";
                actor.LastAppearanceError = actor.PostSpawnPipelineStatus;
                actor.PostSpawnBehaviorReady = true;
                this.log.Warning("[RealNpcSpawnService] PostSpawn validation failed. order={Order}, runtime={RuntimeId}, reason={Reason}", actor.SortOrder, actor.RuntimeId, validationReason);
                this.activePostSpawnApply = null;
            }

            return;
        }

        var drawObjectAddress = ReadActorDrawObjectAddress(actor);
        if (!string.Equals(plan.LastAddress, actor.Address, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.LastDrawObjectAddress, drawObjectAddress, StringComparison.OrdinalIgnoreCase))
        {
            plan.LastAddress = actor.Address;
            plan.LastDrawObjectAddress = drawObjectAddress;
            plan.StableTicks = 0;
            actor.PostSpawnPipelineStatus = "DrawObject changed; waiting for stable consecutive ticks.";
            return;
        }

        plan.StableTicks++;
        actor.PostSpawnPipelineStatus = $"Object stable tick {plan.StableTicks}/3. index={actor.ObjectIndex}, address={actor.Address}, draw={drawObjectAddress}";
        if (plan.StableTicks >= 3)
        {
            this.RestoreTransformExact(actor, "after create stable");
            this.VerifyNoUnexpectedTransformChange(actor, "after create stable");
            plan.State = PostSpawnApplyState.ApplyingPenumbra;
        }
    }

    private void ApplyPostSpawnPenumbra(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        var npc = this.database.GetNpcById(actor.NpcId);
        if (npc == null)
        {
            actor.PostSpawnPipelineState = "Failed";
            actor.PostSpawnPipelineStatus = $"NPC config missing before Penumbra stage: {actor.NpcId}";
            actor.LastAppearanceError = actor.PostSpawnPipelineStatus;
            actor.PostSpawnBehaviorReady = true;
            this.activePostSpawnApply = null;
            return;
        }

        plan.BeforeApplySignature = BuildAppearanceSignature(actor.CharacterObject).Summary;
        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        plan.LocalPlayerSignature = BuildAppearanceSignature(localPlayer).Summary;
        plan.LocalPlayerEquipmentSignature = BuildAppearanceComponentSignature(localPlayer, EquipmentSignatureTokens);
        plan.LocalPlayerCustomizeSignature = BuildAppearanceComponentSignature(localPlayer, CustomizeSignatureTokens);
        plan.PresetSignature = this.appearanceApplyService.DescribeNpcPreset(npc);
        actor.LastAppearanceBeforeSummary = plan.BeforeApplySignature;
        actor.LastAppearancePresetSummary = plan.PresetSignature;
        actor.LastLocalPlayerAppearanceSummary = $"{plan.LocalPlayerSignature}; equipment={plan.LocalPlayerEquipmentSignature}; customize={plan.LocalPlayerCustomizeSignature}";
        actor.PostSpawnPipelineState = "ApplyingPenumbraCollection";
        this.RestoreTransformExact(actor, "before Penumbra collection");
        if (!this.penumbraIpc.ApplyCollection(npc, actor, out var penumbraReason))
        {
            actor.LastPenumbraCollectionError = penumbraReason;
            this.log.Warning("[ActorAppearance] Penumbra collection stage failed but preset apply will continue. order={Order}, actor={Actor}, reason={Reason}", actor.SortOrder, actor.RuntimeId, penumbraReason);
        }

        this.log.Information("[ActorAppearance] Validate target actor={Actor} order={Order} ptr={Ptr} index={Index} preset={Preset} local={Local} localEquip={LocalEquip} before={Before}",
            actor.RuntimeId,
            actor.SortOrder,
            actor.Address,
            actor.ObjectIndex,
            plan.PresetSignature,
            plan.LocalPlayerSignature,
            plan.LocalPlayerEquipmentSignature,
            plan.BeforeApplySignature);
        actor.PostSpawnPipelineStatus = $"Penumbra stage complete: {penumbraReason}";
        plan.State = PostSpawnApplyState.WaitingAfterPenumbra;
    }

    private void ResetAppearanceToNpcBase(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        this.brioAssemblyBridge.RefreshActor(actor);
        if (!this.TryValidatePostSpawnActor(actor, out var validationReason))
        {
            plan.ElapsedTicks = 0;
            plan.StableTicks = 0;
            actor.PostSpawnPipelineState = "WaitingObjectStable";
            actor.PostSpawnPipelineStatus = $"Target changed after Penumbra/redraw: {validationReason}";
            plan.State = PostSpawnApplyState.WaitingObjectStable;
            return;
        }

        var npc = this.database.GetNpcById(actor.NpcId);
        if (string.IsNullOrWhiteSpace(plan.PresetSignature))
            plan.PresetSignature = npc == null
                ? "npc=<missing>"
                : this.appearanceApplyService.DescribeNpcPreset(npc);
        actor.LastAppearancePresetSummary = plan.PresetSignature;
        actor.PostSpawnPipelineState = "ResetAppearanceToNpcBase";
        actor.PostSpawnPipelineStatus = $"Preparing full preset overwrite. Preset={plan.PresetSignature}";
        this.RestoreTransformExact(actor, "before full preset overwrite");
        this.log.Information("[ActorAppearance] ResetAppearanceToNpcBase actor={Actor} order={Order} preset={Preset}", actor.RuntimeId, actor.SortOrder, plan.PresetSignature);
        plan.State = PostSpawnApplyState.ClearEquipmentToEmpty;
    }

    private void ClearActorEquipmentToEmpty(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        this.brioAssemblyBridge.RefreshActor(actor);
        if (!this.TryValidatePostSpawnActor(actor, out var validationReason))
        {
            plan.ElapsedTicks = 0;
            plan.StableTicks = 0;
            actor.PostSpawnPipelineState = "WaitingObjectStable";
            actor.PostSpawnPipelineStatus = $"Target changed before clear-equipment stage: {validationReason}";
            plan.State = PostSpawnApplyState.WaitingObjectStable;
            return;
        }

        var beforeEquipment = BuildAppearanceComponentSignature(actor.CharacterObject, EquipmentSignatureTokens);
        plan.ActorBeforeEquipmentSignature = beforeEquipment;
        this.RestoreTransformExact(actor, "before clear equipment/full apply");
        actor.LastAppearanceClearEquipmentResult = "No separate verified ClearAllEquipment native path is enabled; proceeding with full preset overwrite. Glamourer designs use full flags=7, and GameNpc applies AppearanceImportOptions.All.";
        actor.LastAppearanceBeforeSummary = $"{BuildAppearanceSignature(actor.CharacterObject).Summary}; equipment={beforeEquipment}";
        actor.PostSpawnPipelineState = "ClearAllEquipmentToEmpty";
        actor.PostSpawnPipelineStatus = actor.LastAppearanceClearEquipmentResult;
        this.log.Information("[Appearance] ClearAllEquipmentToEmpty actor={Actor} order={Order} targetPtr={Ptr} index={Index} result={Result} beforeEquip={BeforeEquip} preset={Preset}",
            actor.RuntimeId,
            actor.SortOrder,
            actor.Address,
            actor.ObjectIndex,
            actor.LastAppearanceClearEquipmentResult,
            beforeEquipment,
            plan.PresetSignature);
        plan.State = PostSpawnApplyState.ApplyingAppearance;
    }

    private void VerifyPostSpawnAppearance(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        var afterSignature = BuildAppearanceSignature(actor.CharacterObject);
        var afterEquipmentSignature = BuildAppearanceComponentSignature(actor.CharacterObject, EquipmentSignatureTokens);
        var afterCustomizeSignature = BuildAppearanceComponentSignature(actor.CharacterObject, CustomizeSignatureTokens);
        actor.LastAppearanceAfterSummary = afterSignature.Summary;
        var npc = this.database.GetNpcById(actor.NpcId);
        var allowsCurrentPlayerAppearance = npc?.Appearance.SourceType == CustomNpcAppearanceSourceType.CurrentPlayer;
        var applyFailed = !string.IsNullOrWhiteSpace(actor.LastAppearanceError);
        var localPlayerCloneAppearance = !allowsCurrentPlayerAppearance &&
            IsSameUsableSignature(afterSignature.Summary, plan.LocalPlayerSignature);
        var playerGearResidual = !allowsCurrentPlayerAppearance &&
            IsSameUsableSignature(afterEquipmentSignature, plan.LocalPlayerEquipmentSignature);
        var playerCustomizeResidual = !allowsCurrentPlayerAppearance &&
            IsSameUsableSignature(afterCustomizeSignature, plan.LocalPlayerCustomizeSignature);
        var suspicious = localPlayerCloneAppearance || playerGearResidual || playerCustomizeResidual;
        IReadOnlyList<string> residualSlots = playerGearResidual
            ? GetResidualPlayerEquipmentSlots(actor.CharacterObject, this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState))
            : Array.Empty<string>();
        actor.LastAppearanceRetryCount = plan.AppearanceRetryCount;
        actor.LastAppearanceRedrawFallbackCount = plan.RedrawFallbackCount;
        actor.LastAppearanceResidualSlots = string.Join(",", residualSlots);
        actor.LastAppearanceVerificationState = applyFailed
            ? AppearanceApplyResult.ApplyFailed.ToString()
            : localPlayerCloneAppearance
                ? AppearanceApplyResult.LocalPlayerCloneAppearance.ToString()
                : playerCustomizeResidual
                    ? AppearanceApplyResult.PlayerCustomizeResidual.ToString()
                    : playerGearResidual
                ? AppearanceApplyResult.PlayerGearResidual.ToString()
                : AppearanceApplyResult.Success.ToString();
        actor.LastAppearanceValidationResult = applyFailed
            ? $"Appearance apply reported failure. retry={plan.AppearanceRetryCount}/3; error={actor.LastAppearanceError}; after={afterSignature.Summary}"
            : localPlayerCloneAppearance
                ? $"LocalPlayerCloneAppearance detected. cloneRetry={plan.CloneRetryCount}/{MaxLocalPlayerCloneRetries}; after={afterSignature.Summary}; local={plan.LocalPlayerSignature}; preset={plan.PresetSignature}"
                : playerGearResidual
                    ? $"PlayerGearResidual detected. redrawFallback={plan.RedrawFallbackCount}/2; slots={actor.LastAppearanceResidualSlots}; after={afterSignature.Summary}; afterEquip={afterEquipmentSignature}; local={plan.LocalPlayerSignature}; localEquip={plan.LocalPlayerEquipmentSignature}"
                    : playerCustomizeResidual
                        ? $"PlayerCustomizeResidual detected. retry={plan.AppearanceRetryCount}/3; afterCustomize={afterCustomizeSignature}; localCustomize={plan.LocalPlayerCustomizeSignature}"
                : $"Appearance verification passed. after={afterSignature.Summary}";
        this.log.Information("[Appearance] Verify actor={Actor} order={Order} result={Result} cloneRetry={CloneRetry} applyRetry={Retry} redrawRetry={RedrawRetry} residualSlots={ResidualSlots} preset={Preset} actorEquip={ActorEquip} localEquip={LocalEquip} actorCustomize={ActorCustomize} localCustomize={LocalCustomize} before={Before} local={Local} after={After} error={Error}",
            actor.RuntimeId,
            actor.SortOrder,
            actor.LastAppearanceVerificationState,
            plan.CloneRetryCount,
            plan.AppearanceRetryCount,
            plan.RedrawFallbackCount,
            actor.LastAppearanceResidualSlots,
            plan.PresetSignature,
            afterEquipmentSignature,
            plan.LocalPlayerEquipmentSignature,
            afterCustomizeSignature,
            plan.LocalPlayerCustomizeSignature,
            plan.BeforeApplySignature,
            plan.LocalPlayerSignature,
            afterSignature.Summary,
            actor.LastAppearanceError);

        if (localPlayerCloneAppearance)
        {
            this.DiscardLocalPlayerCloneAndRetry(actor, plan);
            return;
        }

        if (playerGearResidual && plan.RedrawFallbackCount < 2)
        {
            actor.PostSpawnPipelineState = "RequestTargetedRedraw";
            actor.PostSpawnPipelineStatus = actor.LastAppearanceValidationResult;
            plan.State = PostSpawnApplyState.RequestTargetedRedraw;
            return;
        }

        this.log.Information("[ActorAppearance] Verify actor={Actor} order={Order} retry={Retry} applyFailed={ApplyFailed} suspicious={Suspicious} preset={Preset} before={Before} local={Local} after={After} error={Error}",
            actor.RuntimeId,
            actor.SortOrder,
            plan.AppearanceRetryCount,
            applyFailed,
            suspicious,
            plan.PresetSignature,
            plan.BeforeApplySignature,
            plan.LocalPlayerSignature,
            afterSignature.Summary,
            actor.LastAppearanceError);

        if ((applyFailed || suspicious) && plan.AppearanceRetryCount < 3)
        {
            plan.AppearanceRetryCount++;
            plan.AppearanceWaitTicks = 0;
            plan.AppearanceEnqueuedAt = DateTime.MinValue;
            actor.PostSpawnPipelineState = "AppearanceRetry";
            actor.PostSpawnPipelineStatus = actor.LastAppearanceValidationResult;
            plan.State = PostSpawnApplyState.ApplyingAppearance;
            return;
        }

        if (applyFailed || suspicious)
        {
            actor.PostSpawnPipelineState = "Failed";
            actor.PostSpawnPipelineStatus = applyFailed
                ? $"Appearance apply failed after retries: {actor.LastAppearanceError}"
                : playerGearResidual
                    ? "RedrawRetryExceeded: equipment still matches LocalPlayer after targeted redraw fallback."
                    : "AppearanceRetryExceeded: customize/equipment still matches LocalPlayer after full preset reapply.";
            actor.LastAppearanceError = actor.PostSpawnPipelineStatus;
            actor.PostSpawnBehaviorReady = true;
            this.activePostSpawnApply = null;
            return;
        }

        plan.State = PostSpawnApplyState.EnableDraw;
    }

    private void DiscardLocalPlayerCloneAndRetry(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        this.TrackSacrificialCloneActor(actor);
        this.SetPostSpawnQuarantineVisibility(actor, visible: false, "LocalPlayer clone appearance detected; discarding sacrificial actor");
        this.log.Error("[ActorSpawn] discard sacrificial clone ptr={Ptr} index={Index} actor={Actor} order={Order} cloneRetry={Retry}/{MaxRetry} validation={Validation}",
            actor.Address,
            actor.ObjectIndex,
            actor.RuntimeId,
            actor.SortOrder,
            plan.CloneRetryCount,
            MaxLocalPlayerCloneRetries,
            actor.LastAppearanceValidationResult);

        actor.PostSpawnPipelineState = "Failed";
        actor.PostSpawnPipelineStatus = actor.LastAppearanceValidationResult;
        actor.LastAppearanceError = actor.LastAppearanceValidationResult;
        actor.PostSpawnBehaviorReady = true;
        this.activePostSpawnApply = null;
        var npc = this.database.GetNpcById(actor.NpcId);
        if (npc == null)
        {
            this.LastMessage = $"CreateCharacter 返回玩家 clone，但 NPC 配置不存在，无法重试：{actor.NpcId}";
            return;
        }

        if (plan.CloneRetryCount >= MaxLocalPlayerCloneRetries)
        {
            this.LastMessage = $"CreateCharacter 持续返回玩家 clone，已中止：{npc.Name}";
            this.log.Error("[ActorSpawn] clone retry exceeded actor={Actor} npc={NpcId} retry={Retry}", actor.RuntimeId, npc.Id, plan.CloneRetryCount);
            if (string.Equals(this.activeGposeRebuildRuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase))
            {
                this.activeGposeRebuildRuntimeId = string.Empty;
                this.activeGposeRebuildSnapshot = null;
            }

            return;
        }

        var sacrificialDespawned = this.brioAssemblyBridge.TryDespawnActor(actor, out var despawnReason);
        this.log.Information("[ActorSpawn] sacrificial clone despawn result actor={Actor} ptr={Ptr} index={Index} success={Success} reason={Reason}", actor.RuntimeId, actor.Address, actor.ObjectIndex, sacrificialDespawned, despawnReason);
        if (sacrificialDespawned)
            this.UntrackActorIdentity(actor, this.sacrificialClonePointers, this.sacrificialCloneObjectIndexes);
        else
            this.TrackSacrificialCloneActor(actor);
        this.registry.Remove(actor.RuntimeId);
        if (string.Equals(this.CurrentSelectedActorRuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase))
            this.ClearSelectedActorForPlayerLookAt();

        if (string.Equals(this.activeGposeRebuildRuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase) &&
            this.activeGposeRebuildSnapshot != null)
        {
            var retrySnapshot = WithCloneRetry(this.activeGposeRebuildSnapshot, plan.CloneRetryCount + 1);
            this.EnqueueGposeRebuildFront(retrySnapshot);
            this.activeGposeRebuildRuntimeId = string.Empty;
            this.activeGposeRebuildSnapshot = null;
            this.LastMessage = $"GPose rebuild actor hit LocalPlayer clone; retrying same order={retrySnapshot.SortOrder} attempt={retrySnapshot.CloneRetryAttempt}/{MaxLocalPlayerCloneRetries}";
            this.log.Warning("[ActorSpawn] GPose clone retry same actor. order={Order} npc={NpcId} attempt={Attempt}", retrySnapshot.SortOrder, retrySnapshot.Npc.Id, retrySnapshot.CloneRetryAttempt);
            return;
        }

        var shouldUpdateSpawnIntent = this.spawnIntentRegistry.Get(actor.NpcId)?.ShouldBeSpawned == true;
        this.EnqueueActorSpawnFront(new QueuedActorSpawnRequest(
            npc,
            actor.SpawnPosition == Vector3.Zero ? this.GetTemplateSpawnPosition(npc) : actor.SpawnPosition,
            0,
            actor.SortOrder,
            plan.CloneRetryCount + 1,
            UpdateSpawnIntent: shouldUpdateSpawnIntent,
            Source: "clone-discard-retry"));
        this.LastMessage = $"检测到 LocalPlayer clone 外观，已隐藏丢弃并重新创建：{npc.Name} attempt={plan.CloneRetryCount + 1}/{MaxLocalPlayerCloneRetries}";
    }

    private void EnablePostSpawnActorDraw(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        this.brioAssemblyBridge.RefreshActor(actor);
        if (!this.TryValidatePostSpawnActor(actor, out var validationReason))
        {
            actor.PostSpawnPipelineState = "Failed";
            actor.PostSpawnPipelineStatus = $"Refusing to enable draw: target invalid after verified appearance. {validationReason}";
            actor.LastAppearanceError = actor.PostSpawnPipelineStatus;
            actor.PostSpawnBehaviorReady = true;
            this.log.Error("[ActorVisibility] refusing EnableDraw. order={Order}, actor={Actor}, reason={Reason}", actor.SortOrder, actor.RuntimeId, validationReason);
            this.activePostSpawnApply = null;
            return;
        }

        this.RestoreTransformExact(actor, "before EnableDraw");
        this.VerifyNoUnexpectedTransformChange(actor, "before EnableDraw");
        this.SetPostSpawnQuarantineVisibility(actor, visible: true, $"appearance verified order={plan.SortOrder}");
        actor.PostSpawnPipelineState = "EnableDraw";
        actor.PostSpawnPipelineStatus = "Appearance verified; draw enabled.";
        this.log.Information("[ActorVisibility] enable draw only after verify success. order={Order}, actor={Actor}, ptr={Ptr}, index={Index}", actor.SortOrder, actor.RuntimeId, actor.Address, actor.ObjectIndex);
        plan.State = PostSpawnApplyState.ApplyingBehavior;
    }

    private void RequestPostSpawnTargetedRedraw(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        this.brioAssemblyBridge.RefreshActor(actor);
        if (!this.TryValidatePostSpawnActor(actor, out var validationReason))
        {
            plan.ElapsedTicks = 0;
            plan.StableTicks = 0;
            actor.PostSpawnPipelineState = "WaitingObjectStable";
            actor.PostSpawnPipelineStatus = $"Target invalid before redraw fallback: {validationReason}";
            plan.State = PostSpawnApplyState.WaitingObjectStable;
            return;
        }

        if (!this.penumbraIpc.RequestRedrawObject(actor, out var redrawReason))
        {
            actor.PostSpawnPipelineState = "Failed";
            actor.PostSpawnPipelineStatus = $"Targeted redraw fallback failed: {redrawReason}";
            actor.LastAppearanceError = actor.PostSpawnPipelineStatus;
            actor.PostSpawnBehaviorReady = true;
            this.log.Warning("[Appearance] Targeted redraw fallback failed actor={Actor} order={Order} reason={Reason}", actor.RuntimeId, actor.SortOrder, redrawReason);
            this.activePostSpawnApply = null;
            return;
        }

        plan.RedrawFallbackCount++;
        plan.RedrawWaitTicks = 0;
        plan.LastDrawObjectAddress = ReadActorDrawObjectAddress(actor);
        actor.LastAppearanceRedrawFallbackCount = plan.RedrawFallbackCount;
        actor.PostSpawnPipelineState = "WaitingAfterTargetedRedraw";
        actor.PostSpawnPipelineStatus = $"检测到玩家装备残留，已自动 targeted redraw：{redrawReason}";
        this.log.Information("[Appearance] Request targeted Penumbra redraw actor={Actor} order={Order} index={Index} retry={Retry} reason={Reason}", actor.RuntimeId, actor.SortOrder, actor.ObjectIndex, plan.RedrawFallbackCount, redrawReason);
        plan.State = PostSpawnApplyState.WaitingAfterTargetedRedraw;
    }

    private void WaitAfterPostSpawnTargetedRedraw(RuntimeActorInstance actor, PostSpawnApplyPlan plan)
    {
        plan.RedrawWaitTicks++;
        this.brioAssemblyBridge.RefreshActor(actor);
        actor.PostSpawnPipelineState = "WaitingAfterTargetedRedraw";
        actor.PostSpawnPipelineStatus = $"等待 targeted redraw 后 DrawObject 稳定：{plan.RedrawWaitTicks}/5";
        if (plan.RedrawWaitTicks < 5)
            return;

        this.log.Information("[Appearance] Redraw wait complete actor={Actor} order={Order} ticks={Ticks} oldDraw={OldDraw} newDraw={NewDraw}", actor.RuntimeId, actor.SortOrder, plan.RedrawWaitTicks, plan.LastDrawObjectAddress, ReadActorDrawObjectAddress(actor));
        this.RestoreTransformExact(actor, "after targeted redraw");
        this.VerifyNoUnexpectedTransformChange(actor, "after targeted redraw");
        plan.ElapsedTicks = 0;
        plan.StableTicks = 0;
        plan.PenumbraWaitTicks = 0;
        plan.AppearanceWaitTicks = 0;
        plan.AppearanceEnqueuedAt = DateTime.MinValue;
        plan.LastAddress = string.Empty;
        plan.LastDrawObjectAddress = string.Empty;
        plan.State = PostSpawnApplyState.WaitingObjectStable;
        actor.PostSpawnPipelineState = "WaitingObjectStable";
        actor.PostSpawnPipelineStatus = "Targeted redraw complete; re-resolving actor and reapplying full NPC preset.";
    }

    private void SetPostSpawnQuarantineVisibility(RuntimeActorInstance actor, bool visible, string reason)
    {
        if (actor.CharacterObject == null || !actor.IsValid)
        {
            this.log.Warning("[ActorVisibility] skip quarantine visibility; actor invalid. actor={Actor}, visible={Visible}, reason={Reason}", actor.RuntimeId, visible, reason);
            return;
        }

        if (this.IsLocalPlayerActor(actor))
        {
            this.log.Error("[ActorVisibility] refused visibility write because actor resolved to LocalPlayer. actor={Actor}, visible={Visible}, ptr={Ptr}, index={Index}, reason={Reason}", actor.RuntimeId, visible, actor.Address, actor.ObjectIndex, reason);
            actor.LastAppearanceError = "Visibility write refused: target resolved to LocalPlayer.";
            return;
        }

        if (!this.animationService.SetSequenceVisibility(actor, visible, out var visibilityReason))
        {
            actor.PostSpawnPipelineStatus = $"Quarantine visibility {(visible ? "show" : "hide")} skipped/failed: {visibilityReason}";
            this.log.Warning("[ActorVisibility] quarantine visibility failed. actor={Actor}, visible={Visible}, reason={Reason}, result={Result}", actor.RuntimeId, visible, reason, visibilityReason);
            return;
        }

        actor.PostSpawnPipelineStatus = visible
            ? $"Post-spawn quarantine released: {reason}"
            : $"Post-spawn quarantine hidden: {reason}";
        this.log.Information("[ActorVisibility] quarantine visibility set. actor={Actor}, visible={Visible}, reason={Reason}, result={Result}", actor.RuntimeId, visible, reason, visibilityReason);
    }

    private static bool IsResidualLocalPlayerAppearance(string localPlayerSignature, string localPlayerEquipmentSignature, AppearanceSignature afterSignature, string afterEquipmentSignature)
    {
        if (!afterSignature.HasAnySignal || string.IsNullOrWhiteSpace(localPlayerSignature) || localPlayerSignature.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(afterSignature.Summary, localPlayerSignature, StringComparison.Ordinal))
            return true;

        return !string.IsNullOrWhiteSpace(afterEquipmentSignature) &&
               !afterEquipmentSignature.Contains("unavailable", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(afterEquipmentSignature, localPlayerEquipmentSignature, StringComparison.Ordinal);
    }

    private static bool IsSameUsableSignature(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right) ||
            left.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            right.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetResidualPlayerEquipmentSlots(object? actorSource, object? localPlayerSource)
    {
        var actorMap = BuildAppearanceComponentMap(actorSource, EquipmentSignatureTokens);
        var localMap = BuildAppearanceComponentMap(localPlayerSource, EquipmentSignatureTokens);
        if (actorMap.Count == 0 || localMap.Count == 0)
            return ["equipment-summary"];

        var matches = new List<string>();
        foreach (var (name, actorValue) in actorMap)
        {
            if (!localMap.TryGetValue(name, out var localValue))
                continue;

            if (string.Equals(actorValue, localValue, StringComparison.Ordinal))
                matches.Add(NormalizeEquipmentSlotName(name));
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches.Count == 0 ? ["equipment-summary"] : matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> BuildAppearanceComponentMap(object? source, IReadOnlyList<string> tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
            return result;

        try
        {
            var type = source.GetType();
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field) ||
                    !tokens.Any(token => member.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    continue;

                object? value = null;
                try
                {
                    value = member switch
                    {
                        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(source),
                        FieldInfo field => field.GetValue(source),
                        _ => null,
                    };
                }
                catch
                {
                    continue;
                }

                result[member.Name] = FormatSignatureValue(value);
            }
        }
        catch
        {
        }

        return result;
    }

    private static string NormalizeEquipmentSlotName(string memberName)
    {
        foreach (var (token, slot) in new[]
                 {
                     ("Head", "head"),
                     ("Body", "body"),
                     ("Hand", "hands"),
                     ("Arm", "hands"),
                     ("Leg", "legs"),
                     ("Foot", "feet"),
                     ("Feet", "feet"),
                     ("Ear", "ears"),
                     ("Neck", "neck"),
                     ("Wrist", "wrists"),
                     ("Ring", "rings"),
                     ("MainHand", "mainhand"),
                     ("OffHand", "offhand"),
                     ("Weapon", "weapon"),
                     ("Equip", "equipment"),
                     ("DrawData", "drawdata"),
                 })
        {
            if (memberName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return slot;
        }

        return memberName;
    }

    private bool TryValidatePostSpawnActor(RuntimeActorInstance actor, out string reason)
    {
        var resolved = this.ResolveActorTarget(actor);
        reason = resolved.Success ? resolved.ToString() : resolved.FailureReason;
        if (!resolved.Success)
        {
            this.log.Information("[ActorResolve] candidate runtime={RuntimeId} order={Order} ptr={Ptr} index={Index} draw={Draw} localPtr={LocalPtr} localIndex={LocalIndex} success=false reason={Reason}",
                actor.RuntimeId,
                actor.SortOrder,
                actor.Address,
                actor.ObjectIndex,
                resolved.DrawObjectPtr == 0 ? "0x0" : $"0x{resolved.DrawObjectPtr:X}",
                resolved.LocalPlayerPtr == 0 ? "0x0" : $"0x{resolved.LocalPlayerPtr:X}",
                resolved.LocalPlayerIndex,
                resolved.FailureReason);
            return false;
        }

        this.log.Information("[ActorResolve] candidate runtime={RuntimeId} order={Order} ptr=0x{Ptr:X} index={Index} draw=0x{Draw:X} success=true",
            actor.RuntimeId,
            actor.SortOrder,
            resolved.NativePtr,
            resolved.GameObjectIndex,
            resolved.DrawObjectPtr);
        return true;
    }

    private ActorTargetResolveResult ResolveActorTarget(RuntimeActorInstance actor)
    {
        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        var localPlayerIndex = ReadTargetObjectIndex(localPlayer);
        TryParseAddress(ReadMember(localPlayer, "Address"), out var localPlayerPtr);

        if (!actor.IsValid || actor.CharacterObject == null)
            return ActorTargetResolveResult.Fail("actor invalid or CharacterObject=null", localPlayerPtr, localPlayerIndex);

        if (!int.TryParse(actor.ObjectIndex, out var objectIndex) || objectIndex < 0)
            return ActorTargetResolveResult.Fail($"invalid ObjectIndex={actor.ObjectIndex}", localPlayerPtr, localPlayerIndex);

        if (TryParseAddress(actor.Address, out var actorPtr) && actorPtr != 0 && localPlayerPtr != 0 && actorPtr == localPlayerPtr)
            return ActorTargetResolveResult.Fail("NativePtr equals LocalPlayer.NativePtr; refusing fallback target", localPlayerPtr, localPlayerIndex);

        if (!string.IsNullOrWhiteSpace(localPlayerIndex) && ObjectIndexMatches(localPlayerIndex, actor.ObjectIndex))
            return ActorTargetResolveResult.Fail("GameObjectIndex equals LocalPlayer.ObjectIndex; refusing fallback target", localPlayerPtr, localPlayerIndex);

        if (ReferenceEquals(localPlayer, actor.CharacterObject))
            return ActorTargetResolveResult.Fail("CharacterObject reference equals LocalPlayer; refusing fallback target", localPlayerPtr, localPlayerIndex);

        if (actorPtr == 0)
            return ActorTargetResolveResult.Fail($"invalid Address={actor.Address}", localPlayerPtr, localPlayerIndex);

        if (this.warmupActorPointers.Contains(actorPtr) || this.warmupActorObjectIndexes.Contains(objectIndex))
            return ActorTargetResolveResult.Fail("target is hidden spawn warm-up actor; refusing formal binding", localPlayerPtr, localPlayerIndex);

        if (this.sacrificialClonePointers.Contains(actorPtr) || this.sacrificialCloneObjectIndexes.Contains(objectIndex))
            return ActorTargetResolveResult.Fail("target is discarded LocalPlayer clone actor; refusing formal binding", localPlayerPtr, localPlayerIndex);

        var drawObjectAddress = ReadActorDrawObjectAddress(actor);
        if (!TryParseAddress(drawObjectAddress, out var drawObjectPtr) || drawObjectPtr == 0)
            return ActorTargetResolveResult.Fail($"DrawObject not ready: {drawObjectAddress}", localPlayerPtr, localPlayerIndex);

        return new ActorTargetResolveResult(true, actorPtr, objectIndex, drawObjectPtr, localPlayerPtr, localPlayerIndex, string.Empty);
    }

    private bool IsLocalPlayerActor(RuntimeActorInstance actor)
    {
        var localPlayer = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        if (localPlayer == null)
            return false;

        if (ReferenceEquals(localPlayer, actor.CharacterObject))
            return true;

        var localAddress = ReadMember(localPlayer, "Address");
        if (!string.IsNullOrWhiteSpace(localAddress) &&
            string.Equals(localAddress, actor.Address, StringComparison.OrdinalIgnoreCase))
            return true;

        var localObjectIndex = ReadTargetObjectIndex(localPlayer);
        if (!string.IsNullOrWhiteSpace(localObjectIndex) &&
            ObjectIndexMatches(localObjectIndex, actor.ObjectIndex))
            return true;

        return TryParseAddress(localAddress, out var localPtr) &&
               TryParseAddress(actor.Address, out var actorPtr) &&
               localPtr != 0 &&
               localPtr == actorPtr;
    }

    private static unsafe string ReadActorDrawObjectAddress(RuntimeActorInstance actor)
    {
        if (!TryParseAddress(actor.Address, out var address) || address == 0)
            return "unavailable";

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
            return native->GameObject.DrawObject == null
                ? "0x0"
                : $"0x{(nint)native->GameObject.DrawObject:X}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static bool TryParseAddress(string? rawAddress, out nint address)
    {
        address = 0;
        var raw = rawAddress?.Trim() ?? string.Empty;
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

    private static AppearanceSignature BuildAppearanceSignature(object? source)
    {
        if (source == null)
            return new AppearanceSignature("unavailable:null", false);

        try
        {
            var type = source.GetType();
            var parts = new List<string>();
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field))
                    continue;

                var name = member.Name;
                if (!LooksLikeAppearanceMember(name))
                    continue;

                object? value = null;
                try
                {
                    value = member switch
                    {
                        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(source),
                        FieldInfo field => field.GetValue(source),
                        _ => null,
                    };
                }
                catch
                {
                    continue;
                }

                parts.Add($"{name}={FormatSignatureValue(value)}");
            }

            parts.Sort(StringComparer.Ordinal);
            if (parts.Count == 0)
                return new AppearanceSignature($"type={type.FullName}; appearanceMembers=unavailable", false);

            var raw = string.Join(";", parts);
            return new AppearanceSignature($"type={type.Name}; hash={StableHash(raw)}; {raw}", true);
        }
        catch (Exception ex)
        {
            return new AppearanceSignature($"unavailable:{ex.Message}", false);
        }
    }

    private static readonly string[] EquipmentSignatureTokens = ["Equip", "Weapon", "MainHand", "OffHand", "DrawData", "Armor", "Stain", "Dye"];

    private static readonly string[] CustomizeSignatureTokens = ["Customize", "Race", "Gender", "Sex", "Tribe", "Face", "Hair", "Skin", "Eye", "ModelChara"];

    private static string BuildAppearanceComponentSignature(object? source, IReadOnlyList<string> tokens)
    {
        if (source == null)
            return "unavailable:null";

        try
        {
            var type = source.GetType();
            var parts = new List<string>();
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field) ||
                    !tokens.Any(token => member.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    continue;

                object? value = null;
                try
                {
                    value = member switch
                    {
                        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(source),
                        FieldInfo field => field.GetValue(source),
                        _ => null,
                    };
                }
                catch
                {
                    continue;
                }

                parts.Add($"{member.Name}={FormatSignatureValue(value)}");
            }

            parts.Sort(StringComparer.Ordinal);
            return parts.Count == 0
                ? $"type={type.FullName}; component=unavailable"
                : $"type={type.Name}; hash={StableHash(string.Join(";", parts))}; {string.Join(";", parts)}";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.Message}";
        }
    }

    private static bool LooksLikeAppearanceMember(string name)
    {
        foreach (var token in new[] { "Customize", "Race", "Gender", "Sex", "Tribe", "Equip", "Weapon", "MainHand", "OffHand", "DrawData", "Model", "Armor", "Stain", "Dye" })
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FormatSignatureValue(object? value)
    {
        if (value == null)
            return "null";

        if (value is string text)
            return text;

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? "null");
                if (items.Count >= 32)
                    break;
            }

            return "[" + string.Join(",", items) + "]";
        }

        return value.ToString() ?? value.GetType().Name;
    }

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

    private void ApplyPostSpawnBehavior(RuntimeActorInstance actor)
    {
        this.RestoreTransformExact(actor, "Before post-spawn behavior");
        if (actor.AnimationRigMode == ActorAnimationRigMode.Override && actor.AnimationRigPreset != ActorAnimationRigPreset.Current)
        {
            actor.AnimationRigStatus = "Rig probe skipped during post-spawn. Actor creation/appearance pipeline is isolated; apply rig manually after Ready.";
            this.log.Information("[AnimationRig] skipped automatic post-spawn rig probe actor={Actor} order={Order}", actor.RuntimeId, actor.SortOrder);
        }

        this.actionSequenceService.Reset(actor);
        if (!actor.EnableActionSequence && actor.CurrentAnimationId > 0)
            this.animationService.PlayTransientTimeline(actor, actor.CurrentAnimationId, out _);
        this.VerifyNoUnexpectedTransformChange(actor, "After post-spawn behavior");
    }

    public void ApplyNpcAppearanceForNpc(string npcId)
        => this.RunSafely("对 NPC 的全部 Actor 应用外观", () =>
        {
            var count = 0;
            foreach (var actor in this.registry.GetByNpcId(npcId))
            {
                this.QueuePostSpawnApply(actor.RuntimeId, $"manual NPC appearance apply: {npcId}");
                count++;
            }

            this.LastMessage = $"已加入稳定外观应用队列：NPC={npcId}, actorCount={count}";
        });

    public void ApplyAllNpcAppearances()
        => this.RunSafely("对全部 Actor 应用外观", () =>
        {
            var count = 0;
            foreach (var actor in this.registry.GetAll())
            {
                this.QueuePostSpawnApply(actor.RuntimeId, "manual all actor appearance apply");
                count++;
            }

            this.LastMessage = $"已加入稳定外观应用队列：全部 Actor={count}";
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
        this.VerifyNoUnexpectedTransformChange(instance, "After PlayAnimation");
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
        this.VerifyNoUnexpectedTransformChange(instance, "After StopAnimation");
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

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationRigStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        var success = this.animationRigService.ApplyAnimationRigOverride(instance, out var reason);
        this.VerifyNoUnexpectedTransformChange(instance, "After ApplyAnimationRig");
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

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationRigStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        var success = this.animationRigService.RestoreAnimationRig(instance, out var reason);
        this.VerifyNoUnexpectedTransformChange(instance, "After RestoreAnimationRig");
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

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationRigStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        var success = this.animationRigService.ReapplyCurrentAnimationWithRig(instance, out var reason);
        this.VerifyNoUnexpectedTransformChange(instance, "After ReapplyCurrentAnimation");
        this.LastMessage = reason;
        return success;
    }

    public void DumpActorAnimationRigDebugReport(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return;
        }

        this.animationRigService.DumpLastDebugReport(instance);
        this.LastMessage = $"AnimationRig debug report dumped to log for actor {runtimeId[..Math.Min(8, runtimeId.Length)]}.";
    }

    public void DumpActorAnimationPathBeforeExternalChange(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return;
        }

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationPathResolverStatus = $"RigProbe skipped: {blockReason}";
            return;
        }

        this.animationRigService.DumpAnimationPathBeforeExternalChange(instance);
        this.LastMessage = instance.AnimationPathResolverStatus;
    }

    public void DumpActorAnimationPathAfterExternalChange(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return;
        }

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationPathResolverStatus = $"RigProbe skipped: {blockReason}";
            return;
        }

        this.animationRigService.DumpAnimationPathAfterExternalChange(instance);
        this.LastMessage = instance.AnimationPathResolverStatus;
    }

    public bool CompareActorAnimationPathExternalDumps(string runtimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        if (instance == null)
        {
            this.LastMessage = $"Runtime Actor not found: {runtimeId}";
            return false;
        }

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationPathResolverStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        var success = this.animationRigService.CompareExternalAnimationPathDumps(instance, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool CompareActorAnimationPathWithActor(string runtimeId, string otherRuntimeId)
    {
        var instance = this.registry.GetByRuntimeId(runtimeId);
        var other = this.registry.GetByRuntimeId(otherRuntimeId);
        if (instance == null || other == null)
        {
            this.LastMessage = "Compare failed: one of the actors was not found.";
            return false;
        }

        if (string.Equals(instance.RuntimeId, other.RuntimeId, StringComparison.OrdinalIgnoreCase))
        {
            this.LastMessage = "Compare failed: choose a different actor.";
            return false;
        }

        if (!this.TryValidateRigProbeAllowed(instance, out var blockReason))
        {
            this.LastMessage = blockReason;
            instance.AnimationPathResolverStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        if (!this.TryValidateRigProbeAllowed(other, out blockReason))
        {
            this.LastMessage = blockReason;
            other.AnimationPathResolverStatus = $"RigProbe skipped: {blockReason}";
            return false;
        }

        var success = this.animationRigService.CompareAnimationPathWithActor(instance, other, out var reason);
        this.VerifyNoUnexpectedTransformChange(instance, "After AnimationPath compare A");
        this.VerifyNoUnexpectedTransformChange(other, "After AnimationPath compare B");
        this.LastMessage = reason;
        return success;
    }

    private bool TryValidateRigProbeAllowed(RuntimeActorInstance instance, out string reason)
    {
        if (this.spawnWarmupInProgress)
        {
            reason = "Actor not ready; rig probe skipped because spawn prewarm is running.";
            return false;
        }

        if (this.pendingActorSpawns.Count > 0 ||
            this.activePostSpawnApply != null ||
            this.postSpawnApplyQueue.Count > 0 ||
            this.pendingGposeRebuilds.Count > 0 ||
            this.activeGposeRebuildSnapshot != null ||
            !string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId))
        {
            reason = "Actor not ready; rig probe skipped because formal spawn/rebuild/appearance queue is active.";
            return false;
        }

        if (!instance.IsValid || instance.CharacterObject == null)
        {
            reason = "Actor not ready; rig probe skipped because actor is invalid or missing native object.";
            return false;
        }

        if (!instance.PostSpawnBehaviorReady ||
            !string.Equals(instance.PostSpawnPipelineState, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Actor not ready; rig probe skipped because post-spawn state is {instance.PostSpawnPipelineState}.";
            return false;
        }

        if (!this.TryValidatePostSpawnActor(instance, out var validationReason))
        {
            reason = $"Actor not ready; rig probe skipped because target validation failed: {validationReason}";
            return false;
        }

        reason = string.Empty;
        return true;
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
        if (this.validityMonitorService.CurrentIsGposing && !this.validityMonitorService.PreviousFrameIsGposing)
            this.ResetSpawnWarmup("GPose enter");

        this.UpdateSpawnWarmupLifecycle();
        this.UpdateAutomaticSpawnPrewarm();
        this.UpdatePostSpawnApplyPipeline();
        this.UpdateGposeRebuildQueue();
        this.UpdateQueuedActorSpawns();
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
        this.ResetSpawnWarmup("TerritoryChanged");
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
        this.ResetSpawnWarmup("GPoseExit");
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

        this.pendingGposeRebuilds.Clear();
        foreach (var snapshot in rebuildSnapshots
                     .OrderBy(snapshot => snapshot.SortOrder)
                     .ThenBy(snapshot => snapshot.OriginalSpawnSequence))
        {
            this.pendingGposeRebuilds.Enqueue(snapshot);
            this.log.Information("[RealNpcSpawnService] GPoseRebuild plan: order={Order}, oldRuntime={OldRuntimeId}, npc={NpcId}/{Name}", snapshot.SortOrder, snapshot.OldRuntimeId, snapshot.Npc.Id, snapshot.Npc.Name);
        }

        this.activeGposeRebuildRuntimeId = string.Empty;
        this.gposeRebuildTotalCount = this.pendingGposeRebuilds.Count;
        this.gposeRebuildSuccessCount = 0;
        this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count;
        this.LastGposeRebuildResult = $"GPose 退出后已生成串行重建计划：旧 Actor {oldActors.Count} 个，待重建 {this.gposeRebuildTotalCount} 个。";
        this.LastMessage = this.LastGposeRebuildResult;
    }

    private GposeActorRebuildSnapshot? CreateGposeRebuildSnapshot(RuntimeActorInstance actor)
    {
        var npc = this.database.GetNpcById(actor.NpcId);
        if (npc == null)
            return null;

        this.SnapshotFormalActorTransform(actor, "GPoseExit before rebuild");
        return new GposeActorRebuildSnapshot
        {
            OldRuntimeId = actor.RuntimeId,
            Npc = npc,
            SortOrder = this.GetNpcSortOrder(npc.Id),
            OriginalSpawnSequence = actor.SpawnSequence,
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

    private void SnapshotFormalActorTransform(RuntimeActorInstance actor, string reason)
    {
        if (!actor.IsValid || actor.CharacterObject == null)
        {
            this.log.Warning("[ActorTransform] SnapshotWorld skipped actor={Actor} reason={Reason} invalid; preserving config worldPos={Position} worldEuler={Rotation} worldScale={Scale}",
                actor.RuntimeId,
                reason,
                actor.TransformEditPosition,
                actor.TransformEditRotationEuler,
                actor.TransformEditScale);
            return;
        }

        var previous = this.GetAuthoritativeTransform(actor);
        if (!this.brioCapabilityBridge.TryReadModelTransform(actor, out var readReason))
        {
            actor.TransformEditPosition = previous.Position;
            actor.TransformEditRotationEuler = previous.RotationEuler;
            actor.TransformEditScale = previous.Scale;
            actor.SpawnPosition = previous.Position;
            actor.SpawnRotationEuler = previous.RotationEuler;
            actor.SpawnScale = previous.Scale;
            this.log.Warning("[ActorTransform] SnapshotWorld read failed actor={Actor} reason={Reason} readReason={ReadReason}; preserving expected worldPos={Position} worldEuler={Rotation} worldScale={Scale}",
                actor.RuntimeId,
                reason,
                readReason,
                previous.Position,
                previous.RotationEuler,
                previous.Scale);
            return;
        }

        actor.HasSavedTransform = true;
        actor.SpawnPosition = actor.LastKnownPosition;
        actor.SpawnRotationEuler = actor.LastKnownRotationEuler;
        actor.SpawnScale = actor.LastKnownScale == Vector3.Zero ? Vector3.One : actor.LastKnownScale;
        actor.TransformEditPosition = actor.SpawnPosition;
        actor.TransformEditRotationEuler = actor.SpawnRotationEuler;
        actor.TransformEditScale = actor.SpawnScale;
        this.log.Information("[ActorTransform] SnapshotWorld before rebuild actor={Actor} reason={Reason} worldPos={Position} worldEuler={Rotation} worldScale={Scale}",
            actor.RuntimeId,
            reason,
            actor.TransformEditPosition,
            actor.TransformEditRotationEuler,
            actor.TransformEditScale);
    }

    private static GposeActorRebuildSnapshot WithCloneRetry(GposeActorRebuildSnapshot snapshot, int cloneRetryAttempt)
        => new()
        {
            OldRuntimeId = snapshot.OldRuntimeId,
            Npc = snapshot.Npc,
            SortOrder = snapshot.SortOrder,
            OriginalSpawnSequence = snapshot.OriginalSpawnSequence,
            Position = snapshot.Position,
            RotationEuler = snapshot.RotationEuler,
            Scale = snapshot.Scale,
            DefaultAnimationId = snapshot.DefaultAnimationId,
            CurrentAnimationId = snapshot.CurrentAnimationId,
            AnimationEnabled = snapshot.AnimationEnabled,
            EnableActionSequence = snapshot.EnableActionSequence,
            ActionSequenceLoop = snapshot.ActionSequenceLoop,
            ActionSequenceLoopDelay = snapshot.ActionSequenceLoopDelay,
            ActionSequence = snapshot.ActionSequence.Select(CloneActionSequenceStep).ToList(),
            LookAtPlayerEnabled = snapshot.LookAtPlayerEnabled,
            LookAtRadius = snapshot.LookAtRadius,
            PenumbraMode = snapshot.PenumbraMode,
            PenumbraCollectionId = snapshot.PenumbraCollectionId,
            PenumbraCollectionNameCache = snapshot.PenumbraCollectionNameCache,
            AnimationRigMode = snapshot.AnimationRigMode,
            AnimationRigPreset = snapshot.AnimationRigPreset,
            CustomRigRace = snapshot.CustomRigRace,
            CustomRigSex = snapshot.CustomRigSex,
            CustomRigTribe = snapshot.CustomRigTribe,
            CloneRetryAttempt = cloneRetryAttempt,
        };

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
        actor.VisibilityRuntimeState = ActorVisibilityRuntimeState.SequenceHidden;
        actor.LookAtPausedByActionSequence = false;
        actor.SpawnPosition = snapshot.Position;
        actor.SpawnRotationEuler = snapshot.RotationEuler;
        actor.SpawnScale = snapshot.Scale;
        actor.TransformEditPosition = snapshot.Position;
        actor.TransformEditRotationEuler = snapshot.RotationEuler;
        actor.TransformEditScale = snapshot.Scale;
        actor.HasSavedTransform = true;

        this.RestoreTransformExact(actor, $"GPose rebuild order={snapshot.SortOrder}");
        this.QueuePostSpawnApply(actor.RuntimeId, $"GPose rebuild order={snapshot.SortOrder}", PostSpawnApplyState.WaitingObjectStable, snapshot.CloneRetryAttempt);
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

    private void UpdateGposeRebuildQueue()
    {
        if (!string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId))
        {
            var activeActor = this.registry.GetByRuntimeId(this.activeGposeRebuildRuntimeId);
            if (activeActor != null &&
                !string.Equals(activeActor.PostSpawnPipelineState, "Ready", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(activeActor.PostSpawnPipelineState, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count + 1;
                return;
            }

            if (activeActor != null && string.Equals(activeActor.PostSpawnPipelineState, "Ready", StringComparison.OrdinalIgnoreCase))
                this.gposeRebuildSuccessCount++;

            this.activeGposeRebuildRuntimeId = string.Empty;
            this.activeGposeRebuildSnapshot = null;
        }

        if (this.pendingGposeRebuilds.Count == 0)
        {
            this.gposeRebuildQueueCount = 0;
            if (this.gposeRebuildTotalCount > 0)
            {
                this.LastGposeRebuildResult = $"GPose 退出后串行重建完成：{this.gposeRebuildSuccessCount}/{this.gposeRebuildTotalCount} 个。";
                this.LastMessage = this.LastGposeRebuildResult;
                this.gposeRebuildTotalCount = 0;
            }

            return;
        }

        if (this.activePostSpawnApply != null || this.postSpawnApplyQueue.Count > 0)
        {
            this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count;
            return;
        }

        if (!this.EnsureSpawnWarmup("GPose exit rebuild"))
        {
            this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count;
            return;
        }

        var snapshot = this.pendingGposeRebuilds.Dequeue();
        this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count + 1;
        try
        {
            this.log.Information("[RealNpcSpawnService] GPoseRebuild spawn begin. order={Order}, oldRuntime={OldRuntimeId}, npc={NpcId}/{Name}, cloneRetry={Retry}", snapshot.SortOrder, snapshot.OldRuntimeId, snapshot.Npc.Id, snapshot.Npc.Name, snapshot.CloneRetryAttempt);
            var rebuilt = this.SpawnNew(snapshot.Npc, snapshot.Position, requireWarmup: false, cloneRetryAttempt: snapshot.CloneRetryAttempt);
            if (rebuilt == null)
            {
                this.log.Warning("[RealNpcSpawnService] GPoseRebuild spawn returned null. order={Order}, npc={NpcId}", snapshot.SortOrder, snapshot.Npc.Id);
                this.EnqueueGposeRebuildFront(snapshot);
                return;
            }

            this.ApplyGposeRebuildSnapshot(rebuilt, snapshot);
            this.spawnIntentRegistry.UpdateLastRuntime(snapshot.Npc, rebuilt);
            this.activeGposeRebuildRuntimeId = rebuilt.RuntimeId;
            this.activeGposeRebuildSnapshot = snapshot;
            this.log.Information("[RealNpcSpawnService] GPoseRebuild spawn complete. order={Order}, newRuntime={NewRuntimeId}", snapshot.SortOrder, rebuilt.RuntimeId);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[RealNpcSpawnService] Failed to start GPose rebuild actor. order={Order}, NpcId={NpcId}", snapshot.SortOrder, snapshot.Npc.Id);
        }
    }

    private void EnqueueGposeRebuildFront(GposeActorRebuildSnapshot snapshot)
    {
        var remaining = this.pendingGposeRebuilds.ToList();
        this.pendingGposeRebuilds.Clear();
        this.pendingGposeRebuilds.Enqueue(snapshot);
        foreach (var item in remaining)
            this.pendingGposeRebuilds.Enqueue(item);
        this.gposeRebuildQueueCount = this.pendingGposeRebuilds.Count + (string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId) ? 0 : 1);
    }

    private void UpdateQueuedActorSpawns()
    {
        if (this.pendingActorSpawns.Count == 0)
            return;

        if (this.activePostSpawnApply != null ||
            this.postSpawnApplyQueue.Count > 0 ||
            this.pendingGposeRebuilds.Count > 0 ||
            !string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId))
            return;

        if (!this.EnsureSpawnWarmup("queued actor spawn"))
            return;

        var request = this.pendingActorSpawns.Dequeue();
        this.log.Information("[ActorSpawnQueue] spawn begin order={Order} batchIndex={BatchIndex} npc={NpcId}/{NpcName} position={Position} attempt={Attempt} source={Source}",
            request.SortOrder,
            request.BatchIndex,
            request.Npc.Id,
            request.Npc.Name,
            request.Position,
            request.CloneRetryAttempt,
            request.Source);
        var actor = this.SpawnNew(request.Npc, request.Position, requireWarmup: false, cloneRetryAttempt: request.CloneRetryAttempt);
        if (actor == null)
        {
            this.log.Warning("[ActorSpawnQueue] spawn failed order={Order} batchIndex={BatchIndex} npc={NpcId}", request.SortOrder, request.BatchIndex, request.Npc.Id);
            if (request.CloneRetryAttempt < MaxLocalPlayerCloneRetries)
            {
                this.EnqueueActorSpawnFront(request with
                {
                    CloneRetryAttempt = request.CloneRetryAttempt + 1,
                    Source = $"{request.Source}-retry"
                });
                this.LastMessage = $"Formal spawn returned null; retrying same queued actor batchIndex={request.BatchIndex} attempt={request.CloneRetryAttempt + 1}/{MaxLocalPlayerCloneRetries}.";
            }
            else
            {
                this.LastMessage = $"Formal spawn failed after retries; actor not silently consumed. npc={request.Npc.Name}, batchIndex={request.BatchIndex}.";
            }

            return;
        }

        if (request.UpdateSpawnIntent)
            this.spawnIntentRegistry.UpdateLastRuntime(request.Npc, actor);

        this.CurrentSelectedActorRuntimeId = actor.RuntimeId;
        this.LastMessage = $"串行生成 Actor：{request.BatchIndex + 1}，runtime={actor.RuntimeId}。剩余 {this.pendingActorSpawns.Count}";
        this.log.Information("[ActorSpawnQueue] spawn queued post-apply order={Order} batchIndex={BatchIndex} runtime={RuntimeId}", request.SortOrder, request.BatchIndex, actor.RuntimeId);
    }

    private void RebuildMissingIntentActorsIfNeeded()
    {
        if (this.validityMonitorService.CurrentIsGposing ||
            this.validityMonitorService.IsRebuildScheduled ||
            this.pendingGposeRebuilds.Count > 0 ||
            !string.IsNullOrWhiteSpace(this.activeGposeRebuildRuntimeId))
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

        public int SortOrder { get; init; }

        public long OriginalSpawnSequence { get; init; }

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

        public int CloneRetryAttempt { get; init; }
    }

    private sealed record QueuedActorSpawnRequest(
        CustomNpc Npc,
        Vector3 Position,
        int BatchIndex,
        int SortOrder,
        int CloneRetryAttempt,
        bool UpdateSpawnIntent,
        string Source);

    private sealed class PostSpawnApplyPlan
    {
        public PostSpawnApplyPlan(string runtimeId, long spawnSequence, int sortOrder, string reason)
        {
            this.RuntimeId = runtimeId;
            this.SpawnSequence = spawnSequence;
            this.SortOrder = sortOrder;
            this.Reason = reason;
        }

        public string RuntimeId { get; }

        public long SpawnSequence { get; }

        public int SortOrder { get; }

        public string Reason { get; }

        public PostSpawnApplyState State { get; set; } = PostSpawnApplyState.WaitingObjectStable;

        public int ElapsedTicks { get; set; }

        public int StableTicks { get; set; }

        public int AppearanceWaitTicks { get; set; }

        public string LastAddress { get; set; } = string.Empty;

        public string LastDrawObjectAddress { get; set; } = string.Empty;

        public DateTime AppearanceEnqueuedAt { get; set; } = DateTime.MinValue;

        public int PenumbraWaitTicks { get; set; }

        public int AppearanceRetryCount { get; set; }

        public int RedrawFallbackCount { get; set; }

        public int RedrawWaitTicks { get; set; }

        public int CloneRetryCount { get; set; }

        public string BeforeApplySignature { get; set; } = string.Empty;

        public string LocalPlayerSignature { get; set; } = string.Empty;

        public string LocalPlayerEquipmentSignature { get; set; } = string.Empty;

        public string LocalPlayerCustomizeSignature { get; set; } = string.Empty;

        public string PresetSignature { get; set; } = string.Empty;

        public string ActorBeforeEquipmentSignature { get; set; } = string.Empty;
    }

    private enum PostSpawnApplyState
    {
        WaitingObjectStable,
        ApplyingPenumbra,
        WaitingAfterPenumbra,
        ResetAppearanceToNpcBase,
        ClearEquipmentToEmpty,
        ApplyingAppearance,
        WaitingAfterAppearance,
        VerifyAppearance,
        RequestTargetedRedraw,
        WaitingAfterTargetedRedraw,
        EnableDraw,
        ApplyingBehavior,
    }

    private sealed record AppearanceSignature(string Summary, bool HasAnySignal);

    private sealed record ActorTargetResolveResult(
        bool Success,
        nint NativePtr,
        int GameObjectIndex,
        nint DrawObjectPtr,
        nint LocalPlayerPtr,
        string LocalPlayerIndex,
        string FailureReason)
    {
        public static ActorTargetResolveResult Fail(string reason, nint localPlayerPtr, string localPlayerIndex)
            => new(false, 0, -1, 0, localPlayerPtr, localPlayerIndex, reason);

        public override string ToString()
            => Success
                ? $"stable target ptr=0x{NativePtr:X}, index={GameObjectIndex}, draw=0x{DrawObjectPtr:X}"
                : FailureReason;
    }

    private enum AppearanceApplyResult
    {
        Success,
        PendingDrawObject,
        LocalPlayerCloneAppearance,
        PlayerCustomizeResidual,
        PlayerGearResidual,
        ApplyFailed,
        TargetInvalid,
        CloneRetryExceeded,
        RedrawRetryExceeded,
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

    private static string ReadMember(object? source, string name)
    {
        if (source == null)
            return string.Empty;

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
