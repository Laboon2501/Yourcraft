using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed class RealNpcSpawnService
{
    private const int RuntimeBindTimeoutTicks = 180;
    private const int InitialRebuildDelayTicks = 15;
    private const int SceneRebuildDelayTicks = 45;
    private const int GposeExitRebuildDelayTicks = 90;
    private const int PostSpawnTransformRetryTicks = 45;
    private const int PendingTransformRetryIntervalMilliseconds = 50;


    private readonly IClientState clientState;
    private readonly ITargetManager targetManager;
    private readonly QuestDatabase database;
    private readonly RuntimeActorRegistry registry;
    private readonly SpawnIntentRegistry spawnIntentRegistry = new();
    private readonly BrioNpcBridgeService brioBridge;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly BrioCapabilityBridgeService brioCapabilityBridge;
    private readonly ActorAppearanceLocalizerService appearanceLocalizer;
    private readonly AppearanceApplyService appearanceApplyService;
    private readonly AppearanceApplyQueue appearanceApplyQueue;
    private readonly ActorAnimationService animationService;
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
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly IPluginLog log;

    private EventNpcHostService? eventNpcHostService;
    private uint lastTerritoryType;
    private long nextRuntimeActorSequence;
    private bool initialActorRefreshQueued;
    private bool lastObservedGpose;
    private bool lastSceneReady;
    private int scheduledRebuildDelayTicks = -1;
    private string scheduledRebuildReason = string.Empty;
    private DateTime lastRebuildAttemptAt = DateTime.MinValue;

    public RealNpcSpawnService(
        IClientState clientState,
        ITargetManager targetManager,
        QuestDatabase database,
        RuntimeActorRegistry registry,
        BrioNpcBridgeService brioBridge,
        BrioAssemblyBridgeService brioAssemblyBridge,
        BrioCapabilityBridgeService brioCapabilityBridge,
        ActorAppearanceLocalizerService appearanceLocalizer,
        AppearanceApplyService appearanceApplyService,
        AppearanceApplyQueue appearanceApplyQueue,
        ActorAnimationService animationService,
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
        this.appearanceLocalizer = appearanceLocalizer;
        this.appearanceApplyService = appearanceApplyService;
        this.appearanceApplyQueue = appearanceApplyQueue;
        this.animationService = animationService;
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

    public string LastMessage { get; private set; } = "Actor spawn service ready.";
    public string BridgeStatus => this.brioBridge.StatusText;
    public bool IsBrioIpcAvailable => this.brioBridge.IsIpcAvailable;
    public (int Major, int Minor)? BrioIpcVersion => this.brioBridge.CurrentApiVersion;
    public bool IsBrioIpcCompatible => this.brioBridge.IsCompatible;
    public string LastSpawnError => this.brioBridge.LastFailureReason;
    public string BrioAssemblyStatus => this.brioAssemblyBridge.ActorSpawnStatusText;
    public bool IsBrioAssemblyLoaded => this.brioAssemblyBridge.IsBrioAssemblyLoaded;
    public bool HasBrioActorSpawnService => this.brioAssemblyBridge.HasActorSpawnService;
    public bool CanSpawnRealActor => this.brioAssemblyBridge.CanSpawnNativeActor;

    public bool EnableUnsafeNativeWrites
    {
        get => this.brioAssemblyBridge.EnableUnsafeNativeWrites;
        set => this.brioAssemblyBridge.EnableUnsafeNativeWrites = value;
    }

    public bool IsSafeMode => !this.EnableUnsafeNativeWrites;
    public IReadOnlyList<RuntimeActorInstance> Actors => this.registry.GetAll();
    public bool IsBusy => this.registry.GetAll().Any(actor => actor.LifecycleState is ActorLifecycleState.SpawnPending or ActorLifecycleState.Spawning or ActorLifecycleState.BindingRuntime);
    public string BusyStatus => this.IsBusy ? "Actor runtime binding in progress" : "Actor runtime idle";
    public string SpawnPrewarmStatus => "Disabled: direct ActorConfig spawn pipeline is active.";
    public string FormalQueueStatus => $"DirectSpawn scheduler: pending={this.CountPendingSpawnWork()}, scheduled={this.scheduledRebuildDelayTicks}, sceneReady={this.lastSceneReady}.";
    public bool CanRunActorSpawnQueue => this.lastSceneReady && this.HasPendingCurrentTerritorySpawnWork();
    public int PendingActorSpawnCount => this.CountPendingSpawnWork();
    public string ActorSpawnQueueDebug => $"territory={this.clientState.TerritoryType}; configs={this.database.ActorConfigs.Count}; runtime={this.registry.Count}; pending={this.CountPendingSpawnWork()}; gpose={this.validityMonitorService.CurrentIsGposing}; sceneReady={this.lastSceneReady}; scheduledRebuild={this.scheduledRebuildDelayTicks}; reason={this.scheduledRebuildReason}; localPlayer={this.brioAssemblyBridge.LocalPlayerStatusText}";
    public int AppearanceQueueLength => this.appearanceApplyQueue.Count;
    public string AppearanceQueueCurrentActor => this.appearanceApplyQueue.CurrentActorRuntimeId;
    public long AppearanceQueueLastElapsedMilliseconds => this.appearanceApplyQueue.LastElapsedMilliseconds;
    public string AppearanceQueueLastError => this.appearanceApplyQueue.LastError;
    public string AppearanceQueueStatus => this.appearanceApplyQueue.LastStatus;
    public string ActorValidityMonitorStatus => this.validityMonitorService.LastStatus;
    public bool CurrentIsGposing => this.validityMonitorService.CurrentIsGposing;
    public bool PreviousFrameIsGposing => this.validityMonitorService.PreviousFrameIsGposing;
    public DateTime? LastGposeExitedAt => this.validityMonitorService.LastGposeExitedAt;
    public bool IsGposeRebuildScheduled => false;
    public TimeSpan GposeRebuildWaitRemaining => TimeSpan.Zero;
    public int GposeRebuildQueueCount => 0;
    public string LastGposeRebuildResult { get; private set; } = "No GPose rebuild is queued in direct spawn mode.";
    public int SpawnIntentCount => this.CountPendingSpawnWork();
    public IReadOnlyList<SpawnIntent> SpawnIntents => this.spawnIntentRegistry.GetAll();
    public bool IsHumanoidGlamourerApplyStateAvailable => this.appearanceApplyService.IsHumanoidGlamourerApplyStateAvailable;
    public string HumanoidGlamourerApplyStateSignature => this.appearanceApplyService.HumanoidGlamourerApplyStateSignature;
    public bool IsHumanoidBrioActorAppearanceAvailable => this.appearanceApplyService.IsHumanoidBrioActorAppearanceAvailable;
    public string HumanoidBrioActorAppearanceSignature => this.appearanceApplyService.HumanoidBrioActorAppearanceSignature;
    public string BrioIpcProbeMessage => this.brioBridge.IpcProbe.LastProbeMessage;
    public string GlamourerIpcProbeMessage => string.IsNullOrWhiteSpace(this.glamourerIpcProbe.LastProbeMessage)
        ? this.penumbraIpc.LastStatus
        : $"{this.glamourerIpcProbe.LastProbeMessage} | {this.penumbraIpc.LastStatus}";

    public void SetEventNpcHostService(EventNpcHostService service) => this.eventNpcHostService = service;
    public void SetMessage(string message) => this.LastMessage = message;

    public void ProbeBrioIpc()
    {
        this.brioBridge.IpcProbe.Probe();
        this.LastMessage = this.brioBridge.IpcProbe.LastProbeMessage;
    }

    public void ProbeGlamourerIpc()
    {
        this.glamourerIpcProbe.Probe();
        this.glamourerIpcBridge.Probe();
        this.penumbraIpc.TryConnectOrRefresh("manual actor spawn service probe");
        this.LastMessage = this.GlamourerIpcProbeMessage;
    }

    public CustomNpc? GetNpcById(string npcId) => this.database.GetNpcById(npcId);

    public int GetConfiguredRestoreActorCount()
    {
        var territory = CurrentTerritory(this.clientState);
        return this.database.ActorConfigs.Count(config => config.AutoSpawn && config.TerritoryType == territory && territory != 0);
    }

    public int QueueRestoreConfiguredActors()
    {
        var before = this.registry.GetAll().Count;
        this.RefreshActors();
        return Math.Max(0, this.registry.GetAll().Count - before);
    }

    public void RequestActorRebuild(string reason)
        => this.ScheduleRebuild(reason, 0);

    public RuntimeActorInstance? SpawnNew(
        CustomNpc npc,
        Vector3? overrideSpawnPosition = null,
        Vector3? overrideRotationEuler = null,
        Vector3? overrideScale = null,
        bool requireWarmup = true,
        int cloneRetryAttempt = 0,
        int? overrideSortOrder = null,
        string? requestedRuntimeId = null)
    {
        if (npc == null)
            return null;

        var runtimeId = string.IsNullOrWhiteSpace(requestedRuntimeId) ? Guid.NewGuid().ToString("N") : requestedRuntimeId!;
        var position = overrideSpawnPosition ?? this.GetDefaultSpawnPosition(npc);
        var rotation = overrideRotationEuler ?? ToVector3(npc.DefaultRotationEuler);
        var scale = NormalizeScale(overrideScale ?? ToVector3(npc.DefaultScale, Vector3.One));
        var config = this.CreateConfig(npc, runtimeId, position, rotation, scale, overrideSortOrder ?? this.GetNextSortOrder(npc.Id));
        if (!this.IsValidPersistentActorConfig(config, out var invalidReason))
        {
            this.LastMessage = $"Legacy NPC ActorConfig was not created: {invalidReason}";
            return null;
        }

        this.database.ActorConfigs.Add(config);
        this.database.Save();

        var shell = this.EnsureShell(config, npc);
        this.SetLifecycle(shell, ActorLifecycleState.SpawnPending, "ActorConfig created; waiting for framework update spawn.");
        this.LastMessage = $"Created ActorConfig {ShortId(config.ConfigId)} for NPC {npc.Name}.";
        this.ScheduleRebuild("ActorConfig created; spawn on framework update", 0);
        return shell;
    }

    public RuntimeActorInstance? SpawnFromGlamourerDesign(GlamourerDesignEntry design)
    {
        if (!this.appearanceLocalizer.TryCreateFromGlamourerDesign(design, out var appearance, out var reason))
        {
            this.LastMessage = reason;
            return null;
        }

        return this.SpawnNewActor(
            string.IsNullOrWhiteSpace(design.Name) ? "Glamourer Design Actor" : design.Name,
            appearance,
            this.GetDefaultActorSpawnPosition(),
            Vector3.Zero,
            Vector3.One,
            this.GetNextSortOrderForActor());
    }

    public RuntimeActorInstance? SpawnFromGlamourerNpc(GameNpcCatalogEntry entry)
    {
        if (!this.appearanceLocalizer.TryCreateFromGlamourerNpc(entry, out var appearance, out var reason))
        {
            this.LastMessage = reason;
            return null;
        }

        return this.SpawnNewActor(
            string.IsNullOrWhiteSpace(entry.Name) ? "Glamourer NPC Actor" : entry.Name,
            appearance,
            this.GetDefaultActorSpawnPosition(),
            Vector3.Zero,
            Vector3.One,
            this.GetNextSortOrderForActor());
    }

    public RuntimeActorInstance? SpawnNewActor(
        string displayName,
        ActorAppearanceData appearance,
        Vector3? overrideSpawnPosition = null,
        Vector3? overrideRotationEuler = null,
        Vector3? overrideScale = null,
        int? overrideSortOrder = null)
    {
        var runtimeId = Guid.NewGuid().ToString("N");
        var position = overrideSpawnPosition ?? this.GetDefaultActorSpawnPosition();
        var rotation = overrideRotationEuler ?? Vector3.Zero;
        var scale = NormalizeScale(overrideScale ?? Vector3.One);
        var config = this.CreateConfig(displayName, appearance, runtimeId, position, rotation, scale, overrideSortOrder ?? this.GetNextSortOrderForActor());
        this.database.ActorConfigs.Add(config);
        this.database.Save();

        var shell = this.EnsureShell(config, null);
        this.SetLifecycle(shell, ActorLifecycleState.SpawnPending, "ActorConfig created; waiting for framework update spawn.");
        this.LastMessage = $"Created ActorConfig {ShortId(config.ConfigId)} from {appearance.SourceKind}: {displayName}.";
        this.ScheduleRebuild($"ActorConfig created from {appearance.SourceKind}; spawn on framework update", 0);
        return shell;
    }

    public RuntimeActorInstance? SpawnUnique(CustomNpc npc)
    {
        var territory = CurrentTerritory(this.clientState);
        var existingConfig = this.database.ActorConfigs
            .Where(config => config.TerritoryType == territory && string.Equals(config.SourceNpcPresetId, npc.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(config => config.SortOrder)
            .ThenBy(config => config.SpawnSequence)
            .FirstOrDefault();

        if (existingConfig == null)
            return this.SpawnNew(npc);

        var actor = this.EnsureShell(existingConfig, npc);
        return actor.LifecycleState == ActorLifecycleState.Ready
            ? actor
            : this.TrySpawnConfigNow(existingConfig, actor, "SpawnUnique existing config") ?? actor;
    }

    public void QueueSpawnMany(CustomNpc npc, int count, Vector3 basePosition, Vector3 offset)
    {
        var safeCount = Math.Clamp(count, 1, 100);
        RuntimeActorInstance? last = null;
        for (var index = 0; index < safeCount; index++)
        {
            var position = basePosition + (offset * index);
            last = this.SpawnNew(npc, position, ToVector3(npc.DefaultRotationEuler), ToVector3(npc.DefaultScale, Vector3.One), requireWarmup: false, overrideSortOrder: this.GetNextSortOrder(npc.Id) + index);
        }

        this.LastMessage = $"Created {safeCount} ActorConfig/runtime actor(s). Last={last?.RuntimeId ?? "none"}.";
    }

    public bool QueueRestoreActor(CustomNpc npc, Vector3 position, Vector3 rotationEuler, Vector3 scale, int sortOrder, int batchIndex, string source)
        => this.SpawnNew(npc, position, rotationEuler, scale, requireWarmup: false, overrideSortOrder: sortOrder, requestedRuntimeId: Guid.NewGuid().ToString("N")) != null;

    public bool Despawn(string runtimeId, DespawnReason reason)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        var config = this.GetConfig(runtimeId);
        if (actor != null)
            this.DespawnRuntimeOnly(actor, reason.ToString());

        if (config != null && reason == DespawnReason.UserRequested)
        {
            this.database.ActorConfigs.Remove(config);
            this.database.Save();
        }

        this.registry.Remove(runtimeId);
        this.appearanceApplyQueue.RemoveJobsForActor(runtimeId);
        this.LastMessage = $"Despawned Actor {ShortId(runtimeId)}. reason={reason}.";
        return true;
    }

    public void DespawnAllForNpc(string npcId)
    {
        var runtimeIds = this.database.ActorConfigs
            .Where(config => string.Equals(config.SourceNpcPresetId, npcId, StringComparison.OrdinalIgnoreCase))
            .Select(config => config.RuntimeId)
            .Concat(this.registry.GetByNpcId(npcId).Select(actor => actor.RuntimeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var runtimeId in runtimeIds)
        {
            if (this.registry.GetByRuntimeId(runtimeId) is { } actor)
                this.DespawnRuntimeOnly(actor, "delete npc actors");
            this.registry.Remove(runtimeId);
            this.appearanceApplyQueue.RemoveJobsForActor(runtimeId);
        }

        this.database.ActorConfigs.RemoveAll(config => string.Equals(config.SourceNpcPresetId, npcId, StringComparison.OrdinalIgnoreCase));
        this.database.Save();
        this.LastMessage = $"Deleted {runtimeIds.Count} actor config/runtime actor(s) for NPC {npcId}.";
    }

    public void DespawnAll() => this.DespawnAll(deleteConfigs: false);

    public void DespawnAll(bool deleteConfigs)
    {
        var runtimeCount = this.registry.GetAll().Count;
        foreach (var actor in this.registry.GetAll().ToList())
            this.DespawnRuntimeOnly(actor, deleteConfigs ? "delete all actors" : "runtime cleanup");

        this.registry.Clear();
        if (deleteConfigs)
        {
            this.database.ActorConfigs.Clear();
            this.database.Save();
        }

        this.LastMessage = deleteConfigs
            ? $"Deleted all ActorConfig/runtime actors. runtime={runtimeCount}."
            : $"Despawned all runtime actors; persistent ActorConfig entries were kept. runtime={runtimeCount}.";
    }

    public bool MoveActor(string runtimeId, Vector3 position)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
        {
            this.LastMessage = $"Runtime actor not found: {runtimeId}.";
            return false;
        }

        var scale = actor.TransformEditScale == Vector3.Zero ? Vector3.One : actor.TransformEditScale;
        return this.ApplyActorTransform(runtimeId, position, actor.TransformEditRotationEuler, scale);
    }

    public bool RefreshActorTransform(string runtimeId)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
        {
            this.LastMessage = $"Runtime actor not found: {runtimeId}.";
            return false;
        }

        var success = this.TryRefreshActualActorTransform(actor, updateEditingTransform: true, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool ApplyActorTransform(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
        => this.ApplyActorTransformInternal(runtimeId, position, rotationEuler, scale, fromPendingFlush: false);

    private bool ApplyActorTransformInternal(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale, bool fromPendingFlush)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        var config = this.GetConfig(runtimeId);
        if (actor == null && config != null)
            actor = this.EnsureShell(config, this.database.GetNpcById(config.SourceNpcPresetId));

        if (actor == null)
        {
            this.LastMessage = $"Runtime actor/config not found: {runtimeId}.";
            return false;
        }

        var normalizedScale = NormalizeScale(scale);
        actor.SpawnPosition = position;
        actor.SpawnRotationEuler = rotationEuler;
        actor.SpawnScale = normalizedScale;
        actor.HasSavedTransform = true;

        if (!this.IsRuntimeReady(actor))
        {
            this.QueuePendingTransform(actor, position, rotationEuler, normalizedScale, "Runtime actor is not Ready; transform saved and queued.");
            this.LastMessage = actor.LastTransformError;
            return false;
        }

        if (!IsGeneratedActorTarget(actor, out var targetReason))
        {
            actor.RuntimeTransformApplied = false;
            if (fromPendingFlush)
                actor.PendingTransformRetryTicksRemaining = Math.Max(0, actor.PendingTransformRetryTicksRemaining - 1);
            actor.HasPendingTransformApply = fromPendingFlush && actor.PendingTransformRetryTicksRemaining > 0;
            actor.LastTransformError = targetReason;
            actor.LastTransformReadback = $"native readback unavailable: {targetReason}";
            this.LastMessage = actor.LastTransformError;
            return false;
        }

        this.TryRefreshActualActorTransform(actor, updateEditingTransform: false, out _);
        actor.LastMoveBeforePosition = actor.LastKnownPosition;
        var brioSuccess = this.brioCapabilityBridge.TryApplyModelTransform(actor, position, rotationEuler, normalizedScale, out var brioReason);
        var nativeSuccess = this.brioAssemblyBridge.TryApplyActorNativeRootTransform(actor, position, rotationEuler.Y, normalizedScale, out var nativeReason);
        var readbackSuccess = this.TryRefreshActualActorTransform(actor, updateEditingTransform: false, out var readbackReason);
        var mismatchReason = string.Empty;
        var success = (brioSuccess || nativeSuccess) &&
            readbackSuccess &&
            TransformReadbackMatches(actor, position, rotationEuler, normalizedScale, out mismatchReason);
        var reason = $"brio={brioReason}; nativeRoot={nativeReason}; readback={readbackReason}";
        if (!success && !string.IsNullOrWhiteSpace(mismatchReason))
            reason = $"{reason}; mismatch={mismatchReason}";

        if (success)
        {
            actor.HasPendingTransformApply = false;
            actor.PendingTransformRetryTicksRemaining = 0;
        }
        else if (fromPendingFlush)
        {
            actor.PendingTransformRetryTicksRemaining = Math.Max(0, actor.PendingTransformRetryTicksRemaining - 1);
            actor.HasPendingTransformApply = actor.PendingTransformRetryTicksRemaining > 0;
        }
        else
        {
            actor.PendingTransformPosition = position;
            actor.PendingTransformRotationEuler = rotationEuler;
            actor.PendingTransformScale = normalizedScale;
            actor.PendingTransformRetryTicksRemaining = PostSpawnTransformRetryTicks;
            actor.HasPendingTransformApply = true;
        }
        actor.LastMoveTargetPosition = position;
        actor.LastMoveAfterPosition = actor.LastKnownPosition;
        actor.LastMoveActorValidAfter = actor.IsValid;
        actor.LastMoveDistanceReasonable = Vector3.Distance(actor.LastKnownPosition, position) < 2.0f;
        actor.LastTransformError = success ? string.Empty : reason;
        actor.LastTransformReadback = success
            ? $"native position={actor.LastKnownPosition}; rotationEuler={actor.LastKnownRotationEuler}; scale={actor.LastKnownScale}; requested position={position}; rotationEuler={rotationEuler}; scale={normalizedScale}; yaw-only native rotation readback"
            : $"native readback={(readbackSuccess ? $"position={actor.LastKnownPosition}; rotationEuler={actor.LastKnownRotationEuler}; scale={actor.LastKnownScale}" : "unavailable")}; requested position={position}; rotationEuler={rotationEuler}; scale={normalizedScale}; {reason}";
        actor.RuntimeTransformApplied = success;
        if (success)
            actor.LastSuccessfulTransformApplyAt = DateTime.UtcNow;
        this.LastMessage = success ? $"Applied WorldTransform to Actor {ShortId(runtimeId)}." : $"Transform apply failed: {reason}";
        return success;
    }

    private void QueuePendingTransform(RuntimeActorInstance actor, Vector3 position, Vector3 rotationEuler, Vector3 scale, string reason)
    {
        actor.HasPendingTransformApply = true;
        actor.PendingTransformPosition = position;
        actor.PendingTransformRotationEuler = rotationEuler;
        actor.PendingTransformScale = NormalizeScale(scale);
        actor.PendingTransformRetryTicksRemaining = Math.Max(actor.PendingTransformRetryTicksRemaining, PostSpawnTransformRetryTicks);
        actor.RuntimeTransformApplied = false;
        actor.LastTransformError = reason;
        actor.LastTransformReadback = $"native readback unavailable; saved position={position}; rotationEuler={rotationEuler}; scale={actor.PendingTransformScale}";
    }

    private bool TryRefreshActualActorTransform(RuntimeActorInstance actor, bool updateEditingTransform, out string reason)
    {
        if (!this.IsRuntimeReady(actor))
        {
            reason = "native readback unavailable: Runtime actor is not Ready.";
            actor.LastTransformError = reason;
            actor.LastTransformReadback = reason;
            return false;
        }

        if (this.brioAssemblyBridge.TryReadActorNativeTransform(actor, out var nativePosition, out var nativeRotationEuler, out var nativeScale, out var nativeReason))
        {
            if (updateEditingTransform)
            {
                actor.TransformEditPosition = nativePosition;
                actor.TransformEditRotationEuler = nativeRotationEuler;
                actor.TransformEditScale = nativeScale == Vector3.Zero ? Vector3.One : nativeScale;
            }
            actor.LastTransformError = string.Empty;
            actor.LastTransformReadback = $"native position={nativePosition}; rotationEuler={nativeRotationEuler}; scale={nativeScale}; {nativeReason}";
            reason = actor.LastTransformReadback;
            return true;
        }

        if (this.brioCapabilityBridge.TryReadModelTransform(actor, updateEditingTransform, out var brioReason))
        {
            actor.LastTransformReadback = $"brio runtime position={actor.LastKnownPosition}; rotationEuler={actor.LastKnownRotationEuler}; scale={actor.LastKnownScale}; native readback unavailable: {nativeReason}";
            reason = actor.LastTransformReadback;
            actor.LastTransformError = reason;
            return false;
        }

        reason = $"native readback unavailable: {nativeReason}; brio readback unavailable: {brioReason}";
        actor.LastTransformError = reason;
        actor.LastTransformReadback = reason;
        return false;
    }

    private static bool TransformReadbackMatches(RuntimeActorInstance actor, Vector3 position, Vector3 rotationEuler, Vector3 scale, out string reason)
    {
        var positionOk = Vector3.Distance(actor.LastKnownPosition, position) <= 0.35f;
        var yawOk = MathF.Abs(NormalizeAngleDelta(actor.LastKnownRotationEuler.Y - rotationEuler.Y)) <= 0.075f;
        var expectedNativeScale = new Vector3(MathF.Max(0.01f, scale.Y));
        var scaleOk = Vector3.Distance(actor.LastKnownScale, expectedNativeScale) <= 0.05f;

        var parts = new List<string>();
        if (!positionOk)
            parts.Add($"position actual={actor.LastKnownPosition}, requested={position}");
        if (!yawOk)
            parts.Add($"yaw actual={actor.LastKnownRotationEuler.Y:F4}, requested={rotationEuler.Y:F4}");
        if (!scaleOk)
            parts.Add($"scale actual={actor.LastKnownScale}, requestedNativeUniform={expectedNativeScale}");

        reason = string.Join("; ", parts);
        return positionOk && yawOk && scaleOk;
    }

    private static float NormalizeAngleDelta(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;
        while (angle < -MathF.PI)
            angle += MathF.Tau;
        return angle;
    }

    private static bool IsGeneratedActorTarget(RuntimeActorInstance actor, out string reason)
    {
        var objectIndex = actor.LastKnownObjectIndex;
        if (int.TryParse(actor.ObjectIndex, out var parsedIndex))
            objectIndex = parsedIndex;

        if (objectIndex <= 0)
        {
            reason = $"invalid spawned Actor objectIndex={objectIndex}; refusing transform write to objectIndex 0/local player.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void SaveActorTransformSnapshot(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var config = this.GetConfig(runtimeId);
        var actor = this.registry.GetByRuntimeId(runtimeId);
        var normalizedScale = NormalizeScale(scale);
        this.SaveConfigTransform(config, position, rotationEuler, normalizedScale);
        if (actor != null)
        {
            actor.TransformEditPosition = position;
            actor.TransformEditRotationEuler = rotationEuler;
            actor.TransformEditScale = normalizedScale;
            actor.HasSavedTransform = true;
            actor.SavedTransformSnapshot = $"position={position}; rotationEuler={rotationEuler}; scale={normalizedScale}";
        }

        this.LastMessage = $"Saved WorldTransform for Actor {ShortId(runtimeId)}.";
    }

    public void PersistActorWorldTransformToNpc(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
        => this.SaveActorTransformSnapshot(runtimeId, position, rotationEuler, scale);

    public bool ApplyActorModelCharaOverride(string runtimeId, uint modelCharaId)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        var config = this.GetConfig(runtimeId);
        if (actor == null && config != null)
            actor = this.EnsureShell(config, this.database.GetNpcById(config.SourceNpcPresetId));

        if (config == null || actor == null)
        {
            this.LastMessage = $"Runtime actor/config not found: {runtimeId}.";
            return false;
        }

        config.Appearance ??= new ActorAppearanceData();
        config.Appearance.ModelCharaOverrideId = modelCharaId;
        config.SpawnKind = NormalizeSpawnKindForModelOverride(config.SpawnKind, config.Appearance, modelCharaId);
        config.Appearance.SpawnKind = config.SpawnKind;
        config.Appearance.IsHumanoid = config.SpawnKind == ActorSpawnKind.Character;
        this.database.Save();

        actor.SourceModelCharaId = config.Appearance.ModelCharaId;
        actor.ModelCharaOverrideId = modelCharaId;
        actor.EditingModelCharaId = modelCharaId != 0 ? modelCharaId : config.Appearance.ModelCharaId;
        actor.SpawnKind = config.SpawnKind;
        actor.SourceActorKind = string.IsNullOrWhiteSpace(config.SourceActorKind) ? config.Appearance.SourceActorKind : config.SourceActorKind;
        actor.SpawnKindStatus = $"model override saved. sourceModelChara={actor.SourceModelCharaId}, overrideModelChara={actor.ModelCharaOverrideId}, effectiveModelChara={actor.EditingModelCharaId}, spawnKind={actor.SpawnKind}";

        if (!this.IsRuntimeReady(actor))
        {
            actor.LastModelCharaApplyError = "Runtime actor is not Ready; ModelChara override saved and will apply after spawn/rebuild.";
            actor.LastModelCharaApplyResult = string.Empty;
            this.LastMessage = actor.LastModelCharaApplyError;
            return false;
        }

        var ok = this.ApplyNpcAppearance(actor.RuntimeId);
        actor.LastModelCharaApplyResult = ok ? $"ModelChara override applied. source={actor.SourceModelCharaId}, override={actor.ModelCharaOverrideId}, effective={actor.EditingModelCharaId}, actual={actor.LastAppliedModelCharaId}" : string.Empty;
        actor.LastModelCharaApplyError = ok ? string.Empty : actor.LastAppearanceError;
        this.LastMessage = ok ? actor.LastModelCharaApplyResult : $"ModelChara override apply failed: {actor.LastModelCharaApplyError}";
        return ok;
    }

    public void UpdateActorLookAtSettings(string runtimeId, bool enabled, float radius)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
            return;

        actor.LookAtPlayerEnabled = enabled;
        actor.LookAtRadius = MathF.Max(0.1f, radius);
        actor.LookAtMode = enabled ? NpcLookAtMode.NativeLookAt : NpcLookAtMode.None;
        if (!enabled)
            this.lookAtService.Stop(actor, out _);
        this.PersistBehavior(actor);
        this.LastMessage = $"Updated look-at settings for Actor {ShortId(runtimeId)}.";
    }

    public bool PlayAnimation(string runtimeId, uint animationId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        actor.CurrentAnimationId = animationId;
        actor.AnimationEnabled = animationId != 0;
        this.PersistBehavior(actor);
        if (!this.IsRuntimeReady(actor))
        {
            actor.LastAnimationError = "Runtime actor is not Ready; animation saved to ActorConfig.";
            this.LastMessage = actor.LastAnimationError;
            return false;
        }

        var success = this.animationService.Play(actor, animationId, out var reason);
        actor.LastAnimationResult = reason;
        actor.LastAnimationError = success ? string.Empty : reason;
        this.LastMessage = reason;
        return success;
    }

    public bool StopAnimation(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        actor.AnimationEnabled = false;
        this.PersistBehavior(actor);
        var reason = string.Empty;
        var success = !this.IsRuntimeReady(actor) || this.animationService.Stop(actor, out reason);
        this.LastMessage = success ? $"Stopped animation for Actor {ShortId(runtimeId)}." : reason;
        return success;
    }

    public bool PlayExpressionBlend(string runtimeId, uint expressionId, ActorExpressionLayer layer)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        actor.CurrentExpressionId = expressionId;
        actor.CurrentExpressionLayer = layer;
        if (expressionId == 0 || layer == ActorExpressionLayer.None)
        {
            actor.LastExpressionError = "Expression blend id is 0 or layer is None.";
            actor.LastExpressionResult = string.Empty;
            this.LastMessage = actor.LastExpressionError;
            return false;
        }

        if (!this.IsRuntimeReady(actor))
        {
            actor.LastExpressionError = "Actor not ready; expression blend was not applied.";
            actor.LastExpressionResult = string.Empty;
            this.LastMessage = actor.LastExpressionError;
            return false;
        }

        var success = this.ApplyExpressionBlendInternal(actor, expressionId, layer, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool StartExpressionBlendLoop(string runtimeId, uint expressionId, ActorExpressionLayer layer, float intervalSeconds)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        actor.CurrentExpressionId = expressionId;
        actor.CurrentExpressionLayer = layer == ActorExpressionLayer.None ? ActorExpressionLayer.Facial : layer;
        actor.ExpressionBlendLoopIntervalSeconds = Math.Max(0.05f, intervalSeconds);
        if (!this.ApplyExpressionBlendInternal(actor, actor.CurrentExpressionId, actor.CurrentExpressionLayer, out var reason))
        {
            actor.ExpressionBlendLoopEnabled = false;
            this.LastMessage = reason;
            return false;
        }

        actor.ExpressionBlendLoopEnabled = true;
        actor.LastExpressionBlendLoopAt = DateTime.UtcNow;
        actor.LastExpressionResult = $"Expression blend loop started: {actor.CurrentExpressionId}, interval={actor.ExpressionBlendLoopIntervalSeconds:F2}s.";
        actor.LastExpressionError = string.Empty;
        this.LastMessage = actor.LastExpressionResult;
        return true;
    }

    public void StopExpressionBlendLoop(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return;
        }

        actor.ExpressionBlendLoopEnabled = false;
        actor.LastExpressionResult = "Expression blend loop stopped.";
        actor.LastExpressionError = string.Empty;
        this.LastMessage = actor.LastExpressionResult;
    }

    public void ClearExpressionBlend(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return;
        }

        actor.CurrentExpressionId = 0;
        actor.CurrentExpressionLayer = ActorExpressionLayer.Facial;
        actor.ExpressionBlendLoopEnabled = false;
        actor.LastExpressionError = string.Empty;
        actor.LastExpressionResult = "Expression blend selection cleared.";
        this.LastMessage = actor.LastExpressionResult;
    }

    public bool ApplyLipTalk(string runtimeId, uint lipTimelineId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        var success = this.ApplyLipTalkInternal(actor, lipTimelineId, out var reason);
        this.LastMessage = reason;
        return success;
    }

    public bool StartLipTalkLoop(string runtimeId, uint lipTimelineId, float intervalSeconds)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return false;
        }

        actor.CurrentLipTalkId = lipTimelineId;
        actor.LipTalkLoopIntervalSeconds = Math.Max(0.05f, intervalSeconds);
        if (!this.ApplyLipTalkInternal(actor, actor.CurrentLipTalkId, out var reason))
        {
            actor.LipTalkLoopEnabled = false;
            this.LastMessage = reason;
            return false;
        }

        actor.LipTalkLoopEnabled = true;
        actor.LastLipTalkLoopAt = DateTime.UtcNow;
        actor.LastLipTalkResult = $"Lip talk loop started: {actor.CurrentLipTalkId}, interval={actor.LipTalkLoopIntervalSeconds:F2}s.";
        actor.LastLipTalkError = string.Empty;
        this.LastMessage = actor.LastLipTalkResult;
        return true;
    }

    public void StopLipTalkLoop(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
        {
            this.LastMessage = $"Actor not found: {runtimeId}.";
            return;
        }

        actor.LipTalkLoopEnabled = false;
        this.ApplyLipTalkInternal(actor, 0, out _);
        actor.LastLipTalkResult = "Lip talk loop stopped.";
        actor.LastLipTalkError = string.Empty;
        this.LastMessage = actor.LastLipTalkResult;
    }

    private bool ApplyExpressionBlendInternal(RuntimeActorInstance actor, uint expressionId, ActorExpressionLayer layer, out string reason)
    {
        if (!this.IsRuntimeReady(actor))
        {
            reason = "Actor not ready; expression blend was not applied.";
            actor.LastExpressionError = reason;
            actor.LastExpressionResult = string.Empty;
            return false;
        }

        var success = this.animationService.PlayExpressionTimeline(actor, expressionId, layer, out reason);
        actor.LastExpressionResult = success ? reason : string.Empty;
        actor.LastExpressionError = success ? string.Empty : reason;
        return success;
    }

    private bool ApplyLipTalkInternal(RuntimeActorInstance actor, uint lipTimelineId, out string reason)
    {
        if (lipTimelineId == 0)
            return this.animationService.ApplyLipTalk(actor, 0, out reason);

        if (!this.IsRuntimeReady(actor))
        {
            reason = "Actor not ready; lip talk was not applied.";
            actor.LastLipTalkError = reason;
            actor.LastLipTalkResult = string.Empty;
            return false;
        }

        var success = this.animationService.ApplyLipTalk(actor, lipTimelineId, out reason);
        actor.LastLipTalkResult = success ? reason : string.Empty;
        actor.LastLipTalkError = success ? string.Empty : reason;
        return success;
    }

    public void ResetActionSequence(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
            return;

        this.actionSequenceService.Reset(actor);
        this.PersistBehavior(actor);
        this.LastMessage = $"Reset action sequence for Actor {ShortId(runtimeId)}.";
    }

    public void TestActionSequenceStep(string runtimeId, Guid stepId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
            return;

        var step = actor.ActionSequence.FirstOrDefault(item => item.Id == stepId);
        if (step == null)
        {
            this.LastMessage = $"Action sequence step not found: {stepId}.";
            return;
        }

        var success = this.actionSequenceService.TestStep(actor, step, out var reason);
        actor.LastActionSequenceError = success ? string.Empty : reason;
        this.PersistBehavior(actor);
        this.LastMessage = reason;
    }

    public void EnqueueNpcAppearance(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is { } actor && actor.LifecycleState == ActorLifecycleState.Ready)
            this.appearanceApplyQueue.Enqueue(runtimeId, "manual actor appearance apply");
        else
            this.LastMessage = $"Actor {ShortId(runtimeId)} is not Ready; appearance will apply on next successful bind.";
    }

    public void ApplyNpcAppearanceForNpc(string npcId)
    {
        foreach (var actor in this.registry.GetByNpcId(npcId).Where(this.IsRuntimeReady))
            this.ApplyNpcAppearance(actor.RuntimeId);
        this.LastMessage = $"Applied appearance for runtime actors from NPC {npcId}.";
    }

    public void ApplyAllNpcAppearances()
    {
        foreach (var actor in this.registry.GetAll().Where(this.IsRuntimeReady))
            this.ApplyNpcAppearance(actor.RuntimeId);
    }

    public void LogActorAppearanceDiagnostics(string runtimeId)
    {
        if (this.registry.GetByRuntimeId(runtimeId) is not { } actor)
            return;

        this.log.Information("[ActorAppearance] runtime={RuntimeId}, npc={NpcId}, state={State}, objectIndex={ObjectIndex}, address={Address}, last={Last}", actor.RuntimeId, actor.NpcId, actor.LifecycleState, actor.ObjectIndex, actor.Address, actor.LastAppearanceApplyResult);
        this.LastMessage = $"Logged appearance diagnostics for Actor {ShortId(runtimeId)}.";
    }

    public void ForceClearAndReapplyAppearance(string runtimeId) => this.ApplyNpcAppearance(runtimeId);
    public void ForceTargetedRedrawAndReapplyAppearance(string runtimeId) => this.ApplyNpcAppearance(runtimeId);

    public void RefreshActors()
    {
        var territory = CurrentTerritory(this.clientState);
        foreach (var actor in this.registry.GetAll().ToList())
        {
            if (actor.SpawnedTerritoryType != territory)
            {
                this.DespawnRuntimeOnly(actor, "wrong territory");
                this.registry.Remove(actor.RuntimeId);
            }
        }

        foreach (var config in this.database.ActorConfigs.Where(config => config.AutoSpawn && config.TerritoryType == territory).OrderBy(config => config.SortOrder).ThenBy(config => config.SpawnSequence).ToList())
        {
            if (!this.IsValidPersistentActorConfig(config, out var invalidReason))
            {
                if (this.registry.GetByRuntimeId(config.RuntimeId) is { } invalidActor)
                    this.SetLifecycle(invalidActor, ActorLifecycleState.Despawned, $"Ignored invalid ActorConfig: {invalidReason}");
                continue;
            }

            this.NormalizeCurrentActorConfig(config);
            var actor = this.EnsureShell(config, this.database.GetNpcById(config.SourceNpcPresetId));
            if (actor.LifecycleState is ActorLifecycleState.Ready or ActorLifecycleState.Spawning or ActorLifecycleState.BindingRuntime or ActorLifecycleState.SpawnFailed)
                continue;

            this.TrySpawnConfigNow(config, actor, "RefreshActors");
        }

        this.LastMessage = $"Refreshed current territory actors. territory={territory}.";
    }

    public void CleanupActorsForMissingNpcs()
    {
        var before = this.database.ActorConfigs.Count;
        var seenConfigIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRuntimeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();

        foreach (var config in this.database.ActorConfigs.ToList())
        {
            var valid = this.IsValidPersistentActorConfig(config, out var reason);
            if (valid && !seenConfigIds.Add(config.ConfigId))
            {
                valid = false;
                reason = "duplicate configId";
            }

            if (valid && !seenRuntimeIds.Add(config.RuntimeId))
            {
                valid = false;
                reason = "duplicate runtimeId";
            }

            if (!valid)
            {
                removed.Add($"{ShortId(config.ConfigId)}:{reason}");
                if (this.registry.GetByRuntimeId(config.RuntimeId) is { } actor)
                    this.DespawnRuntimeOnly(actor, $"invalid/legacy ActorConfig cleanup: {reason}");
                this.registry.Remove(config.RuntimeId);
                this.database.ActorConfigs.Remove(config);
                continue;
            }

            this.NormalizeCurrentActorConfig(config);
        }

        if (removed.Count > 0 || before != this.database.ActorConfigs.Count)
            this.database.Save();

        this.LastMessage = removed.Count == 0
            ? "ActorConfig cleanup complete; no invalid legacy entries found."
            : $"ActorConfig cleanup removed {removed.Count} invalid/legacy entries.";
    }

    public RuntimeActorInstance? GetActor(string runtimeId) => this.registry.GetByRuntimeId(runtimeId);

    public int GetActorCountForNpc(string npcId)
        => this.database.ActorConfigs.Count(config => string.Equals(config.SourceNpcPresetId, npcId, StringComparison.OrdinalIgnoreCase));

    public CustomNpc? FindInteractableNpc(IEnumerable<CustomNpc> npcs, Vector3? playerPosition, uint territoryType)
    {
        if (!playerPosition.HasValue)
            return null;

        var npcById = npcs.ToDictionary(npc => npc.Id, StringComparer.OrdinalIgnoreCase);
        var territory = (ushort)Math.Clamp((int)territoryType, 0, ushort.MaxValue);
        return this.database.ActorConfigs
            .Where(config => config.TerritoryType == territory && npcById.ContainsKey(config.SourceNpcPresetId))
            .Select(config => new { Config = config, Npc = npcById[config.SourceNpcPresetId], Distance = Vector3.Distance(playerPosition.Value, ToVector3(config.WorldPosition)) })
            .Where(item => item.Distance <= MathF.Max(0.1f, item.Npc.InteractRadius))
            .OrderBy(item => item.Distance)
            .Select(item => item.Npc)
            .FirstOrDefault();
    }

    public void Update()
    {
        var runtimeActors = this.registry.GetAll();
        this.validityMonitorService.Update(runtimeActors);
        var territory = this.clientState.TerritoryType;
        var gpose = this.validityMonitorService.CurrentIsGposing;
        var sceneReady = this.IsActorSceneReady(out var sceneReason);
        this.UpdateRuntimeDebugState(gpose, sceneReason);

        if (territory != this.lastTerritoryType)
        {
            var reason = $"territory changed {this.lastTerritoryType}->{territory}";
            this.lastTerritoryType = territory;
            this.ClearRuntimeForRebuild(reason);
            this.ScheduleRebuild(reason, SceneRebuildDelayTicks);
        }

        if (gpose && !this.lastObservedGpose)
        {
            this.ClearRuntimeForRebuild("GPose entered; runtime actor handles invalidated");
            this.ScheduleRebuild("GPose entered; rebuilding actors for GPose scene", SceneRebuildDelayTicks);
        }
        else if (!gpose && this.lastObservedGpose)
        {
            this.ClearRuntimeForRebuild("GPose exited; runtime actor handles invalidated");
            this.ScheduleRebuild("GPose exited; waiting for actor scene stability", GposeExitRebuildDelayTicks);
        }

        if (sceneReady && !this.lastSceneReady && this.HasCurrentTerritoryConfigs())
            this.ScheduleRebuild("actor scene became ready", SceneRebuildDelayTicks);

        if (!this.initialActorRefreshQueued && sceneReady && this.HasCurrentTerritoryConfigs())
        {
            this.initialActorRefreshQueued = true;
            this.ScheduleRebuild("initial actor scene ready", InitialRebuildDelayTicks);
        }

        if (this.HasPendingCurrentTerritorySpawnWork() && this.scheduledRebuildDelayTicks < 0)
            this.ScheduleRebuild(sceneReady ? "pending actor config ready to spawn" : $"pending actor config waiting: {sceneReason}", 0);

        this.lastObservedGpose = gpose;
        this.lastSceneReady = sceneReady;

        if (!sceneReady)
        {
            this.MaterializeCurrentTerritoryShells(sceneReason);
            this.LastMessage = $"Actor rebuild waiting: {sceneReason}";
            return;
        }

        this.UpdateScheduledRebuild();

        foreach (var actor in this.registry.GetAll().ToList())
        {
            if (actor.LifecycleState == ActorLifecycleState.BindingRuntime || actor.LifecycleState == ActorLifecycleState.Spawning)
                this.UpdateBinding(actor);
        }

        this.appearanceApplyQueue.Update();
        var readyActors = this.registry.GetAll().Where(this.IsRuntimeReady).ToList();
        this.FlushPendingTransforms(readyActors);
        this.UpdateActorBehaviorLoops(this.registry.GetAll().ToList());
        this.actionSequenceService.Update(readyActors);
        this.lookAtService.Update(readyActors, this.database);
    }

    private void FlushPendingTransforms(IEnumerable<RuntimeActorInstance> readyActors)
    {
        var now = DateTime.UtcNow;
        foreach (var actor in readyActors.Where(actor => actor.HasPendingTransformApply).ToList())
        {
            if ((now - actor.LastPendingTransformAttemptAt).TotalMilliseconds < PendingTransformRetryIntervalMilliseconds)
                continue;

            actor.LastPendingTransformAttemptAt = now;
            var position = actor.PendingTransformPosition;
            var rotation = actor.PendingTransformRotationEuler;
            var scale = actor.PendingTransformScale == Vector3.Zero ? Vector3.One : actor.PendingTransformScale;
            if (!this.ApplyActorTransformInternal(actor.RuntimeId, position, rotation, scale, fromPendingFlush: true) && !this.IsRuntimeReady(actor))
                actor.HasPendingTransformApply = true;
        }
    }

    private void UpdateActorBehaviorLoops(IReadOnlyList<RuntimeActorInstance> actors)
    {
        var now = DateTime.UtcNow;
        foreach (var actor in actors)
        {
            if (!this.IsRuntimeReady(actor))
            {
                if (actor.ExpressionBlendLoopEnabled)
                    actor.LastExpressionError = "Expression blend loop stopped: Actor not ready.";
                if (actor.LipTalkLoopEnabled)
                    actor.LastLipTalkError = "Lip talk loop stopped: Actor not ready.";
                actor.ExpressionBlendLoopEnabled = false;
                actor.LipTalkLoopEnabled = false;
                continue;
            }

            if (actor.ExpressionBlendLoopEnabled)
            {
                var interval = Math.Max(0.05f, actor.ExpressionBlendLoopIntervalSeconds);
                if ((now - actor.LastExpressionBlendLoopAt).TotalSeconds >= interval)
                {
                    actor.LastExpressionBlendLoopAt = now;
                    if (!this.ApplyExpressionBlendInternal(actor, actor.CurrentExpressionId, actor.CurrentExpressionLayer, out var reason))
                    {
                        actor.ExpressionBlendLoopEnabled = false;
                        actor.LastExpressionError = $"Expression blend loop stopped: {reason}";
                    }
                }
            }

            if (actor.LipTalkLoopEnabled)
            {
                var interval = Math.Max(0.05f, actor.LipTalkLoopIntervalSeconds);
                if ((now - actor.LastLipTalkLoopAt).TotalSeconds >= interval)
                {
                    actor.LastLipTalkLoopAt = now;
                    if (!this.ApplyLipTalkInternal(actor, actor.CurrentLipTalkId, out var reason))
                    {
                        actor.LipTalkLoopEnabled = false;
                        actor.LastLipTalkError = $"Lip talk loop stopped: {reason}";
                    }
                }
            }
        }
    }

    private void ScheduleRebuild(string reason, int delayTicks)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "actor rebuild requested";

        this.scheduledRebuildReason = reason;
        this.scheduledRebuildDelayTicks = Math.Max(0, delayTicks);
        foreach (var actor in this.registry.GetAll())
            actor.LastRebuildReason = reason;
        this.LastMessage = $"Actor rebuild scheduled in {this.scheduledRebuildDelayTicks} ticks: {reason}";
    }

    private void UpdateScheduledRebuild()
    {
        if (this.scheduledRebuildDelayTicks < 0)
            return;

        if (this.scheduledRebuildDelayTicks > 0)
        {
            this.scheduledRebuildDelayTicks--;
            return;
        }

        var reason = this.scheduledRebuildReason;
        this.scheduledRebuildDelayTicks = -1;
        this.scheduledRebuildReason = string.Empty;
        this.lastRebuildAttemptAt = DateTime.UtcNow;
        this.RefreshActors();
        foreach (var actor in this.registry.GetAll())
            actor.LastRebuildReason = reason;
        this.LastMessage = $"Actor rebuild completed/requested: {reason}";
    }

    private void ClearRuntimeForRebuild(string reason)
    {
        foreach (var actor in this.registry.GetAll().ToList())
            this.DespawnRuntimeOnly(actor, reason);
        this.registry.Clear();
        this.MaterializeCurrentTerritoryShells(reason);
    }

    private void MaterializeCurrentTerritoryShells(string reason)
    {
        var territory = CurrentTerritory(this.clientState);
        foreach (var config in this.database.ActorConfigs.Where(config => config.TerritoryType == territory).OrderBy(config => config.SortOrder).ThenBy(config => config.SpawnSequence))
        {
            if (!this.IsValidPersistentActorConfig(config, out _))
                continue;

            var actor = this.EnsureShell(config, this.database.GetNpcById(config.SourceNpcPresetId));
            actor.LastRebuildReason = reason;
            actor.LastSceneReadyState = reason;
            actor.LastGposeState = this.validityMonitorService.CurrentIsGposing ? "GPose" : "Normal";
            if (actor.LifecycleState != ActorLifecycleState.Ready && actor.LifecycleState != ActorLifecycleState.Spawning && actor.LifecycleState != ActorLifecycleState.BindingRuntime && actor.LifecycleState != ActorLifecycleState.SpawnFailed)
                this.SetLifecycle(actor, ActorLifecycleState.SpawnPending, reason);
        }
    }

    private bool HasCurrentTerritoryConfigs()
    {
        var territory = CurrentTerritory(this.clientState);
        return this.database.ActorConfigs.Any(config => config.AutoSpawn && config.TerritoryType == territory && this.IsValidPersistentActorConfig(config, out _));
    }

    private bool HasPendingCurrentTerritorySpawnWork()
    {
        var territory = CurrentTerritory(this.clientState);
        foreach (var config in this.database.ActorConfigs.Where(config => config.AutoSpawn && config.TerritoryType == territory))
        {
            if (!this.IsValidPersistentActorConfig(config, out _))
                continue;

            var actor = this.registry.GetByRuntimeId(config.RuntimeId);
            if (actor == null)
                return true;

            if (actor.LifecycleState is ActorLifecycleState.ConfigOnly or ActorLifecycleState.SpawnPending or ActorLifecycleState.Despawned)
                return true;
        }

        return false;
    }

    private int CountPendingSpawnWork()
    {
        var territory = CurrentTerritory(this.clientState);
        var count = 0;
        foreach (var config in this.database.ActorConfigs.Where(config => config.AutoSpawn && config.TerritoryType == territory))
        {
            if (!this.IsValidPersistentActorConfig(config, out _))
                continue;

            var actor = this.registry.GetByRuntimeId(config.RuntimeId);
            if (actor == null || actor.LifecycleState is ActorLifecycleState.ConfigOnly or ActorLifecycleState.SpawnPending or ActorLifecycleState.Despawned)
                count++;
        }

        return count;
    }

    private bool IsActorSceneReady(out string reason)
    {
        if (!this.CanSpawnRealActor)
        {
            reason = $"native actor spawn unavailable: {this.BrioAssemblyStatus}";
            return false;
        }

        if (!this.TryReadLocalPlayerPosition(out _, out var localPlayerReason))
        {
            if (this.validityMonitorService.CurrentIsGposing)
            {
                reason = $"ready (GPose; local player unavailable but rebuild uses saved ActorConfig transform: {localPlayerReason})";
                return true;
            }

            reason = $"local player not available: {localPlayerReason}";
            return false;
        }

        reason = this.validityMonitorService.CurrentIsGposing ? "ready (GPose)" : "ready";
        return true;
    }

    private void UpdateRuntimeDebugState(bool gpose, string sceneReason)
    {
        foreach (var actor in this.registry.GetAll())
        {
            actor.LastGposeState = gpose ? "GPose" : "Normal";
            actor.LastSceneReadyState = sceneReason;
        }
    }

    private RuntimeActorInstance? TrySpawnConfigNow(PersistentActorConfig config, RuntimeActorInstance shell, string reason)
    {
        if (!this.IsValidPersistentActorConfig(config, out var invalidReason))
        {
            this.SetLifecycle(shell, ActorLifecycleState.Despawned, $"Invalid ActorConfig ignored: {invalidReason}");
            return shell;
        }

        this.NormalizeCurrentActorConfig(config);

        shell.LastSpawnReason = reason;
        shell.LastRebuildReason = this.scheduledRebuildReason;
        shell.LastGposeState = this.validityMonitorService.CurrentIsGposing ? "GPose" : "Normal";

        if (!this.IsActorSceneReady(out var sceneReason))
        {
            shell.LastSceneReadyState = sceneReason;
            this.SetLifecycle(shell, ActorLifecycleState.SpawnPending, $"Scene not ready: {sceneReason}");
            return shell;
        }

        var currentTerritory = CurrentTerritory(this.clientState);
        if (config.TerritoryType != currentTerritory)
        {
            this.SetLifecycle(shell, ActorLifecycleState.Despawned, $"Wrong territory. config={config.TerritoryType}, current={currentTerritory}.");
            return shell;
        }

        if (!this.CanSpawnRealActor)
        {
            this.SetLifecycle(shell, ActorLifecycleState.Failed, $"Native actor spawn is unavailable. {this.BrioAssemblyStatus}");
            return shell;
        }

        config.RuntimeId = string.IsNullOrWhiteSpace(config.RuntimeId) ? Guid.NewGuid().ToString("N") : config.RuntimeId;
        if (shell.CharacterObject != null)
            this.DespawnRuntimeOnly(shell, "respawn existing runtime handle");

        shell.SpawnAttemptCount++;
        shell.LastSpawnAttemptAt = DateTime.UtcNow;
        shell.SpawnRequestId = Guid.NewGuid().ToString("N");
        this.SetLifecycle(shell, ActorLifecycleState.Spawning, reason);
        this.registry.Add(shell);

        try
        {
            var position = ToVector3(config.WorldPosition);
            var rotation = ToVector3(config.WorldRotationEuler);
            if (!this.brioAssemblyBridge.TrySpawnActor(config, out var spawned, out var spawnReason, position, rotation.Y))
            {
                shell.LastError = spawnReason;
                shell.ObjectIndex = "unavailable";
                shell.Address = "unavailable";
                this.SetLifecycle(shell, ActorLifecycleState.SpawnFailed, spawnReason);
                this.LastMessage = spawnReason;
                return shell;
            }

            this.PopulateActorFromConfig(spawned, config, this.database.GetNpcById(config.SourceNpcPresetId));
            spawned.SpawnSource = "ARR-style native ActorConfig pipeline";
            spawned.LastSpawnReason = reason;
            spawned.LastRebuildReason = this.scheduledRebuildReason;
            spawned.LastSceneReadyState = sceneReason;
            spawned.LastGposeState = this.validityMonitorService.CurrentIsGposing ? "GPose" : "Normal";
            spawned.SpawnRequestId = shell.SpawnRequestId;
            spawned.SpawnAttemptCount = shell.SpawnAttemptCount;
            spawned.LastSpawnAttemptAt = shell.LastSpawnAttemptAt;
            this.registry.Add(spawned);
            this.SetLifecycle(spawned, ActorLifecycleState.Spawned, spawnReason);
            this.SetLifecycle(spawned, ActorLifecycleState.BindingRuntime, "Native actor spawned; binding post-spawn pipeline.");
            this.UpdateBinding(spawned);
            return spawned;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Actor direct spawn failed. ConfigId={ConfigId}, RuntimeId={RuntimeId}", config.ConfigId, config.RuntimeId);
            shell.LastError = ex.Message;
            this.SetLifecycle(shell, ActorLifecycleState.SpawnFailed, ex.Message);
            this.LastMessage = ex.Message;
            return shell;
        }
    }

    private void UpdateBinding(RuntimeActorInstance actor)
    {
        if (actor.CharacterObject != null && !actor.HasBoundDrawObject)
        {
            actor.BindingStartedAt ??= DateTime.UtcNow;
            actor.BindingWaitTicks++;
            if (!this.brioAssemblyBridge.TryEnsureNativeActorDraw(actor, out var drawReason))
            {
                this.SetLifecycle(actor, ActorLifecycleState.BindingRuntime, $"{drawReason} tick {actor.BindingWaitTicks}/{RuntimeBindTimeoutTicks}.");
                if (actor.BindingWaitTicks >= RuntimeBindTimeoutTicks)
                    this.SetLifecycle(actor, ActorLifecycleState.Failed, $"Runtime actor draw binding timed out: {drawReason}");
                return;
            }

            actor.PostSpawnPipelineStatus = drawReason;
        }

        if (this.IsRuntimeReady(actor))
        {
            actor.HasBoundNativeActor = true;
            actor.HasBoundDrawObject = true;
            actor.LastRuntimeBindAt = DateTime.UtcNow;
            this.SetLifecycle(actor, ActorLifecycleState.BindingRuntime, "Runtime actor bound; applying post-spawn appearance/transform/behavior.");
            if (this.ApplyConfigToReadyActor(actor))
                this.SetLifecycle(actor, ActorLifecycleState.Ready, actor.RuntimeTransformApplied ? "Runtime actor bound, appearance applied, transform applied, behavior restored." : "Runtime actor bound; transform has an error but actor remains usable.");
            return;
        }

        actor.BindingStartedAt ??= DateTime.UtcNow;
        actor.BindingWaitTicks++;
        this.SetLifecycle(actor, ActorLifecycleState.BindingRuntime, $"Waiting for runtime bind tick {actor.BindingWaitTicks}/{RuntimeBindTimeoutTicks}.");
        if (actor.BindingWaitTicks >= RuntimeBindTimeoutTicks)
            this.SetLifecycle(actor, ActorLifecycleState.Failed, "Runtime actor binding timed out.");
    }

    private bool ApplyConfigToReadyActor(RuntimeActorInstance actor)
    {
        var config = this.GetConfig(actor.RuntimeId);
        if (config == null)
        {
            this.SetLifecycle(actor, ActorLifecycleState.Failed, "PersistentActorConfig missing during post-spawn apply.");
            return false;
        }

        var appearanceOk = this.ApplyNpcAppearance(actor.RuntimeId);
        if (!appearanceOk)
        {
            var reason = $"AppearanceFailed: {actor.LastAppearanceError}";
            this.SetLifecycle(actor, ActorLifecycleState.Failed, reason);
            return false;
        }

        var transformOk = this.ApplyActorTransform(actor.RuntimeId, ToVector3(config.WorldPosition), ToVector3(config.WorldRotationEuler), ToVector3(config.WorldScale, Vector3.One));
        if (!transformOk)
        {
            actor.RuntimeTransformApplied = false;
            actor.PostSpawnPipelineStatus = $"TransformFailed but actor remains ready: {actor.LastTransformError}";
        }

        if (actor.AnimationEnabled && actor.CurrentAnimationId != 0)
            this.PlayAnimation(actor.RuntimeId, actor.CurrentAnimationId);
        this.PersistBehavior(actor);
        return true;
    }

    private bool ApplyNpcAppearance(string runtimeId)
    {
        var actor = this.registry.GetByRuntimeId(runtimeId);
        if (actor == null)
            return false;

        var config = this.GetConfig(actor.RuntimeId);
        if (config == null)
        {
            actor.LastAppearanceError = "ActorConfig no longer exists.";
            return false;
        }

        if (!this.IsValidPersistentActorConfig(config, out var invalidReason))
        {
            actor.LastAppearanceError = $"Invalid ActorConfig: {invalidReason}";
            return false;
        }

        this.NormalizeCurrentActorConfig(config);

        if (!this.IsRuntimeReady(actor))
        {
            actor.LastAppearanceError = "Runtime actor is not Ready; appearance will apply after bind.";
            return false;
        }

        var penumbraOk = this.penumbraIpc.ApplyCollection(config, actor, out var penumbraReason);
        var appearanceOk = this.appearanceApplyService.ApplyActorConfigAppearance(config, actor);
        this.nameplateService.TrySetActorName(actor, actor.DesiredDisplayName);
        actor.RuntimeAppearanceApplied = appearanceOk;
        actor.LastSuccessfulAppearanceApplyAt = appearanceOk ? DateTime.UtcNow : actor.LastSuccessfulAppearanceApplyAt;
        actor.LastAppearanceApplyResult = $"appearance={appearanceOk}; penumbra={penumbraOk}; {penumbraReason}; {actor.LastAppearanceApplyResult}";
        this.LastMessage = actor.LastAppearanceApplyResult;
        return appearanceOk;
    }

    private RuntimeActorInstance EnsureShell(PersistentActorConfig config, CustomNpc? npc)
    {
        if (this.registry.GetByRuntimeId(config.RuntimeId) is { } existing)
            return existing;

        var shell = new RuntimeActorInstance();
        this.PopulateActorFromConfig(shell, config, npc);
        this.SetLifecycle(shell, config.TerritoryType == CurrentTerritory(this.clientState) ? ActorLifecycleState.ConfigOnly : ActorLifecycleState.Despawned, "Persistent ActorConfig shell.");
        this.registry.Add(shell);
        return shell;
    }

    private void PopulateActorFromConfig(RuntimeActorInstance actor, PersistentActorConfig config, CustomNpc? npc)
    {
        var position = ToVector3(config.WorldPosition);
        var rotation = ToVector3(config.WorldRotationEuler);
        var scale = NormalizeScale(ToVector3(config.WorldScale, Vector3.One));
        actor.RuntimeId = config.RuntimeId;
        actor.ConfigId = config.ConfigId;
        actor.NpcId = config.SourceNpcPresetId;
        actor.TemplateNpcId = config.SourceNpcPresetId;
        actor.NpcName = string.IsNullOrWhiteSpace(config.NpcNameSnapshot) ? config.DisplayName : config.NpcNameSnapshot;
        actor.DisplayName = BuildDisplayName(config, npc);
        actor.DesiredDisplayName = actor.DisplayName;
        actor.ExpectedName = actor.DisplayName;
        actor.SpawnedTerritoryType = config.TerritoryType;
        actor.TerritoryId = config.TerritoryType;
        actor.SpawnedTerritoryName = config.TerritoryName;
        actor.SortOrder = config.SortOrder;
        actor.SpawnSequence = config.SpawnSequence;
        actor.SpawnKind = config.SpawnKind == ActorSpawnKind.Unknown ? (config.Appearance?.SpawnKind ?? ActorSpawnKind.Unknown) : config.SpawnKind;
        if (actor.SpawnKind == ActorSpawnKind.Unknown)
            actor.SpawnKind = config.Appearance?.IsHumanoid == true ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman;
        actor.SourceActorKind = string.IsNullOrWhiteSpace(config.SourceActorKind) ? config.Appearance?.SourceActorKind ?? string.Empty : config.SourceActorKind;
        actor.SpawnKindStatus = $"config spawnKind={actor.SpawnKind}, sourceActorKind={actor.SourceActorKind}, objectKind={config.Appearance?.ObjectKind}";
        actor.SpawnPosition = position;
        actor.SpawnRotationEuler = rotation;
        actor.SpawnScale = scale;
        actor.TransformEditPosition = position;
        actor.TransformEditRotationEuler = rotation;
        actor.TransformEditScale = scale;
        actor.LastKnownPosition = position;
        actor.LastKnownRotationEuler = rotation;
        actor.LastKnownRotation = Quaternion.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
        actor.LastKnownScale = scale;
        actor.HasSavedTransform = true;
        actor.DefaultAnimationId = config.DefaultAnimationId;
        actor.CurrentAnimationId = config.CurrentAnimationId != 0 ? config.CurrentAnimationId : config.DefaultAnimationId;
        actor.AnimationEnabled = config.AnimationEnabled || config.AutoPlayDefaultAnimation;
        actor.LookAtPlayerEnabled = config.LookAtPlayerEnabled;
        actor.LookAtRadius = config.LookAtRadius;
        actor.LookAtMode = config.LookAtPlayerEnabled ? NpcLookAtMode.NativeLookAt : NpcLookAtMode.None;
        actor.EnableActionSequence = config.EnableActionSequence;
        actor.ActionSequenceLoop = config.ActionSequenceLoop;
        actor.ActionSequenceLoopDelay = config.ActionSequenceLoopDelay;
        actor.ActionSequence = config.ActionSequence.Select(CloneStep).ToList();
        actor.PenumbraMode = config.PenumbraMode;
        actor.PenumbraCollectionId = config.PenumbraCollectionId;
        actor.PenumbraCollectionNameCache = config.PenumbraCollectionNameCache;
        var appearance = config.Appearance ?? new ActorAppearanceData();
        actor.AppearanceSourceType = appearance.SourceKind.ToString();
        actor.GlamourerDesignId = appearance.SourceKind == ActorAppearanceSourceKind.GlamourerDesign ? appearance.SourceId : string.Empty;
        actor.GlamourerDesignName = appearance.SourceName;
        actor.GlamourerDesignPath = appearance.SourcePath;
        actor.GlamourerIpcAvailable = false;
        actor.LastAppearancePresetSummary = appearance.Summary;
        actor.SourceModelCharaId = appearance.ModelCharaId;
        actor.ModelCharaOverrideId = appearance.ModelCharaOverrideId;
        actor.EditingModelCharaId = EffectiveModelCharaId(appearance);
        actor.LastAppliedModelCharaId = actor.EditingModelCharaId;
        actor.PostSpawnBehaviorReady = true;
    }

    private PersistentActorConfig CreateConfig(CustomNpc npc, string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale, int sortOrder)
    {
        var territory = CurrentTerritory(this.clientState);
        var appearance = this.appearanceLocalizer.FromNpcTemplate(npc);
        var spawnKind = appearance.SpawnKind == ActorSpawnKind.Unknown
            ? appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman
            : appearance.SpawnKind;
        appearance.SpawnKind = spawnKind;
        appearance.IsHumanoid = spawnKind == ActorSpawnKind.Character;
        appearance.ModelCharaOverrideId = 0;
        return new PersistentActorConfig
        {
            ConfigId = Guid.NewGuid().ToString("N"),
            RuntimeId = runtimeId,
            SourceNpcPresetId = npc.Id,
            NpcNameSnapshot = npc.Name,
            DisplayName = BuildDisplayName(npc),
            Appearance = appearance,
            SpawnKind = spawnKind,
            SourceActorKind = appearance.SourceActorKind,
            TerritoryType = territory,
            TerritoryName = $"Territory {territory}",
            WorldPosition = ToData(position),
            WorldRotationEuler = ToData(rotationEuler),
            WorldScale = ToData(NormalizeScale(scale)),
            DefaultAnimationId = npc.DefaultAnimationId,
            CurrentAnimationId = npc.DefaultAnimationId,
            AnimationEnabled = npc.AutoPlayDefaultAnimation && npc.DefaultAnimationId != 0,
            AutoPlayDefaultAnimation = npc.AutoPlayDefaultAnimation,
            LookAtPlayerEnabled = npc.LookAtPlayerEnabled,
            LookAtRadius = npc.LookAtRadius,
            PenumbraMode = npc.PenumbraMode,
            PenumbraCollectionId = npc.PenumbraCollectionId,
            PenumbraCollectionNameCache = npc.PenumbraCollectionNameCache,
            AutoSpawn = true,
            SortOrder = sortOrder,
            SpawnSequence = ++this.nextRuntimeActorSequence,
        };
    }

    private PersistentActorConfig CreateConfig(string displayName, ActorAppearanceData appearance, string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale, int sortOrder)
    {
        var territory = CurrentTerritory(this.clientState);
        var safeName = string.IsNullOrWhiteSpace(displayName) ? "Actor" : displayName.Trim();
        var spawnKind = appearance.SpawnKind == ActorSpawnKind.Unknown
            ? appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman
            : appearance.SpawnKind;
        appearance.SpawnKind = spawnKind;
        appearance.IsHumanoid = spawnKind == ActorSpawnKind.Character;
        return new PersistentActorConfig
        {
            ConfigId = Guid.NewGuid().ToString("N"),
            RuntimeId = runtimeId,
            SourceNpcPresetId = string.Empty,
            NpcNameSnapshot = safeName,
            DisplayName = safeName,
            Appearance = appearance,
            SpawnKind = spawnKind,
            SourceActorKind = appearance.SourceActorKind,
            TerritoryType = territory,
            TerritoryName = $"Territory {territory}",
            WorldPosition = ToData(position),
            WorldRotationEuler = ToData(rotationEuler),
            WorldScale = ToData(NormalizeScale(scale)),
            AutoSpawn = true,
            SortOrder = sortOrder,
            SpawnSequence = ++this.nextRuntimeActorSequence,
        };
    }

    private void PersistBehavior(RuntimeActorInstance actor)
    {
        var config = this.GetConfig(actor.RuntimeId);
        if (config == null)
            return;

        config.CurrentAnimationId = actor.CurrentAnimationId;
        config.AnimationEnabled = actor.AnimationEnabled;
        config.LookAtPlayerEnabled = actor.LookAtPlayerEnabled;
        config.LookAtRadius = actor.LookAtRadius;
        config.EnableActionSequence = actor.EnableActionSequence;
        config.ActionSequenceLoop = actor.ActionSequenceLoop;
        config.ActionSequenceLoopDelay = actor.ActionSequenceLoopDelay;
        config.ActionSequence = actor.ActionSequence.Select(CloneStep).ToList();
        config.PenumbraMode = actor.PenumbraMode;
        config.PenumbraCollectionId = actor.PenumbraCollectionId;
        config.PenumbraCollectionNameCache = actor.PenumbraCollectionNameCache;
        this.database.Save();
    }

    private void SaveConfigTransform(PersistentActorConfig? config, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        if (config == null)
            return;

        config.WorldPosition = ToData(position);
        config.WorldRotationEuler = ToData(rotationEuler);
        config.WorldScale = ToData(NormalizeScale(scale));
        this.database.Save();
    }

    private PersistentActorConfig? GetConfig(string runtimeId)
        => this.database.ActorConfigs.FirstOrDefault(config => string.Equals(config.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    private void DespawnRuntimeOnly(RuntimeActorInstance actor, string reason)
    {
        try
        {
            this.penumbraIpc.CleanupActorAssignment(actor);
            if (actor.CharacterObject != null)
                this.brioAssemblyBridge.TryDespawnActor(actor, out _);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Runtime actor despawn failed. RuntimeId={RuntimeId}", actor.RuntimeId);
        }

        actor.CharacterObject = null;
        actor.IsValid = false;
        actor.IsReady = false;
        actor.IsStale = true;
        actor.HasBoundNativeActor = false;
        actor.HasBoundDrawObject = false;
        actor.HasPendingTransformApply = false;
        actor.ExpressionBlendLoopEnabled = false;
        actor.LipTalkLoopEnabled = false;
        this.SetLifecycle(actor, ActorLifecycleState.Despawned, reason);
    }

    private bool IsRuntimeReady(RuntimeActorInstance actor)
    {
        if (actor.CharacterObject != null && !actor.IsStale)
            actor.IsValid = true;
        return actor.CharacterObject != null && actor.IsValid && !actor.IsStale;
    }

    private void SetLifecycle(RuntimeActorInstance actor, ActorLifecycleState state, string status)
    {
        if (actor.LifecycleState != state)
        {
            actor.LifecycleState = state;
            actor.LifecycleStateChangedAt = DateTime.UtcNow;
        }

        actor.PostSpawnPipelineState = state.ToString();
        actor.PostSpawnPipelineStatus = status;
        actor.LastError = state is ActorLifecycleState.Failed or ActorLifecycleState.SpawnFailed ? status : actor.LastError;
        actor.IsReady = state == ActorLifecycleState.Ready;
        if (state == ActorLifecycleState.Spawned)
        {
            actor.IsValid = true;
            actor.IsStale = false;
            actor.HasBoundNativeActor = true;
        }
        if (state == ActorLifecycleState.Ready)
        {
            actor.IsValid = true;
            actor.IsStale = false;
            actor.HasBoundNativeActor = true;
            actor.HasBoundDrawObject = true;
        }
    }

    private Vector3 GetDefaultSpawnPosition(CustomNpc npc)
    {
        var offset = ToVector3(npc.DefaultSpawnOffset);
        return this.TryReadLocalPlayerPosition(out var playerPosition) ? playerPosition + offset : offset;
    }

    private Vector3 GetDefaultActorSpawnPosition()
        => this.TryReadLocalPlayerPosition(out var playerPosition) ? playerPosition + new Vector3(0f, 0f, 2f) : new Vector3(0f, 0f, 2f);

    private bool TryReadLocalPlayerPosition(out Vector3 position)
    {
        return this.TryReadLocalPlayerPosition(out position, out _);
    }

    private bool TryReadLocalPlayerPosition(out Vector3 position, out string reason)
    {
        position = Vector3.Zero;
        if (this.brioAssemblyBridge.TryReadLocalPlayerPosition(out position, out reason))
            return true;

        var objectTableReason = reason;
        var player = this.clientState.GetType().GetProperty("LocalPlayer")?.GetValue(this.clientState);
        if (player == null)
        {
            reason = $"{objectTableReason}; ClientState.LocalPlayer is null.";
            return false;
        }

        var value = player.GetType().GetProperty("Position")?.GetValue(player);
        if (value is not Vector3 playerPosition)
        {
            reason = $"{objectTableReason}; ClientState.LocalPlayer.Position is unavailable.";
            return false;
        }

        if (!float.IsFinite(playerPosition.X) || !float.IsFinite(playerPosition.Y) || !float.IsFinite(playerPosition.Z))
        {
            reason = $"{objectTableReason}; ClientState.LocalPlayer.Position is invalid: {playerPosition}.";
            return false;
        }

        position = playerPosition;
        reason = $"{objectTableReason}; fallback ClientState.LocalPlayer position available.";
        return true;
    }

    private int GetNextSortOrder(string npcId)
    {
        var existing = this.database.ActorConfigs
            .Where(config => string.Equals(config.SourceNpcPresetId, npcId, StringComparison.OrdinalIgnoreCase))
            .Select(config => config.SortOrder == int.MaxValue ? 0 : config.SortOrder)
            .DefaultIfEmpty(0)
            .Max();
        return existing + 1;
    }

    private int GetNextSortOrderForActor()
    {
        var existing = this.database.ActorConfigs
            .Select(config => config.SortOrder == int.MaxValue ? 0 : config.SortOrder)
            .DefaultIfEmpty(0)
            .Max();
        return existing + 1;
    }

    private void NormalizeCurrentActorConfig(PersistentActorConfig config)
    {
        if (config.SchemaVersion != PersistentActorConfig.CurrentSchemaVersion)
            return;

        config.RuntimeId = string.IsNullOrWhiteSpace(config.RuntimeId) ? Guid.NewGuid().ToString("N") : config.RuntimeId;
        if (string.IsNullOrWhiteSpace(config.DisplayName))
            config.DisplayName = string.IsNullOrWhiteSpace(config.NpcNameSnapshot) ? config.Appearance.SourceName : config.NpcNameSnapshot;
        if (string.IsNullOrWhiteSpace(config.DisplayName))
            config.DisplayName = "Actor";
        var appearance = config.Appearance ?? new ActorAppearanceData();
        var spawnKind = config.SpawnKind != ActorSpawnKind.Unknown
            ? config.SpawnKind
            : appearance.SpawnKind != ActorSpawnKind.Unknown
                ? appearance.SpawnKind
                : appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman;
        config.SpawnKind = spawnKind;
        appearance.SpawnKind = spawnKind;
        appearance.IsHumanoid = spawnKind == ActorSpawnKind.Character;
        if (string.IsNullOrWhiteSpace(config.SourceActorKind))
            config.SourceActorKind = appearance.SourceActorKind;
        config.WorldScale = ToData(NormalizeScale(ToVector3(config.WorldScale, Vector3.One)));
    }

    private static ActorSpawnKind NormalizeSpawnKindForModelOverride(ActorSpawnKind current, ActorAppearanceData appearance, uint modelCharaOverrideId)
    {
        if (modelCharaOverrideId == 0)
            return appearance.SpawnKind == ActorSpawnKind.Unknown
                ? appearance.IsHumanoid ? ActorSpawnKind.Character : ActorSpawnKind.Demihuman
                : appearance.SpawnKind;

        return current switch
        {
            ActorSpawnKind.Mount or ActorSpawnKind.Minion => current,
            _ => ActorSpawnKind.Demihuman,
        };
    }

    private static uint EffectiveModelCharaId(ActorAppearanceData appearance)
        => appearance.ModelCharaOverrideId != 0 ? appearance.ModelCharaOverrideId : appearance.ModelCharaId;

    private bool IsValidPersistentActorConfig(PersistentActorConfig config, out string reason)
    {
        if (config.SchemaVersion != PersistentActorConfig.CurrentSchemaVersion)
        {
            reason = $"schema {config.SchemaVersion} != {PersistentActorConfig.CurrentSchemaVersion}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.ConfigId) || string.IsNullOrWhiteSpace(config.RuntimeId))
        {
            reason = "missing configId/runtimeId";
            return false;
        }

        if (LooksLikeLegacyNpcConfig(config))
        {
            reason = "legacy NPC-management ActorConfig";
            return false;
        }

        var appearance = config.Appearance;
        if (appearance == null)
        {
            reason = "missing appearance snapshot";
            return false;
        }

        if (!IsAllowedActorAppearanceSource(appearance.SourceKind))
        {
            reason = $"unsupported source {appearance.SourceKind}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(appearance.SourceId))
        {
            reason = "missing source id";
            return false;
        }

        var position = ToVector3(config.WorldPosition);
        if (!IsFiniteVector(position) || position == Vector3.Zero)
        {
            reason = $"invalid position {position}";
            return false;
        }

        if (config.TerritoryType == 0)
        {
            reason = "missing territory";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsAllowedActorAppearanceSource(ActorAppearanceSourceKind source)
        => source is ActorAppearanceSourceKind.GlamourerDesign
            or ActorAppearanceSourceKind.GlamourerNpc
            or ActorAppearanceSourceKind.ManualSnapshot
            or ActorAppearanceSourceKind.Local;

    private static bool LooksLikeLegacyNpcConfig(PersistentActorConfig config)
    {
        static bool BadText(string value)
            => value.Contains("local-npc", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("本地 NPC", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("鏈湴 NPC", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("-", StringComparison.OrdinalIgnoreCase);

        return BadText(config.SourceNpcPresetId ?? string.Empty) ||
               BadText(config.NpcNameSnapshot ?? string.Empty) ||
               BadText(config.DisplayName ?? string.Empty);
    }

    private static bool IsFiniteVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static ushort CurrentTerritory(IClientState clientState)
        => (ushort)Math.Clamp((int)clientState.TerritoryType, 0, ushort.MaxValue);

    private static Vector3 ToVector3(Vector3Data data, Vector3? fallback = null)
    {
        var value = new Vector3(data.X, data.Y, data.Z);
        return value == Vector3.Zero && fallback.HasValue ? fallback.Value : value;
    }

    private static Vector3Data ToData(Vector3 value)
        => new() { X = value.X, Y = value.Y, Z = value.Z };

    private static Vector3 NormalizeScale(Vector3 scale)
        => new(
            MathF.Max(0.01f, float.IsFinite(scale.X) ? scale.X : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Y) ? scale.Y : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Z) ? scale.Z : 1f));

    private static string BuildDisplayName(CustomNpc? npc)
    {
        if (npc == null)
            return "Actor";

        if (!string.IsNullOrWhiteSpace(npc.NameTemplate) && npc.NameTemplate.Contains("{name}", StringComparison.OrdinalIgnoreCase))
            return npc.NameTemplate.Replace("{name}", npc.Name, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(npc.Name) ? "Actor" : npc.Name;
    }

    private static string BuildDisplayName(PersistentActorConfig config, CustomNpc? npc)
    {
        if (!string.IsNullOrWhiteSpace(config.DisplayName))
            return config.DisplayName;
        if (!string.IsNullOrWhiteSpace(config.NpcNameSnapshot))
            return config.NpcNameSnapshot;
        return BuildDisplayName(npc);
    }

    private static ActorActionSequenceStep CloneStep(ActorActionSequenceStep step)
        => new()
        {
            Id = step.Id,
            Name = step.Name,
            Kind = step.Kind,
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
            DurationSeconds = step.DurationSeconds,
            BubbleText = step.BubbleText,
            BubbleDurationSeconds = step.BubbleDurationSeconds,
            BubbleUseAutoDuration = step.BubbleUseAutoDuration,
            ShowBubbleOnEnter = step.ShowBubbleOnEnter,
            HideBubbleOnDespawn = step.HideBubbleOnDespawn,
            AllowLookAtDuringStep = step.AllowLookAtDuringStep,
            MoveStartWorldOffset = step.MoveStartWorldOffset,
            MoveEndWorldOffset = step.MoveEndWorldOffset,
            MoveUseAbsoluteWorldTarget = step.MoveUseAbsoluteWorldTarget,
            MoveWorldTarget = step.MoveWorldTarget,
            MoveDurationSeconds = step.MoveDurationSeconds,
            MoveInterpolation = step.MoveInterpolation,
            MoveFaceDirection = step.MoveFaceDirection,
            MoveRestoreAtStepEnd = step.MoveRestoreAtStepEnd,
            MoveAffectsRotation = step.MoveAffectsRotation,
            MoveYawDegrees = step.MoveYawDegrees,
            MoveAnimationId = step.MoveAnimationId,
            PlayMoveAnimationOnEnter = step.PlayMoveAnimationOnEnter,
        };

    private static string ShortId(string runtimeId)
        => string.IsNullOrWhiteSpace(runtimeId) ? "none" : runtimeId[..Math.Min(8, runtimeId.Length)];
}

