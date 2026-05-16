using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace LocalQuestReborn.Services;

public sealed unsafe class SceneEditorService
{
    private const int RequiredRestoreStableTicks = 60;
    private readonly RealNpcSpawnService actors;
    private readonly LocalLayoutObjectService localLayoutObjects;
    private readonly LocalLightNativeService localLights;
    private readonly SceneEditorSelectionService selection;
    private readonly IObjectTable objectTable;
    private readonly LayoutProbeService layoutProbe;
    private readonly Func<Vector3?> playerPositionProvider;
    private readonly Configuration configuration;
    private readonly Func<uint> getTerritoryType;
    private readonly Action saveConfiguration;
    private readonly IPluginLog log;
    private readonly List<SceneEditableRef> nativeCache = [];
    private DateTime lastNativeScanUtc = DateTime.MinValue;
    private DateTime lastUndoShortcutUtc = DateTime.MinValue;
    private DateTime lastPersistDirtyUtc = DateTime.MinValue;
    private DateTime lastLocalBgPartSnapshotUtc = DateTime.MinValue;
    private DateTime lastLocalActorSnapshotUtc = DateTime.MinValue;
    private bool persistDirty;
    private bool restorePending;
    private bool restoreRunning;
    private int sceneGeneration;
    private int restoreGeneration;
    private int sceneStableTicks;
    private uint lastSeenTerritory;
    private string restoreReason = string.Empty;
    private int restoreWaitTicks;
    private int restoreStage;
    private int restoreIndex;
    private List<SceneEditorNativeModificationRecord> restoreNativeRecords = [];
    private List<SceneEditorLocalBgPartRecord> restoreBgPartRecords = [];
    private List<SceneEditorLocalActorRecord> restoreActorRecords = [];
    private List<LocalLightInstance> restoreLightRecords = [];
    private LocalBgPartRestoreItem? activeBgPartRestore;
    private bool restoreActorsRequested;

    public SceneEditorService(
        RealNpcSpawnService actors,
        LocalLayoutObjectService localLayoutObjects,
        LocalLightNativeService localLights,
        SceneEditorSelectionService selection,
        IObjectTable objectTable,
        LayoutProbeService layoutProbe,
        Func<Vector3?> playerPositionProvider,
        Configuration configuration,
        Func<uint> getTerritoryType,
        Action saveConfiguration,
        IPluginLog log)
    {
        this.actors = actors;
        this.localLayoutObjects = localLayoutObjects;
        this.localLights = localLights;
        this.selection = selection;
        this.objectTable = objectTable;
        this.layoutProbe = layoutProbe;
        this.playerPositionProvider = playerPositionProvider;
        this.configuration = configuration;
        this.getTerritoryType = getTerritoryType;
        this.saveConfiguration = saveConfiguration;
        this.log = log;
        this.Gizmo = new SceneEditorGizmoService(log);
        this.Undo = new SceneEditorUndoService(log);
    }

    public bool OverlayEnabled { get; set; }

    public bool ShowActors { get; set; } = true;

    public bool ShowBgParts { get; set; } = true;

    public bool ShowLights { get; set; } = true;

    public bool ShowPluginObjects { get; set; } = true;

    public bool ShowNativeObjects { get; set; } = true;

    public bool AllowNativeTransformWrites { get; set; }

    public bool NativeFullLayoutTransformConfirmed { get; set; }

    public bool UnsafeNativeWritesEnabled { get; private set; }

    public bool BgPartCollisionModeEnabled { get; private set; }

    public bool BgPartCollisionModeConfirmed { get; private set; }

    public string LastBgPartCollisionOperation { get; private set; } = "collisionMode=Off; collision operation=Skipped";

    public float MarkerRadius { get; set; } = 5.5f;

    public SceneEditorGizmoService Gizmo { get; }

    public SceneEditorUndoService Undo { get; }

    public SceneEditorGizmoMode GizmoMode => this.Gizmo.Mode;

    public LocalLayoutTransformMode CurrentBgPartTransformMode =>
        this.BgPartCollisionModeEnabled && this.BgPartCollisionModeConfirmed
            ? LocalLayoutTransformMode.FullLayoutWithCollision
            : LocalLayoutTransformMode.VisualOnly;

    public uint TransformGeneration { get; private set; }

    public string LastStatus { get; private set; } = "Scene Editor ready.";

    public string LastHoveredMarkerDebug { get; private set; } = "none";

    public string LastClickedMarkerDebug { get; private set; } = "none";

    public string LastGizmoDebug { get; private set; } = "idle";

    public string LastNativeScanStatus { get; private set; } = "Native scan not started.";

    public string LastQuickActionStatus { get; private set; } = "No SceneEditor quick action yet.";

    public string RestoreStatus { get; private set; } = "Restore queue idle.";

    private List<SceneEditorNativeModificationRecord> NativeRecords => this.configuration.SceneEditorNativeModifications ??= [];

    public IReadOnlyList<SceneEditorNativeModificationRecord> NativeModificationRecords => this.NativeRecords;

    private List<SceneEditorLocalBgPartRecord> LocalBgPartRecords => this.configuration.SceneEditorLocalBgParts ??= [];

    public IReadOnlyList<SceneEditorLocalBgPartRecord> LocalBgPartRestoreRecords => this.LocalBgPartRecords;

    private List<SceneEditorLocalActorRecord> LocalActorRecords => this.configuration.SceneEditorLocalActors ??= [];

    public IReadOnlyList<SceneEditorLocalActorRecord> LocalActorRestoreRecords => this.LocalActorRecords;

    public RuntimeActorInstance? GetLocalActor(string runtimeId)
        => this.actors.Actors.FirstOrDefault(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    public LocalLightInstance? GetLocalLight(string runtimeId)
        => this.localLights.GetById(runtimeId);

    public void SetGizmoMode(SceneEditorGizmoMode mode)
        => this.Gizmo.SetMode(mode);

    public void SetBgPartCollisionMode(bool enabled, bool confirmed, bool unsafeEnabled)
    {
        var changed = this.BgPartCollisionModeEnabled != enabled ||
                      this.BgPartCollisionModeConfirmed != (enabled && confirmed) ||
                      this.UnsafeNativeWritesEnabled != unsafeEnabled;
        this.UnsafeNativeWritesEnabled = unsafeEnabled;
        this.BgPartCollisionModeEnabled = enabled;
        this.BgPartCollisionModeConfirmed = enabled && confirmed;
        this.AllowNativeTransformWrites = unsafeEnabled && (!enabled || this.BgPartCollisionModeConfirmed);
        this.NativeFullLayoutTransformConfirmed = enabled && this.BgPartCollisionModeConfirmed;
        if (!changed)
            return;

        this.LastBgPartCollisionOperation = enabled
            ? this.BgPartCollisionModeConfirmed
                ? "collisionMode=On; collision operation=Moved with layout transform when BgPart transform is applied."
                : "collisionMode=OnPendingConfirmation; collision operation=Failed until FullLayout confirmation is enabled."
            : "collisionMode=Off; collision operation=Skipped; visual-only BgPart transform.";
    }

    public void UpdateLocalActorLookAt(string runtimeId, bool enabled, float radius)
    {
        this.actors.UpdateActorLookAtSettings(runtimeId, enabled, radius);
        this.LastStatus = this.actors.LastMessage;
    }

    public bool PlayLocalActorAnimation(string runtimeId, uint animationId)
    {
        var result = this.actors.PlayAnimation(runtimeId, animationId);
        this.LastStatus = this.actors.LastMessage;
        return result;
    }

    public bool StopLocalActorAnimation(string runtimeId)
    {
        var result = this.actors.StopAnimation(runtimeId);
        this.LastStatus = this.actors.LastMessage;
        return result;
    }

    public void RequestLocalLightApply(string runtimeId)
    {
        this.localLights.RequestApply(runtimeId);
        this.LastStatus = this.localLights.LastStatus;
        this.TransformGeneration++;
        this.MarkPersistDirty("LocalLight apply");
    }

    public void RequestRestore(string reason)
    {
        this.restorePending = true;
        this.restoreReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason;
        this.log.Information("[Restore] Request reason={Reason}", this.restoreReason);
        this.RestoreStatus = $"Restore pending: {this.restoreReason}";
    }

    public void NotifySceneChanging(string reason)
    {
        this.sceneGeneration++;
        this.sceneStableTicks = 0;
        this.restoreRunning = false;
        this.restoreStage = 0;
        this.restoreIndex = 0;
        this.restoreNativeRecords.Clear();
        this.restoreBgPartRecords.Clear();
        this.restoreActorRecords.Clear();
        this.restoreLightRecords.Clear();
        this.activeBgPartRestore = null;
        this.restoreActorsRequested = false;
        this.nativeCache.Clear();
        this.lastNativeScanUtc = DateTime.MinValue;
        this.RestoreStatus = $"[Restore] Scene invalidated: {reason}";
        this.log.Information("[Restore] Scene invalidated reason={Reason} generation={Generation}", reason, this.sceneGeneration);
    }

    public void UpdateRestoreQueue(bool sceneStable)
    {
        this.SyncLocalBgPartSnapshotsDebounced();
        this.SyncLocalActorSnapshotsDebounced();
        this.SaveDirtyConfigurationIfDue();

        var territory = this.getTerritoryType();
        if (this.lastSeenTerritory != territory)
        {
            this.lastSeenTerritory = territory;
            this.NotifySceneChanging($"TerritoryChanged:{territory}");
        }

        if (!sceneStable)
        {
            this.sceneStableTicks = 0;
            if (this.restoreRunning)
                this.RestoreStatus = "[Restore] Paused reason=scene unstable";
            return;
        }

        this.sceneStableTicks++;
        if (this.sceneStableTicks < RequiredRestoreStableTicks)
        {
            this.RestoreStatus = $"[Restore] Wait scene stable ticks={this.sceneStableTicks}/{RequiredRestoreStableTicks}";
            return;
        }

        if (!this.restoreRunning && this.restorePending)
            this.StartRestoreQueue();

        if (!this.restoreRunning)
            return;

        if (this.restoreWaitTicks > 0)
        {
            this.restoreWaitTicks--;
            this.RestoreStatus = $"[Restore] Wait scene stable ticks={this.restoreWaitTicks}";
            return;
        }

        this.AdvanceRestoreQueue();
    }

    public void FlushPersistence()
    {
        this.SyncLocalBgPartSnapshots();
        this.SyncLocalActorSnapshots();
        if (this.persistDirty)
        {
            this.log.Debug("[Persist] Save begin.");
            this.saveConfiguration();
            this.persistDirty = false;
            this.log.Debug("[Persist] Save end.");
        }
    }

    public void SetHoveredMarker(SceneEditableRef? item, Vector2 screenPosition, Vector2 mousePosition, float distance)
    {
        this.LastHoveredMarkerDebug = item == null
            ? "none"
            : $"{item.Kind} {item.RuntimeId} screen={screenPosition} mouse={mousePosition} dist={distance:F1}";
        if (item != null)
            this.Gizmo.SetMarkerHover(item.RuntimeId, screenPosition, mousePosition, distance);
    }

    public void NotifyMarkerClick(SceneEditableRef item, Vector2 screenPosition, Vector2 mousePosition)
    {
        this.LastClickedMarkerDebug = $"{item.Kind} {item.RuntimeId} screen={screenPosition} mouse={mousePosition}";
        this.log.Information("[SceneEditor] MarkerClick kind={Kind} id={Id} screen={Screen} mouse={Mouse}", item.Kind, item.RuntimeId, screenPosition, mousePosition);
    }

    public void NotifyGizmoDragStart(SceneEditableRef item, SceneEditorGizmoMode mode, string axis)
    {
        this.LastGizmoDebug = $"drag start {mode}/{axis} {item.Kind} {item.RuntimeId}";
        this.log.Information("[SceneEditor] GizmoDragStart kind={Kind} id={Id} mode={Mode} axis={Axis}", item.Kind, item.RuntimeId, mode, axis);
    }

    public void NotifyGizmoDragDelta(SceneEditableRef item, SceneEditorGizmoMode mode, string axis, WorldTransform transform)
    {
        this.LastGizmoDebug = $"drag {mode}/{axis} pos={transform.WorldPosition} rot={transform.WorldEulerRadians} scale={transform.WorldScale}";
    }

    public void NotifyGizmoDragEnd(SceneEditableKind kind, string runtimeId, SceneEditorGizmoMode mode, string axis)
    {
        this.LastGizmoDebug = $"drag end {mode}/{axis} {kind} {runtimeId}";
        this.log.Information("[SceneEditor] GizmoDragEnd kind={Kind} id={Id} mode={Mode} axis={Axis}", kind, runtimeId, mode, axis);
    }

    public IReadOnlyList<SceneEditableRef> GetEditables()
    {
        var items = new List<SceneEditableRef>();

        if (this.ShowPluginObjects && this.ShowActors)
        {
            foreach (var actor in this.actors.Actors)
            {
                var position = actor.TransformEditPosition != Vector3.Zero ? actor.TransformEditPosition : actor.LastKnownPosition;
                var rotation = ActorTransformUtil.NormalizeRotation(actor.TransformEditRotationEuler != Vector3.Zero ? actor.TransformEditRotationEuler : actor.LastKnownRotationEuler);
                var scale = ActorTransformUtil.NormalizeScale(actor.TransformEditScale == Vector3.Zero ? actor.LastKnownScale : actor.TransformEditScale);
                items.Add(new SceneEditableRef(
                    actor.RuntimeId,
                    SceneEditableKind.LocalActor,
                    ParsePointer(actor.Address),
                    ParseObjectIndex(actor.ObjectIndex),
                    string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.NpcName : actor.DisplayName,
                    actor.AppearanceSourceType,
                    true,
                    WorldTransform.FromEuler(position, rotation, scale == Vector3.Zero ? Vector3.One : scale),
                    actor.IsValid && actor.CharacterObject != null,
                    actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden));
            }
        }

        if (this.ShowPluginObjects && this.ShowBgParts)
        {
            foreach (var instance in this.localLayoutObjects.Instances)
            {
                items.Add(new SceneEditableRef(
                    instance.Id,
                    SceneEditableKind.LocalBgPart,
                    ParsePointer(instance.GraphicsObjectAddress),
                    -1,
                    string.IsNullOrWhiteSpace(instance.CurrentResourcePath) ? instance.Id : instance.CurrentResourcePath,
                    FirstNonEmpty(instance.CustomModelPath, instance.CurrentResourcePath, instance.TemplateResourcePath),
                    true,
                    WorldTransform.FromEuler(instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale),
                    !instance.IsRestored && !instance.IsInvalid && !instance.IsDuplicate && !instance.IsRenderInvalid,
                    !instance.Visible));
            }
        }

        if (this.ShowPluginObjects && this.ShowLights)
        {
            foreach (var light in this.localLights.Instances)
            {
                items.Add(new SceneEditableRef(
                    light.Id,
                    SceneEditableKind.LocalLight,
                    light.NativeSceneLight,
                    -1,
                    light.Name,
                    light.LightKind.ToString(),
                    true,
                    WorldTransform.FromEuler(light.Position, light.Rotation, light.Scale),
                    light.Enabled && light.IsNativeCreated,
                    light.Hidden || !light.Enabled));
            }
        }

        if (this.ShowNativeObjects)
            items.AddRange(this.GetNativeEditables());

        return items;
    }

    public SceneEditableRef? GetSelectedEditable()
    {
        if (!this.selection.HasSelection || this.selection.SelectedKind == null)
            return null;

        return this.GetEditables().FirstOrDefault(item =>
            item.Kind == this.selection.SelectedKind &&
            string.Equals(item.RuntimeId, this.selection.SelectedRuntimeId, StringComparison.OrdinalIgnoreCase));
    }

    public bool RefreshSelectedTransform()
    {
        var selected = this.GetSelectedEditable();
        if (selected == null)
        {
            this.LastStatus = "No selected Scene Editor object.";
            return false;
        }

        return this.RefreshTransform(selected.Kind, selected.RuntimeId);
    }

    public bool RefreshTransform(SceneEditableKind kind, string runtimeId)
    {
        var result = kind switch
        {
            SceneEditableKind.LocalActor => this.actors.RefreshActorTransform(runtimeId),
            SceneEditableKind.LocalBgPart => this.localLayoutObjects.RefreshWorldTransform(runtimeId),
            SceneEditableKind.LocalLight => true,
            _ => false,
        };

        if (kind == SceneEditableKind.LocalLight)
            this.LastStatus = "LocalLight transform is read from plugin config/native apply state.";
        else if (!result)
            this.LastStatus = $"Refresh transform failed for {kind}: {runtimeId}";

        if (result)
            this.TransformGeneration++;

        return result;
    }

    public bool ApplyWorldTransform(SceneEditableKind kind, string runtimeId, WorldTransform transform)
    {
        var scale = kind == SceneEditableKind.LocalActor
            ? ActorTransformUtil.NormalizeScale(transform.WorldScale)
            : WorldTransformUtil.NormalizeScale(transform.WorldScale);
        var rotation = kind == SceneEditableKind.LocalActor
            ? ActorTransformUtil.NormalizeRotation(transform.WorldEulerRadians)
            : transform.WorldEulerRadians;
        this.log.Debug("[SceneEditor] ApplyWorldTransform kind={Kind} id={Id} pos={Position} rot={Rotation} scale={Scale}",
            kind,
            runtimeId,
            transform.WorldPosition,
            rotation,
            scale);

        switch (kind)
        {
            case SceneEditableKind.LocalActor:
                var actorResult = this.actors.ApplyActorTransform(runtimeId, transform.WorldPosition, rotation, scale);
                this.LastStatus = this.actors.LastMessage;
                if (actorResult)
                {
                    this.actors.PersistActorWorldTransformToNpc(runtimeId, transform.WorldPosition, rotation, scale);
                    this.SyncLocalActorRecord(runtimeId, transform.WorldPosition, rotation, scale);
                    this.TransformGeneration++;
                    this.MarkPersistDirty("LocalActor transform");
                }
                return actorResult;
            case SceneEditableKind.LocalBgPart:
                if (!this.ApplyLocalBgPartTransform(runtimeId, transform.WorldPosition, transform.WorldEulerRadians, scale, "SceneEditor LocalBgPart transform"))
                    return false;
                this.LastStatus = this.localLayoutObjects.LastStatus;
                this.TransformGeneration++;
                this.SyncLocalBgPartSnapshots();
                this.MarkPersistDirty("LocalBgPart transform");
                return true;
            case SceneEditableKind.LocalLight:
                var light = this.localLights.GetById(runtimeId);
                if (light == null)
                {
                    this.LastStatus = $"LocalLight not found: {runtimeId}";
                    return false;
                }

                light.Position = transform.WorldPosition;
                light.Rotation = transform.WorldEulerRadians;
                light.Scale = scale;
                light.TerritoryId = this.getTerritoryType();
                this.localLights.RequestApply(runtimeId);
                this.LastStatus = this.localLights.LastStatus;
                this.TransformGeneration++;
                this.MarkPersistDirty("LocalLight transform");
                return true;
            case SceneEditableKind.NativeBgPart:
            case SceneEditableKind.NativeLight:
                if (!this.AllowNativeTransformWrites)
                {
                    this.LastStatus = "Native transform write blocked: unsafe/native writes are disabled.";
                    return false;
                }

                if (!this.CanRunRestoreNow(out var nativeLayoutPauseReason))
                {
                    this.LastStatus = $"Native transform write skipped: {nativeLayoutPauseReason}.";
                    return false;
                }

                var beforeLayout = this.FindEditable(kind, runtimeId);
                var layoutResult = this.ApplyNativeLayoutTransform(runtimeId, transform);
                if (layoutResult && beforeLayout is { IsNativeGameObject: true, IsPlayer: false })
                    this.RecordNativeTransformChange(beforeLayout, WorldTransform.FromEuler(transform.WorldPosition, transform.WorldEulerRadians, scale), "SceneEditorTransform");
                return layoutResult;
            case SceneEditableKind.NativeActor:
            case SceneEditableKind.EventNpc:
                if (!this.AllowNativeTransformWrites)
                {
                    this.LastStatus = "Native actor transform write blocked: unsafe/native writes are disabled.";
                    return false;
                }

                if (!this.CanRunRestoreNow(out var nativeActorPauseReason))
                {
                    this.LastStatus = $"Native actor transform write skipped: {nativeActorPauseReason}.";
                    return false;
                }

                var beforeActor = this.FindEditable(kind, runtimeId);
                var nativeActorResult = this.ApplyNativeActorTransform(runtimeId, transform);
                if (nativeActorResult && beforeActor is { IsNativeGameObject: true, IsPlayer: false })
                    this.RecordNativeTransformChange(beforeActor, WorldTransform.FromEuler(transform.WorldPosition, transform.WorldEulerRadians, scale), "SceneEditorTransform");
                return nativeActorResult;
            case SceneEditableKind.Player:
                this.LastStatus = "LocalPlayer transform editing is always blocked.";
                return false;
            default:
                this.LastStatus = $"Unsupported editable kind: {kind}";
                return false;
        }
    }

    public void PushTransformUndo(
        SceneEditableKind kind,
        string runtimeId,
        string displayName,
        WorldTransform before,
        WorldTransform after,
        string source)
        => this.Undo.Push(kind, runtimeId, displayName, before, after, source);

    public bool TryUndoLast()
    {
        var result = this.Undo.TryApplyUndo(entry =>
        {
            var current = this.GetEditables().FirstOrDefault(item =>
                item.Kind == entry.Kind &&
                string.Equals(item.RuntimeId, entry.RuntimeId, StringComparison.OrdinalIgnoreCase));
            if (current == null || !current.IsValid || !current.TransformEditable)
                return false;

            return this.ApplyWorldTransform(entry.Kind, entry.RuntimeId, entry.Before);
        }, out var message);

        this.LastStatus = message;
        return result;
    }

    public void ForgetLocalBgPartRecord(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        var removed = this.LocalBgPartRecords.RemoveAll(item => string.Equals(item.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            this.MarkPersistDirty("LocalBgPart record removed");
    }

    public void ForgetAllLocalBgPartRecords()
    {
        if (this.LocalBgPartRecords.Count == 0)
            return;

        this.LocalBgPartRecords.Clear();
        this.MarkPersistDirty("LocalBgPart records cleared");
    }

    public bool TryHandleUndoShortcut(bool isGizmoDragging, bool wantTextInput, bool ctrlDown, bool zPressed)
    {
        if (!this.OverlayEnabled || !zPressed)
            return false;

        if (isGizmoDragging)
        {
            this.LastStatus = "Ctrl+Z skipped: gizmo dragging.";
            return false;
        }

        if (wantTextInput)
        {
            this.LastStatus = "Ctrl+Z skipped: text input active.";
            return false;
        }

        if (!ctrlDown)
            return false;

        var now = DateTime.UtcNow;
        if ((now - this.lastUndoShortcutUtc).TotalMilliseconds < 80)
            return false;

        this.lastUndoShortcutUtc = now;
        if (!this.Undo.HasUndo)
        {
            this.LastStatus = "Ctrl+Z skipped: undo stack is empty.";
            return false;
        }

        this.log.Debug("[SceneEditor] CtrlZ detected.");
        return this.TryUndoLast();
    }

    public bool SaveSelectedTransform(WorldTransform transform)
    {
        var selected = this.GetSelectedEditable();
        if (selected == null)
        {
            this.LastStatus = "No selected Scene Editor object.";
            return false;
        }

        switch (selected.Kind)
        {
            case SceneEditableKind.LocalActor:
                this.actors.SaveActorTransformSnapshot(
                    selected.RuntimeId,
                    transform.WorldPosition,
                    ActorTransformUtil.NormalizeRotation(transform.WorldEulerRadians),
                    ActorTransformUtil.NormalizeScale(transform.WorldScale));
                this.LastStatus = this.actors.LastMessage;
                return true;
            case SceneEditableKind.LocalBgPart:
                if (!this.ApplyLocalBgPartTransform(selected.RuntimeId, transform.WorldPosition, transform.WorldEulerRadians, transform.WorldScale, "SceneEditor SaveSelectedTransform"))
                    return false;
                this.LastStatus = this.localLayoutObjects.LastStatus;
                return true;
            case SceneEditableKind.LocalLight:
                this.ApplyWorldTransform(selected.Kind, selected.RuntimeId, transform);
                return true;
            default:
                return false;
        }
    }

    private bool ApplyLocalBgPartTransform(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale, string reason)
    {
        var mode = this.CurrentBgPartTransformMode;
        if (mode == LocalLayoutTransformMode.FullLayoutWithCollision && !this.NativeFullLayoutTransformConfirmed)
        {
            this.LastStatus = "Local BgPart transform blocked: collision mode is ON but FullLayoutWithCollision is not confirmed.";
            this.LastBgPartCollisionOperation = "source=Plugin; collisionMode=OnPendingConfirmation; collision operation=Failed; handle=unavailable";
            return false;
        }

        var instance = this.localLayoutObjects.GetById(runtimeId);
        if (instance == null)
        {
            this.LastStatus = $"Local BgPart not found: {runtimeId}";
            this.LastBgPartCollisionOperation = "source=Plugin; collisionMode=Unknown; collision operation=Failed; handle=unavailable";
            return false;
        }

        if (instance.TransformMode != mode)
        {
            var changed = this.localLayoutObjects.ChangeCollisionMode(
                runtimeId,
                mode,
                this.GetCurrentBgPartCandidates(),
                this.AllowNativeTransformWrites,
                this.NativeFullLayoutTransformConfirmed || mode == LocalLayoutTransformMode.VisualOnly);
            if (!changed)
            {
                this.LastStatus = this.localLayoutObjects.LastStatus;
                this.LastBgPartCollisionOperation = $"source=Plugin; collisionMode={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "On" : "Off")}; collision operation=Failed; handle={instance.OccupiedSlotAddress}; {this.LastStatus}";
                return false;
            }
        }

        this.localLayoutObjects.ApplyVisualTransform(runtimeId, position, rotationEuler, scale);
        this.LastBgPartCollisionOperation = $"source=Plugin; collisionMode={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "On" : "Off")}; collision operation={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "Moved" : "Skipped")}; handle={instance.OccupiedSlotAddress}; reason={reason}";
        return true;
    }

    public bool TryCopyOneBgPart(SceneEditableRef selected, Vector3 offset)
    {
        var template = this.ResolveBgPartProbe(selected);
        if (template == null)
        {
            this.LastQuickActionStatus = "Cannot copy: selected object is not a BgPart with a readable mdl path.";
            return false;
        }

        var sourceTransform = WorldTransform.FromEuler(
            selected.Transform.WorldPosition,
            selected.Transform.WorldEulerRadians,
            selected.Transform.WorldScale);
        return this.CreateSingleBgPartCopy(template, sourceTransform, offset);
    }

    public bool TryCopyOneBgPart(LayoutProbeInstance? source, Vector3 offset)
    {
        if (source == null)
        {
            this.LastQuickActionStatus = "Cannot copy: no source BgPart is selected.";
            return false;
        }

        var sourceTransform = WorldTransform.FromEuler(
            source.Position,
            Vector3.Zero,
            source.Scale == Vector3.Zero ? Vector3.One : source.Scale);
        return this.CreateSingleBgPartCopy(source, sourceTransform, offset);
    }

    private bool CreateSingleBgPartCopy(LayoutProbeInstance template, WorldTransform sourceTransform, Vector3 offset)
    {
        if (!this.AllowNativeTransformWrites)
        {
            this.LastQuickActionStatus = "Cannot copy BgPart: unsafe/native writes are disabled or FullLayoutWithCollision has not been confirmed.";
            return false;
        }

        var candidates = this.GetCurrentBgPartCandidates().ToList();
        var mode = this.CurrentBgPartTransformMode;
        var created = this.localLayoutObjects.CreateCopyFromTemplate(
            template,
            candidates,
            sourceTransform.WorldPosition + offset,
            mode,
            CarrierAllocationPolicy.PreferredListThenAnyValid,
            unsafeEnabled: this.AllowNativeTransformWrites,
            fullLayoutConfirmed: this.NativeFullLayoutTransformConfirmed,
            defaultRotationEuler: sourceTransform.WorldEulerRadians,
            defaultScale: sourceTransform.WorldScale);

        if (created == null)
        {
            this.LastQuickActionStatus = this.localLayoutObjects.LastStatus;
            return false;
        }

        this.selection.Select(SceneEditableKind.LocalBgPart, created.Id, SceneEditorSelectionSource.SceneEditorPanel);
        this.LastBgPartCollisionOperation = $"source={(template.Source.Contains("Native", StringComparison.OrdinalIgnoreCase) ? "Native" : "Plugin/Probe")}; collisionMode={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "On" : "Off")}; collision operation={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "Cloned/Moved" : "Skipped")}; handle={created.OccupiedSlotAddress}";
        this.LastQuickActionStatus = $"Copied one BgPart: {created.Id}; mode={mode}; {this.LastBgPartCollisionOperation}";
        this.TransformGeneration++;
        this.SyncLocalBgPartSnapshots();
        this.MarkPersistDirty("LocalBgPart created");
        return true;
    }

    public bool TryProtectBgPart(SceneEditableRef selected)
    {
        var slot = this.ResolveBgPartProbe(selected);
        if (slot == null)
        {
            this.LastQuickActionStatus = "Cannot protect: selected object is not a BgPart.";
            return false;
        }

        var registry = this.localLayoutObjects.ProtectedBgParts;
        if (registry == null)
        {
            this.LastQuickActionStatus = "Cannot protect: protection registry is unavailable.";
            return false;
        }

        var added = registry.ProtectSlot(slot, "Added from Scene Editor");
        this.LastQuickActionStatus = added ? "Added BgPart slot to protection list." : "BgPart was already protected or could not be added.";
        return added;
    }

    public bool TryPreferBgPart(SceneEditableRef selected)
    {
        var slot = this.ResolveBgPartProbe(selected);
        if (slot == null)
        {
            this.LastQuickActionStatus = "Cannot prefer: selected object is not a BgPart.";
            return false;
        }

        var registry = this.localLayoutObjects.PreferredModifyBgParts;
        if (registry == null)
        {
            this.LastQuickActionStatus = "Cannot prefer: preferred modify registry is unavailable.";
            return false;
        }

        var added = registry.ProtectSlot(slot, "Added from Scene Editor");
        this.LastQuickActionStatus = added ? "Added BgPart slot to preferred modify list." : "BgPart was already preferred or could not be added.";
        return added;
    }

    public bool TryMarkBgPartCandidate(SceneEditableRef selected)
    {
        var slot = this.ResolveBgPartProbe(selected);
        if (slot == null)
        {
            this.LastQuickActionStatus = "Cannot select candidate: selected object is not a BgPart.";
            return false;
        }

        this.LastQuickActionStatus = $"Scene Editor candidate BgPart: {slot.ResourcePath} @ {slot.Address}";
        return true;
    }

    public SceneEditorNativeModificationRecord? GetNativeModificationRecord(SceneEditableRef selected)
    {
        if (!selected.IsNativeGameObject)
            return null;

        var stableKey = this.GetStableKey(selected);
        return this.NativeRecords.FirstOrDefault(item =>
            string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
    }

    public bool HideNativeObject(SceneEditableRef selected)
    {
        if (!CanTrackNativeModification(selected))
        {
            this.LastStatus = "Native hide blocked: target is not a supported native object.";
            return false;
        }

        if (!this.AllowNativeTransformWrites)
        {
            this.LastStatus = "Native hide blocked: enable the existing unsafe/native write confirmation first.";
            return false;
        }

        var originalTransform = this.ReadNativeTransformOrFallback(selected);
        var record = this.EnsureNativeRecord(selected, originalTransform, "Hidden");
        var hiddenPosition = originalTransform.WorldPosition + new Vector3(0f, -5000f, 0f);
        var hiddenTransform = WorldTransform.FromEuler(hiddenPosition, originalTransform.WorldEulerRadians, originalTransform.WorldScale);

        if (!this.ApplyWorldTransform(selected.Kind, selected.RuntimeId, hiddenTransform))
        {
            record.Status = "HideFailed";
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native hide failed");
            return false;
        }

        SetRecordCurrentTransform(record, hiddenTransform);
        SetVector3Data(record.HiddenPosition, hiddenTransform.WorldPosition);
        SetVector3Data(record.HiddenRotationEuler, hiddenTransform.WorldEulerRadians);
        SetVector3Data(record.HiddenScale, hiddenTransform.WorldScale);
        record.IsHidden = true;
        record.IsModified = true;
        record.Reason = "Hidden";
        record.Status = "Hidden";
        if (selected.Kind == SceneEditableKind.NativeBgPart && string.IsNullOrWhiteSpace(record.PreferredModifyStatus))
        {
            var preferred = this.TryPreferBgPart(selected);
            record.PreferredModifyAdded = preferred || record.PreferredModifyAdded;
            record.PreferredModifyStatus = this.LastQuickActionStatus;
        }

        this.MarkPersistDirty("Native hide");
        this.LastStatus = $"Native object hidden underground: {selected.DisplayName}";
        this.log.Information("[SceneEditor] Native hide kind={Kind} key={Key} name={Name}", selected.Kind, record.StableKey, selected.DisplayName);
        return true;
    }

    public bool RestoreNativeObject(SceneEditableRef selected)
    {
        var record = this.GetNativeModificationRecord(selected);
        if (record == null)
        {
            this.LastStatus = "No native modification record exists for the selected object.";
            return false;
        }

        return this.RestoreNativeModification(record.RecordId);
    }

    public bool RestoreNativeModification(string recordId)
    {
        var record = this.NativeRecords.FirstOrDefault(item =>
            string.Equals(item.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            this.LastStatus = "Native modification record not found.";
            return false;
        }

        if (record.Kind == SceneEditableKind.Player)
        {
            record.Status = "RestoreBlockedPlayer";
            this.MarkPersistDirty("Native restore blocked");
            this.LastStatus = "LocalPlayer restore is blocked.";
            return false;
        }

        if (!this.CanRunRestoreNow(out var pausedReason))
        {
            this.LastStatus = $"Native restore paused: {pausedReason}.";
            return false;
        }

        var original = GetOriginalTransform(record);
        if (!this.ApplyNativeRecordTransform(record, original))
        {
            record.Status = "Missing";
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native restore missing");
            this.LastStatus = $"Native restore failed or object missing: {record.DisplayName}";
            return false;
        }

        SetRecordCurrentTransform(record, original);
        record.IsHidden = false;
        record.IsModified = false;
        record.Status = "Restored";
        record.LastModifiedAt = DateTime.UtcNow;
        this.MarkPersistDirty("Native restore");
        this.LastStatus = $"Native object restored: {record.DisplayName}";
        this.log.Information("[SceneEditor] Native restore kind={Kind} key={Key} name={Name}", record.Kind, record.StableKey, record.DisplayName);
        return true;
    }

    public int RestoreAllHiddenNativeObjects()
    {
        return this.RestoreNativeRecords(item => item.IsHidden, "all hidden native objects");
    }

    public int RestoreAllNativeModifications()
    {
        return this.RestoreNativeRecords(item => item.IsHidden || item.IsModified, "all native modifications");
    }

    public int RestoreCurrentTerritoryNativeActors()
        => this.RestoreNativeRecords(item => this.IsCurrentTerritoryRecord(item) && item.Kind is SceneEditableKind.NativeActor or SceneEditableKind.EventNpc && (item.IsHidden || item.IsModified), "current territory native NPC/EventNPC modifications");

    public int RestoreCurrentTerritoryNativeBgParts()
        => this.RestoreNativeRecords(item => this.IsCurrentTerritoryRecord(item) && item.Kind == SceneEditableKind.NativeBgPart && (item.IsHidden || item.IsModified), "current territory native BgPart modifications");

    public int RestoreCurrentTerritoryNativeLights()
        => this.RestoreNativeRecords(item => this.IsCurrentTerritoryRecord(item) && item.Kind == SceneEditableKind.NativeLight && (item.IsHidden || item.IsModified), "current territory native Light modifications");

    public int RestoreCurrentTerritoryNativeModifications()
        => this.RestoreNativeRecords(item => this.IsCurrentTerritoryRecord(item) && (item.IsHidden || item.IsModified), "current territory native modifications");

    public int RestoreCurrentTerritoryHiddenObjects()
        => this.RestoreNativeRecords(item => this.IsCurrentTerritoryRecord(item) && item.IsHidden, "current territory hidden native objects");

    private int RestoreNativeRecords(Func<SceneEditorNativeModificationRecord, bool> predicate, string label)
    {
        var records = this.NativeRecords.Where(predicate).Select(item => item.RecordId).ToList();
        var restored = 0;
        foreach (var id in records)
        {
            if (this.RestoreNativeModification(id))
                restored++;
        }

        this.LastStatus = $"Restored {label}: {restored}/{records.Count}";
        return restored;
    }

    public bool RemoveNativeModificationRecord(string recordId)
    {
        var record = this.NativeRecords.FirstOrDefault(item => string.Equals(item.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
        if (record is { IsHidden: true } or { IsModified: true })
        {
            this.LastStatus = "Record cleanup blocked: restore Hidden/Modified records before removing them.";
            return false;
        }

        var removed = record == null ? 0 : this.NativeRecords.Remove(record) ? 1 : 0;
        if (removed <= 0)
            return false;

        this.MarkPersistDirty("Native record removed");
        this.LastStatus = "Removed native modification record.";
        return true;
    }

    public int CleanupInactiveNativeModificationRecords()
    {
        var removed = this.NativeRecords.RemoveAll(item =>
            string.Equals(item.Status, "Restored", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Status, "Missing", StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            this.MarkPersistDirty("Native records cleanup");

        this.LastStatus = $"Cleaned native modification records: {removed}";
        return removed;
    }

    private void CancelRestore(string reason)
    {
        if (!this.restoreRunning)
            return;

        this.restoreRunning = false;
        this.restorePending = true;
        this.restoreStage = 0;
        this.restoreIndex = 0;
        this.restoreNativeRecords.Clear();
        this.restoreBgPartRecords.Clear();
        this.restoreActorRecords.Clear();
        this.restoreLightRecords.Clear();
        this.activeBgPartRestore = null;
        this.restoreActorsRequested = false;
        this.RestoreStatus = $"[Restore] Cancelled: {reason}";
        this.log.Information("[Restore] Cancel reason={Reason}", reason);
    }

    private void StartRestoreQueue()
    {
        this.restorePending = false;
        this.restoreRunning = true;
        this.restoreGeneration = this.sceneGeneration;
        this.restoreStage = 0;
        this.restoreIndex = 0;
        this.restoreWaitTicks = 8;
        this.activeBgPartRestore = null;
        this.restoreActorsRequested = false;

        var territory = this.getTerritoryType();
        var nativeTotal = this.NativeRecords.Count;
        var nativeLegacy = this.NativeRecords.Count(item => item.TerritoryId == 0);
        var nativeWrongTerritory = this.NativeRecords.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreNativeRecords = this.NativeRecords
            .Where(item => item.TerritoryId == territory)
            .Where(item => item.Kind != SceneEditableKind.Player)
            .Where(item => item.IsHidden || item.IsModified)
            .Where(item => !string.Equals(item.Status, "Restored", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.CreatedAt)
            .ToList();

        var bgTotal = this.LocalBgPartRecords.Count;
        var bgLegacy = this.LocalBgPartRecords.Count(item => item.TerritoryId == 0);
        var bgWrongTerritory = this.LocalBgPartRecords.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreBgPartRecords = this.LocalBgPartRecords
            .Where(item => item.Enabled)
            .Where(item => item.TerritoryId == territory)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.LastSavedAt)
            .ToList();

        var lightTotal = this.localLights.Instances.Count;
        var lightLegacy = this.localLights.Instances.Count(item => item.TerritoryId == 0);
        var lightWrongTerritory = this.localLights.Instances.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreLightRecords = this.localLights.Instances
            .Where(item => item.TerritoryId == territory)
            .Select((item, index) => new { item, index })
            .OrderBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToList();

        var actorTotal = this.LocalActorRecords.Count;
        var actorLegacy = this.LocalActorRecords.Count(item => item.TerritoryId == 0);
        var actorWrongTerritory = this.LocalActorRecords.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreActorRecords = this.LocalActorRecords
            .Where(item => item.Enabled)
            .Where(item => item.TerritoryId == territory)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.LastSavedAt)
            .ToList();

        var actorRestoreCount = this.restoreActorRecords.Count;
        foreach (var record in this.NativeRecords.Where(item => item.TerritoryId == 0))
            record.Status = "LegacyNoTerritory";
        foreach (var record in this.LocalBgPartRecords.Where(item => item.TerritoryId == 0))
            record.RestoreStatus = "LegacyNoTerritory: skipped automatic restore.";
        foreach (var record in this.LocalActorRecords.Where(item => item.TerritoryId == 0))
            record.RestoreStatus = "LegacyNoTerritory: skipped automatic restore.";
        foreach (var light in this.localLights.Instances.Where(item => item.TerritoryId == 0))
            light.LastOperation = "LegacyNoTerritory: skipped automatic restore.";
        this.RestoreStatus = $"[Restore] Start generation reason={this.restoreReason}; native={this.restoreNativeRecords.Count}; bgParts={this.restoreBgPartRecords.Count}; lights={this.restoreLightRecords.Count}; actors={actorRestoreCount}";
        this.log.Information(
            "[RestorePlan] reason={Reason}; currentTerritory={Territory}; native total={NativeTotal}, matched={NativeCount}, skippedWrongTerritory={NativeWrongTerritory}, skippedLegacyNoTerritory={NativeLegacy}; localBgParts total={BgTotal}, matched={BgPartCount}, skippedWrongTerritory={BgWrongTerritory}, skippedLegacyNoTerritory={BgLegacy}; localLights total={LightTotal}, matched={LightCount}, skippedWrongTerritory={LightWrongTerritory}, skippedLegacyNoTerritory={LightLegacy}; localActors total={ActorTotal}, matched={ActorCount}, skippedWrongTerritory={ActorWrongTerritory}, skippedLegacyNoTerritory={ActorLegacy}",
            this.restoreReason,
            territory,
            nativeTotal,
            this.restoreNativeRecords.Count,
            nativeWrongTerritory,
            nativeLegacy,
            bgTotal,
            this.restoreBgPartRecords.Count,
            bgWrongTerritory,
            bgLegacy,
            lightTotal,
            this.restoreLightRecords.Count,
            lightWrongTerritory,
            lightLegacy,
            actorTotal,
            this.restoreActorRecords.Count,
            actorWrongTerritory,
            actorLegacy);
        foreach (var record in this.LocalActorRecords.Where(item => item.TerritoryId == 0))
            this.log.Information("[RestorePlan] Skipped actor legacy no territory id={Id} npc={NpcId}", record.RecordId, record.NpcId);
        foreach (var record in this.LocalActorRecords.Where(item => item.TerritoryId != 0 && item.TerritoryId != territory))
            this.log.Information("[RestorePlan] Skipped actor wrong territory id={Id} actorTerritory={ActorTerritory} currentTerritory={CurrentTerritory}", record.RecordId, record.TerritoryId, territory);
    }

    private void AdvanceRestoreQueue()
    {
        if (!this.CanRunRestoreNow(out var pauseReason))
        {
            this.RestoreStatus = $"[Restore] Paused reason={pauseReason}";
            return;
        }

        if (this.restoreGeneration != this.sceneGeneration)
        {
            this.log.Information("[Restore] Abort stale generation old={Old} current={Current}", this.restoreGeneration, this.sceneGeneration);
            this.restoreRunning = false;
            this.restorePending = true;
            this.RestoreStatus = $"[Restore] Abort stale generation old={this.restoreGeneration} current={this.sceneGeneration}";
            return;
        }

        switch (this.restoreStage)
        {
            case 0:
                if (this.restoreIndex == 0)
                    this.log.Information("[Restore] Stage NativeModifications begin count={Count}", this.restoreNativeRecords.Count);

                if (this.restoreIndex < this.restoreNativeRecords.Count)
                {
                    var record = this.restoreNativeRecords[this.restoreIndex++];
                    this.ApplyPersistedNativeModification(record);
                    this.RestoreStatus = $"[Restore] Native {this.restoreIndex}/{this.restoreNativeRecords.Count}: {record.DisplayName}";
                    return;
                }

                this.log.Information("[Restore] Stage NativeModifications end count={Count}", this.restoreNativeRecords.Count);
                this.restoreStage = 1;
                this.restoreIndex = 0;
                return;

            case 1:
                if (this.localLayoutObjects.IsBusy || this.localLayoutObjects.IsCreateQueueActive)
                {
                    this.RestoreStatus = "[Restore] Waiting for LocalBgPart queue to become idle.";
                    return;
                }

                if (this.restoreIndex == 0)
                    this.log.Information("[Restore] Stage LocalBgParts begin count={Count}", this.restoreBgPartRecords.Count);

                if (this.activeBgPartRestore != null)
                {
                    if (!this.AdvanceActiveLocalBgPartRestore())
                        return;

                    this.activeBgPartRestore = null;
                    this.restoreIndex++;
                    return;
                }

                if (this.restoreIndex < this.restoreBgPartRecords.Count)
                {
                    var record = this.restoreBgPartRecords[this.restoreIndex];
                    this.activeBgPartRestore = new LocalBgPartRestoreItem(
                        record,
                        WorldTransform.FromEuler(
                            ToVector3(record.WorldPosition),
                            ToVector3(record.WorldRotationEuler),
                            WorldTransformUtil.NormalizeScale(ToVector3(record.WorldScale))));
                    this.RestoreStatus = $"[RestoreBgPart] state=Pending order={record.SortOrder} path={FirstNonEmpty(record.CurrentMdlPath, record.SourceMdlPath, record.InstanceId)}";
                    this.log.Information("[RestoreBgPart] state=Pending order={Order} path={Path}", record.SortOrder, FirstNonEmpty(record.CurrentMdlPath, record.SourceMdlPath, record.InstanceId));
                    return;
                }

                this.log.Information("[Restore] Stage LocalBgParts end count={Count}", this.restoreBgPartRecords.Count);
                this.restoreStage = 2;
                this.restoreIndex = 0;
                return;

            case 2:
                if (this.restoreIndex == 0)
                    this.log.Information("[Restore] Stage LocalLights begin count={Count}", this.restoreLightRecords.Count);

                if (this.restoreIndex < this.restoreLightRecords.Count)
                {
                    var light = this.restoreLightRecords[this.restoreIndex++];
                    if (light.Enabled)
                        this.localLights.RequestApply(light.Id);
                    this.RestoreStatus = $"[Restore] Light {this.restoreIndex}/{this.restoreLightRecords.Count}: {light.Name}";
                    return;
                }

                this.log.Information("[Restore] Stage LocalLights end count={Count}", this.restoreLightRecords.Count);
                this.restoreStage = 3;
                this.restoreIndex = 0;
                return;

            case 3:
                if (!this.restoreActorsRequested)
                {
                    var queued = this.QueueRestoreLocalActorRecords();
                    this.restoreActorsRequested = true;
                    this.RestoreStatus = $"[Restore] LocalActors restore requested. queued={queued}";
                    this.log.Information("[Restore] Stage LocalActors begin queued={Queued}", queued);
                    return;
                }

                if (this.actors.IsBusy)
                {
                    this.RestoreStatus = $"[Restore] Waiting LocalActors queue: {this.actors.BusyStatus}";
                    return;
                }

                this.log.Information("[Restore] Stage LocalActors end.");
                this.restoreStage = 4;
                this.restoreIndex = 0;
                return;

            default:
                this.restoreRunning = false;
                this.RestoreStatus = $"[Restore] Complete reason={this.restoreReason}";
                this.log.Information("[Restore] Complete reason={Reason}", this.restoreReason);
                if (this.restorePending)
                    this.StartRestoreQueue();
                return;
        }
    }

    private bool CanRunRestoreNow(out string reason)
    {
        if (this.getTerritoryType() == 0)
        {
            reason = "territory invalid";
            return false;
        }

        if (this.playerPositionProvider() == null)
        {
            reason = "LocalPlayer unavailable";
            return false;
        }

        if (this.sceneStableTicks < RequiredRestoreStableTicks)
        {
            reason = $"scene stable ticks {this.sceneStableTicks}/{RequiredRestoreStableTicks}";
            return false;
        }

        if (this.actors.IsBusy)
        {
            reason = $"actor spawn/apply queue busy: {this.actors.BusyStatus}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private int QueueRestoreLocalActorRecords()
    {
        if (this.restoreActorRecords.Count == 0)
            return 0;

        var queued = 0;
        var queuedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < this.restoreActorRecords.Count; index++)
        {
            var record = this.restoreActorRecords[index];
            var npc = this.actors.GetNpcById(record.NpcId);
            if (npc == null)
            {
                record.RestoreStatus = "Failed: missing NPC config";
                this.log.Warning("[ActorRestore] skip local actor record missing npc record={Record} npc={NpcId}", record.RecordId, record.NpcId);
                continue;
            }

            var position = record.WorldPosition;
            var rotation = ActorTransformUtil.NormalizeRotation(record.WorldRotationEuler);
            var scale = ActorTransformUtil.NormalizeScale(record.WorldScale);
            var key = $"{record.NpcId}|{record.SortOrder}|{MathF.Round(position.X, 2)}|{MathF.Round(position.Y, 2)}|{MathF.Round(position.Z, 2)}";
            if (!queuedKeys.Add(key))
            {
                record.RestoreStatus = "Skipped: duplicate actor restore record";
                this.log.Information("[ActorRestore] skip duplicate local actor record={Record} key={Key}", record.RecordId, key);
                continue;
            }

            var alreadyLive = this.actors.Actors.Any(actor =>
                actor.IsValid &&
                string.Equals(actor.NpcId, record.NpcId, StringComparison.OrdinalIgnoreCase) &&
                Vector3.Distance(actor.TransformEditPosition == Vector3.Zero ? actor.SpawnPosition : actor.TransformEditPosition, position) < 0.1f);
            if (alreadyLive)
            {
                record.RestoreStatus = "Skipped: matching live actor already exists";
                continue;
            }

            this.actors.QueueRestoreActor(
                npc,
                position,
                rotation,
                scale,
                record.SortOrder,
                index,
                "SceneEditor local actor restore");
            record.RestoreStatus = "Queued";
            queued++;
        }

        if (queued > 0)
            this.MarkPersistDirty("LocalActor restore queued");
        return queued;
    }

    private bool ApplyPersistedNativeModification(SceneEditorNativeModificationRecord record)
    {
        if (record.Kind == SceneEditableKind.Player)
            return false;

        record.NativeAddress = string.Empty;
        var transform = record.IsHidden
            ? GetHiddenOrCurrentTransform(record)
            : GetCurrentTransform(record);

        if (!this.ApplyNativeRecordTransform(record, transform))
        {
            if (!string.Equals(record.Status, "Ambiguous", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(record.Status, "LegacyNoTerritory", StringComparison.OrdinalIgnoreCase))
            {
                record.Status = "Missing";
            }
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Persisted native restore missing");
            this.log.Warning("[Restore] Native restore missing stableKey={StableKey} kind={Kind} name={Name}", record.StableKey, record.Kind, record.DisplayName);
            return false;
        }

        record.Status = record.IsHidden ? "Hidden" : "Modified";
        record.LastModifiedAt = DateTime.UtcNow;
        this.MarkPersistDirty("Persisted native restore");
        this.log.Information("[Restore] Native restore stableKey={StableKey} kind={Kind} status={Status}", record.StableKey, record.Kind, record.Status);
        return true;
    }

    private bool AdvanceActiveLocalBgPartRestore()
    {
        var item = this.activeBgPartRestore;
        if (item == null)
            return true;

        if (!this.CanRunRestoreNow(out var pauseReason))
        {
            this.RestoreStatus = $"[RestoreBgPart] paused reason={pauseReason}";
            return false;
        }

        var record = item.Record;
        switch (item.State)
        {
            case LocalBgPartRestoreState.Pending:
                item.State = LocalBgPartRestoreState.ResolvingSource;
                record.RestoreStatus = "ResolvingSource";
                this.log.Information("[RestoreBgPart] state=ResolvingSource order={Order} instance={Instance}", record.SortOrder, record.InstanceId);
                return false;

            case LocalBgPartRestoreState.ResolvingSource:
            {
                var existing = this.localLayoutObjects.GetById(record.InstanceId);
                if (existing != null && !existing.IsRestored && !existing.IsInvalid && !existing.IsDuplicate)
                {
                    item.InstanceId = existing.Id;
                    item.State = LocalBgPartRestoreState.WaitingNativeReady;
                    item.WaitTicks = 5;
                    record.RestoreStatus = $"WaitingNativeReady existing={existing.Id}";
                    this.log.Information("[RestoreBgPart] state=WaitingNativeReady order={Order} existing={Id}", record.SortOrder, existing.Id);
                    return false;
                }

                var candidates = this.GetCurrentBgPartCandidates().ToList();
                if (candidates.Count == 0)
                    return this.FailActiveLocalBgPartRestore(item, "no BgPart carrier candidates");

                var template = this.ResolveBgPartTemplate(record, candidates);
                if (template == null)
                    return this.FailActiveLocalBgPartRestore(item, "no readable mdl path");

                item.Candidates = candidates;
                item.Template = template;
                item.State = LocalBgPartRestoreState.CreatingCopy;
                record.RestoreStatus = $"CreatingCopy template={template.ResourcePath}";
                this.log.Information("[RestoreBgPart] state=CreatingCopy order={Order} template={Template}", record.SortOrder, template.ResourcePath);
                return false;
            }

            case LocalBgPartRestoreState.CreatingCopy:
            {
                var created = this.localLayoutObjects.CreateCopyFromTemplate(
                    item.Template,
                    item.Candidates,
                    item.Target.WorldPosition,
                    record.CollisionMode,
                    CarrierAllocationPolicy.PreferredListThenAnyValid,
                    unsafeEnabled: true,
                    fullLayoutConfirmed: record.CollisionMode == LocalLayoutTransformMode.FullLayoutWithCollision,
                    defaultRotationEuler: item.Target.WorldEulerRadians,
                    defaultScale: item.Target.WorldScale,
                    deferInitialTransformApply: true);

                if (created == null)
                    return this.FailActiveLocalBgPartRestore(item, this.localLayoutObjects.LastStatus);

                var previousRuntime = string.IsNullOrWhiteSpace(record.InstanceId)
                    ? null
                    : this.localLayoutObjects.GetById(record.InstanceId);
                if (!string.IsNullOrWhiteSpace(record.InstanceId) &&
                    !string.Equals(created.Id, record.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                    previousRuntime == null)
                {
                    created.Id = record.InstanceId;
                }
                else
                {
                    record.InstanceId = created.Id;
                }

                item.InstanceId = created.Id;
                item.State = LocalBgPartRestoreState.WaitingInstanceRegistered;
                item.WaitTicks = 2;
                record.RestoreStatus = $"WaitingInstanceRegistered id={record.InstanceId}";
                this.log.Information("[RestoreBgPart] create deferTransformApply=true order={Order} id={Id}", record.SortOrder, record.InstanceId);
                return false;
            }

            case LocalBgPartRestoreState.WaitingInstanceRegistered:
                if (item.WaitTicks-- > 0)
                {
                    this.RestoreStatus = $"[RestoreBgPart] waiting instance registered order={record.SortOrder} ticks={item.WaitTicks}";
                    return false;
                }

                if (this.localLayoutObjects.GetById(item.InstanceId) == null)
                {
                    if (++item.Attempts < 30)
                    {
                        item.WaitTicks = 1;
                        return false;
                    }

                    return this.FailActiveLocalBgPartRestore(item, $"created instance not registered: {item.InstanceId}");
                }

                item.State = LocalBgPartRestoreState.WaitingNativeReady;
                item.Attempts = 0;
                item.WaitTicks = 5;
                record.RestoreStatus = $"WaitingNativeReady id={item.InstanceId}";
                this.log.Information("[RestoreBgPart] state=WaitingNativeReady order={Order} id={Id}", record.SortOrder, item.InstanceId);
                return false;

            case LocalBgPartRestoreState.WaitingNativeReady:
                if (item.WaitTicks-- > 0)
                {
                    this.RestoreStatus = $"[RestoreBgPart] waiting native ready order={record.SortOrder} ticks={item.WaitTicks}";
                    return false;
                }

                if (!this.localLayoutObjects.PrepareRestoreNative(item.InstanceId, out var prepareResult))
                {
                    if (++item.Attempts < 30)
                    {
                        item.WaitTicks = 2;
                        record.RestoreStatus = $"WaitingNativeReady retry={item.Attempts}; {prepareResult}";
                        this.RestoreStatus = $"[RestoreBgPart] waiting native ready retry={item.Attempts}: {prepareResult}";
                        return false;
                    }

                    return this.FailActiveLocalBgPartRestore(item, prepareResult);
                }

                item.State = LocalBgPartRestoreState.ApplyingTransform;
                item.Attempts = 0;
                record.RestoreStatus = $"ApplyingTransform; {prepareResult}";
                this.log.Information("[RestoreBgPart] apply transform begin order={Order} id={Id}", record.SortOrder, item.InstanceId);
                return false;

            case LocalBgPartRestoreState.ApplyingTransform:
                if (!this.localLayoutObjects.ApplyRestoreTransform(
                        item.InstanceId,
                        item.Target.WorldPosition,
                        item.Target.WorldEulerRadians,
                        item.Target.WorldScale,
                        out var applyResult))
                {
                    if (++item.Attempts < 5)
                    {
                        item.WaitTicks = 2;
                        item.State = LocalBgPartRestoreState.WaitingNativeReady;
                        record.RestoreStatus = $"Apply retry={item.Attempts}; {applyResult}";
                        this.RestoreStatus = $"[RestoreBgPart] apply retry={item.Attempts}: {applyResult}";
                        return false;
                    }

                    return this.FailActiveLocalBgPartRestore(item, applyResult);
                }

                item.State = LocalBgPartRestoreState.VerifyingTransform;
                item.WaitTicks = 1;
                record.RestoreStatus = $"VerifyingTransform; {applyResult}";
                this.log.Information("[RestoreBgPart] apply transform end order={Order} id={Id} result={Result}", record.SortOrder, item.InstanceId, applyResult);
                return false;

            case LocalBgPartRestoreState.VerifyingTransform:
                if (item.WaitTicks-- > 0)
                    return false;

                _ = this.localLayoutObjects.RefreshWorldTransform(item.InstanceId);
                item.State = LocalBgPartRestoreState.Done;
                record.RestoreStatus = $"Done id={item.InstanceId}";
                this.TransformGeneration++;
                this.SyncLocalBgPartSnapshots();
                this.MarkPersistDirty("LocalBgPart restored");
                this.log.Information("[RestoreBgPart] state=Done order={Order} id={Id}", record.SortOrder, item.InstanceId);
                return true;

            case LocalBgPartRestoreState.Failed:
            case LocalBgPartRestoreState.Done:
            default:
                return true;
        }
    }

    private bool FailActiveLocalBgPartRestore(LocalBgPartRestoreItem item, string reason)
    {
        item.State = LocalBgPartRestoreState.Failed;
        item.Record.RestoreStatus = $"Failed: {reason}";
        this.MarkPersistDirty("LocalBgPart restore failed");
        this.log.Warning("[RestoreBgPart] state=Failed order={Order} instance={Instance} reason={Reason}", item.Record.SortOrder, item.Record.InstanceId, reason);
        return true;
    }

    private bool RestoreLocalBgPartRecord(SceneEditorLocalBgPartRecord record)
    {
        var target = WorldTransform.FromEuler(
            ToVector3(record.WorldPosition),
            ToVector3(record.WorldRotationEuler),
            WorldTransformUtil.NormalizeScale(ToVector3(record.WorldScale)));

        var existing = this.localLayoutObjects.GetById(record.InstanceId);
        if (existing != null && !existing.IsRestored && !existing.IsInvalid && !existing.IsDuplicate)
        {
            record.RestoreStatus = $"Existing runtime instance kept without native write: {existing.Id}";
            this.log.Information("[Restore] BgPart order={Order} existing={Id} result={Result}", record.SortOrder, existing.Id, record.RestoreStatus);
            return true;
        }

        var candidates = this.GetCurrentBgPartCandidates().ToList();
        if (candidates.Count == 0)
        {
            record.RestoreStatus = "Failed: no BgPart carrier candidates.";
            this.log.Warning("[Restore] BgPart order={Order} failed: no carrier candidates. path={Path}", record.SortOrder, FirstNonEmpty(record.CurrentMdlPath, record.SourceMdlPath));
            this.MarkPersistDirty("LocalBgPart restore failed");
            return false;
        }

        var template = this.ResolveBgPartTemplate(record, candidates);
        if (template == null)
        {
            record.RestoreStatus = "Failed: no readable mdl path.";
            this.MarkPersistDirty("LocalBgPart restore failed");
            return false;
        }

        var created = this.localLayoutObjects.CreateCopyFromTemplate(
            template,
            candidates,
            target.WorldPosition,
            record.CollisionMode,
            CarrierAllocationPolicy.PreferredListThenAnyValid,
            unsafeEnabled: true,
            fullLayoutConfirmed: record.CollisionMode == LocalLayoutTransformMode.FullLayoutWithCollision,
            defaultRotationEuler: target.WorldEulerRadians,
            defaultScale: target.WorldScale);

        if (created == null)
        {
            record.RestoreStatus = $"Failed: {this.localLayoutObjects.LastStatus}";
            this.log.Warning("[Restore] BgPart order={Order} create failed: {Result}", record.SortOrder, record.RestoreStatus);
            this.MarkPersistDirty("LocalBgPart restore failed");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.InstanceId) &&
            !string.Equals(created.Id, record.InstanceId, StringComparison.OrdinalIgnoreCase) &&
            this.localLayoutObjects.GetById(record.InstanceId) == null)
        {
            created.Id = record.InstanceId;
        }
        else if (string.IsNullOrWhiteSpace(record.InstanceId))
        {
            record.InstanceId = created.Id;
        }

        record.RestoreStatus = $"Restored: {created.Id}";
        this.TransformGeneration++;
        this.SyncLocalBgPartSnapshots();
        this.MarkPersistDirty("LocalBgPart restored");
        this.log.Information("[Restore] BgPart order={Order} restored id={Id} path={Path} mode={Mode}", record.SortOrder, created.Id, template.ResourcePath, record.CollisionMode);
        return true;
    }

    private LayoutProbeInstance? ResolveBgPartTemplate(SceneEditorLocalBgPartRecord record, IReadOnlyList<LayoutProbeInstance> candidates)
    {
        var targetPath = FirstNonEmpty(record.CurrentMdlPath, record.CustomMdlPath, record.SourceMdlPath);
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var exact = candidates.FirstOrDefault(item => string.Equals(item.ResourcePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        return new LayoutProbeInstance
        {
            Type = "BgPart",
            Address = FirstNonEmpty(record.SourceBgPartStableKey, $"persisted:{record.InstanceId}"),
            Key = FirstNonEmpty(record.SourceBgPartStableKey, $"persisted:{record.InstanceId}"),
            ResourcePath = targetPath,
            SourceKind = FirstNonEmpty(record.SourceKind, "PersistedSceneEditor"),
            Position = ToVector3(record.WorldPosition),
            Rotation = ToVector3(record.WorldRotationEuler).ToString(),
            Scale = WorldTransformUtil.NormalizeScale(ToVector3(record.WorldScale)),
            SharedGroupPath = record.SourceSharedGroupPath,
            ParentKey = record.SourceBgPartStableKey,
            ChildIndex = record.SourceChildIndex,
        };
    }

    private SceneEditableRef? FindEditable(SceneEditableKind kind, string runtimeId)
        => this.GetEditables().FirstOrDefault(item =>
            item.Kind == kind &&
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    private void MarkPersistDirty(string reason)
    {
        this.persistDirty = true;
        this.lastPersistDirtyUtc = DateTime.UtcNow;
        this.log.Debug("[Persist] MarkDirty reason={Reason}", reason);
    }

    private void SaveDirtyConfigurationIfDue()
    {
        if (!this.persistDirty)
            return;

        if ((DateTime.UtcNow - this.lastPersistDirtyUtc).TotalMilliseconds < 800)
            return;

        this.log.Debug("[Persist] Save begin.");
        this.saveConfiguration();
        this.persistDirty = false;
        this.log.Debug("[Persist] Save end.");
    }

    private void SyncLocalBgPartSnapshotsDebounced()
    {
        if ((DateTime.UtcNow - this.lastLocalBgPartSnapshotUtc).TotalMilliseconds < 1200)
            return;

        this.lastLocalBgPartSnapshotUtc = DateTime.UtcNow;
        this.SyncLocalBgPartSnapshots();
    }

    private void SyncLocalActorSnapshotsDebounced()
    {
        if ((DateTime.UtcNow - this.lastLocalActorSnapshotUtc).TotalMilliseconds < 1200)
            return;

        this.lastLocalActorSnapshotUtc = DateTime.UtcNow;
        this.SyncLocalActorSnapshots();
    }

    private void SyncLocalActorSnapshots()
    {
        var territory = this.getTerritoryType();
        if (territory == 0)
            return;

        var active = this.actors.Actors
            .Where(actor => !actor.IsStale)
            .Where(actor => actor.IsValid || actor.IsReady)
            .Where(actor => actor.TerritoryId == 0 || actor.TerritoryId == territory)
            .OrderBy(actor => actor.SortOrder)
            .ThenBy(actor => actor.SpawnSequence)
            .ToList();

        var changed = false;
        for (var i = 0; i < active.Count; i++)
        {
            var actor = active[i];
            var actorPosition = actor.HasSavedTransform ? actor.TransformEditPosition : actor.SpawnPosition;
            var record = this.LocalActorRecords.FirstOrDefault(item =>
                    string.Equals(item.RuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase)) ??
                this.LocalActorRecords.FirstOrDefault(item =>
                    item.TerritoryId == territory &&
                    string.Equals(item.NpcId, actor.NpcId, StringComparison.OrdinalIgnoreCase) &&
                    item.SortOrder == actor.SortOrder &&
                    Vector3.Distance(item.WorldPosition, actorPosition) < 0.25f);
            if (record == null)
            {
                record = new SceneEditorLocalActorRecord
                {
                    RuntimeId = actor.RuntimeId,
                    RecordId = Guid.NewGuid().ToString("N"),
                };
                this.LocalActorRecords.Add(record);
                changed = true;
            }

            changed |= this.CopyLocalActorToRecord(actor, record, territory);
        }

        if (changed)
            this.MarkPersistDirty("LocalActor snapshot sync");
    }

    private bool CopyLocalActorToRecord(RuntimeActorInstance actor, SceneEditorLocalActorRecord record, uint territory)
    {
        var position = actor.HasSavedTransform ? actor.TransformEditPosition : actor.SpawnPosition;
        var rotation = ActorTransformUtil.NormalizeRotation(actor.HasSavedTransform ? actor.TransformEditRotationEuler : actor.SpawnRotationEuler);
        var scale = ActorTransformUtil.NormalizeScale(actor.HasSavedTransform ? actor.TransformEditScale : actor.SpawnScale);
        var sortOrder = actor.SortOrder == int.MaxValue ? this.LocalActorRecords.IndexOf(record) : actor.SortOrder;
        var changed =
            record.NpcId != actor.NpcId ||
            record.DisplayName != FirstNonEmpty(actor.DisplayName, actor.NpcName, actor.NpcId) ||
            record.TerritoryId != territory ||
            record.SortOrder != sortOrder ||
            !record.Enabled ||
            record.WorldPosition != position ||
            record.WorldRotationEuler != rotation ||
            record.WorldScale != scale;

        if (!changed)
            return false;

        record.RuntimeId = actor.RuntimeId;
        record.NpcId = actor.NpcId;
        record.DisplayName = FirstNonEmpty(actor.DisplayName, actor.NpcName, actor.NpcId);
        record.TerritoryId = territory;
        record.SortOrder = sortOrder;
        record.Enabled = true;
        record.WorldPosition = position;
        record.WorldRotationEuler = rotation;
        record.WorldScale = scale;
        record.LastSavedAt = DateTime.UtcNow;
        record.RestoreStatus = "Saved";
        this.log.Information("[ActorRestore] saved local actor snapshot runtime={RuntimeId} npc={NpcId} territory={Territory} order={Order} pos={Position} rot={Rotation} scale={Scale}",
            actor.RuntimeId,
            actor.NpcId,
            territory,
            sortOrder,
            position,
            rotation,
            scale);
        return true;
    }

    private void SyncLocalActorRecord(string runtimeId, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var actor = this.GetLocalActor(runtimeId);
        if (actor == null)
            return;

        var normalizedRotation = ActorTransformUtil.NormalizeRotation(rotationEuler);
        var normalizedScale = ActorTransformUtil.NormalizeScale(scale);
        var record = this.LocalActorRecords.FirstOrDefault(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            record = new SceneEditorLocalActorRecord
            {
                RuntimeId = runtimeId,
                NpcId = actor.NpcId,
                RecordId = Guid.NewGuid().ToString("N"),
            };
            this.LocalActorRecords.Add(record);
        }

        record.NpcId = actor.NpcId;
        record.DisplayName = FirstNonEmpty(actor.DisplayName, actor.NpcName, actor.NpcId);
        record.TerritoryId = this.getTerritoryType();
        record.SortOrder = actor.SortOrder;
        record.Enabled = true;
        record.WorldPosition = position;
        record.WorldRotationEuler = normalizedRotation;
        record.WorldScale = normalizedScale;
        record.LastSavedAt = DateTime.UtcNow;
        record.RestoreStatus = "Saved";
        this.log.Information("[ActorRestore] saved local actor record runtime={RuntimeId} npc={NpcId} order={Order} pos={Position} yaw={Yaw} scale={Scale}", runtimeId, record.NpcId, record.SortOrder, position, normalizedRotation.Y, record.WorldScale);
    }

    private void SyncLocalBgPartSnapshots()
    {
        var active = this.localLayoutObjects.Instances
            .Where(item => !item.IsRestored && !item.IsInvalid && !item.IsDuplicate)
            .ToList();

        var changed = false;
        for (var i = 0; i < active.Count; i++)
        {
            var instance = active[i];
            var record = this.LocalBgPartRecords.FirstOrDefault(item => string.Equals(item.InstanceId, instance.Id, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                record = new SceneEditorLocalBgPartRecord { InstanceId = instance.Id };
                this.LocalBgPartRecords.Add(record);
                changed = true;
            }

            changed |= this.CopyLocalBgPartToRecord(instance, record, i);
        }

        if (changed)
            this.MarkPersistDirty("LocalBgPart snapshot sync");
    }

    private bool CopyLocalBgPartToRecord(LocalLayoutObjectInstance instance, SceneEditorLocalBgPartRecord record, int sortOrder)
    {
        var changed = false;
        changed |= SetPropertyIfChanged(record.SortOrder, sortOrder, value => record.SortOrder = value);
        changed |= SetPropertyIfChanged(record.TerritoryId, this.getTerritoryType(), value => record.TerritoryId = value);
        changed |= SetPropertyIfChanged(record.SourceMdlPath, FirstNonEmpty(instance.TemplateResourcePath, instance.SourceResourcePath, instance.CurrentResourcePath, instance.CustomModelPath), value => record.SourceMdlPath = value);
        changed |= SetPropertyIfChanged(record.CurrentMdlPath, FirstNonEmpty(instance.CurrentResourcePath, instance.CustomModelPath, instance.TemplateResourcePath), value => record.CurrentMdlPath = value);
        changed |= SetPropertyIfChanged(record.CustomMdlPath, instance.CustomModelPath, value => record.CustomMdlPath = value);
        changed |= SetPropertyIfChanged(record.CollisionMode, instance.TransformMode, value => record.CollisionMode = value);
        changed |= SetPropertyIfChanged(record.SourceBgPartStableKey, FirstNonEmpty(instance.SourceParentKey, instance.OccupiedSlotAddress, instance.TemplateSourceSlotAddress), value => record.SourceBgPartStableKey = value);
        changed |= SetPropertyIfChanged(record.SourceKind, instance.SourceKind, value => record.SourceKind = value);
        changed |= SetPropertyIfChanged(record.SourceSharedGroupPath, instance.SourceSharedGroupPath, value => record.SourceSharedGroupPath = value);
        changed |= SetPropertyIfChanged(record.SourceChildIndex, instance.SourceChildIndex, value => record.SourceChildIndex = value);
        changed |= SetPropertyIfChanged(record.Enabled, !instance.IsRestored && !instance.IsInvalid, value => record.Enabled = value);
        changed |= SetPropertyIfChanged(record.Hidden, !instance.Visible, value => record.Hidden = value);
        changed |= SetPropertyIfChanged(record.RestoreStatus, string.IsNullOrWhiteSpace(instance.RestoreStatus) ? instance.InstanceState : instance.RestoreStatus, value => record.RestoreStatus = value);
        changed |= SetVectorDataIfChanged(record.WorldPosition, instance.CurrentPosition);
        changed |= SetVectorDataIfChanged(record.WorldRotationEuler, instance.CurrentRotationEuler);
        changed |= SetVectorDataIfChanged(record.WorldScale, WorldTransformUtil.NormalizeScale(instance.CurrentScale));
        if (changed)
            record.LastSavedAt = DateTime.UtcNow;
        return changed;
    }

    private static bool SetIfChanged<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        return true;
    }

    private static bool SetPropertyIfChanged<T>(T current, T value, Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;

        setter(value);
        return true;
    }

    private static bool SetVectorDataIfChanged(Vector3Data data, Vector3 value)
    {
        if (MathF.Abs(data.X - value.X) <= 0.001f &&
            MathF.Abs(data.Y - value.Y) <= 0.001f &&
            MathF.Abs(data.Z - value.Z) <= 0.001f)
        {
            return false;
        }

        SetVector3Data(data, value);
        return true;
    }

    private bool IsCurrentTerritoryRecord(SceneEditorNativeModificationRecord record)
        => record.TerritoryId != 0 && record.TerritoryId == this.getTerritoryType();

    private WorldTransform ReadNativeTransformOrFallback(SceneEditableRef selected)
    {
        if (!this.CanRunRestoreNow(out _))
            return selected.Transform;

        if (selected.Kind is SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight && selected.NativePtr != 0)
        {
            try
            {
                var pointer = (ILayoutInstance*)selected.NativePtr;
                var native = pointer->GetTransformImpl();
                if (native != null)
                {
                    return WorldTransform.FromEuler(
                        native->Translation,
                        WorldTransformUtil.QuaternionToWorldEulerRadians(native->Rotation),
                        native->Scale);
                }
            }
            catch
            {
            }
        }

        return selected.Transform;
    }

    private void RecordNativeTransformChange(SceneEditableRef before, WorldTransform after, string reason)
    {
        if (!CanTrackNativeModification(before))
            return;

        var beforeTransform = before.Transform;
        var record = this.EnsureNativeRecord(before, beforeTransform, reason);
        SetRecordCurrentTransform(record, after);
        record.IsModified = !TransformsApproximatelyEqual(GetOriginalTransform(record), after) || record.IsHidden;
        if (!record.IsHidden)
            record.Status = record.IsModified ? "Modified" : "Restored";
        record.Reason = reason;
        record.LastModifiedAt = DateTime.UtcNow;
        this.MarkPersistDirty($"Native transform {reason}");
    }

    private SceneEditorNativeModificationRecord EnsureNativeRecord(SceneEditableRef selected, WorldTransform original, string reason)
    {
        var stableKey = this.GetStableKey(selected);
        var record = this.NativeRecords.FirstOrDefault(item =>
            string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (record != null)
        {
            record.NativeAddress = string.Empty;
            return record;
        }

        record = new SceneEditorNativeModificationRecord
        {
            RecordId = Guid.NewGuid().ToString("N"),
            StableKey = stableKey,
            RuntimeIdAtRecordTime = selected.RuntimeId,
            NativeAddress = string.Empty,
            Kind = selected.Kind,
            DisplayName = selected.DisplayName,
            MdlPath = selected.MdlPath,
            TerritoryId = this.getTerritoryType(),
            ObjectIndexAtRecordTime = selected.ObjectIndex,
            DataId = selected.DataId,
            IsInteractableNpc = selected.IsInteractableNpc || selected.Kind == SceneEditableKind.EventNpc,
            RuntimeOnly = string.IsNullOrWhiteSpace(selected.StableKey),
            UseFullLayoutTransform = selected.Kind is SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight && this.NativeFullLayoutTransformConfirmed,
            Reason = reason,
            Status = "Modified",
        };
        SetRecordOriginalTransform(record, original);
        SetRecordCurrentTransform(record, original);
        this.NativeRecords.Add(record);
        return record;
    }

    private bool ApplyNativeRecordTransform(SceneEditorNativeModificationRecord record, WorldTransform transform)
    {
        if (record.TerritoryId == 0)
        {
            record.Status = "LegacyNoTerritory";
            this.LastStatus = "Native restore skipped: legacy record has no TerritoryId.";
            this.log.Information("[RestoreNative] skipped legacy no territory stableKey={StableKey} kind={Kind} name={Name}", record.StableKey, record.Kind, record.DisplayName);
            return false;
        }

        if (record.TerritoryId != this.getTerritoryType())
        {
            this.LastStatus = "Native restore skipped: record belongs to another territory.";
            return false;
        }

        if (!this.CanRunRestoreNow(out var reason))
        {
            this.LastStatus = $"Native restore skipped: {reason}.";
            return false;
        }

        this.lastNativeScanUtc = DateTime.MinValue;
        this.nativeCache.Clear();
        var current = this.ResolveNativeObjectFresh(record);

        if (current == null || current.NativePtr == 0 || current.IsPlayer)
        {
            this.LastStatus = $"Native restore resolve failed: {record.StableKey}";
            this.log.Warning("[Restore] Resolve native failed stableKey={StableKey} kind={Kind} name={Name}", record.StableKey, record.Kind, record.DisplayName);
            return false;
        }

        return record.Kind switch
        {
            SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight
                => this.ApplyFreshNativeLayoutTransform(record, current, transform),
            SceneEditableKind.NativeActor or SceneEditableKind.EventNpc
                => this.ApplyNativeActorTransformAddress(record.RuntimeIdAtRecordTime, current.NativePtr, transform),
            _ => false,
        };
    }

    private SceneEditableRef? ResolveNativeObjectFresh(SceneEditorNativeModificationRecord record)
    {
        if (!this.IsCurrentTerritoryRecord(record))
        {
            this.log.Information("[RestoreNative] skipped territory mismatch stableKey={StableKey} recordTerritory={RecordTerritory} currentTerritory={CurrentTerritory}", record.StableKey, record.TerritoryId, this.getTerritoryType());
            return null;
        }

        this.log.Information("[RestoreNative] resolve begin stableKey={StableKey} kind={Kind} name={Name}", record.StableKey, record.Kind, record.DisplayName);
        var candidates = this.GetNativeEditablesForRestore()
            .Where(item => item.IsNativeGameObject)
            .Where(item => !item.IsPlayer)
            .Where(item => item.Kind == record.Kind)
            .ToList();

        var exact = candidates.FirstOrDefault(item =>
            string.Equals(this.GetStableKey(item), record.StableKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.StableKey, record.StableKey, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            this.log.Information("[RestoreNative] resolve exact stableKey={StableKey} ptr={Ptr}", record.StableKey, exact.NativePtr);
            return exact;
        }

        var original = GetOriginalTransform(record).WorldPosition;
        var current = GetCurrentTransform(record).WorldPosition;
        var hidden = ToVector3(record.HiddenPosition);
        var scored = candidates
            .Select(item => new { Item = item, Score = this.ScoreNativeCandidate(record, item, original, current, hidden) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Item.ObjectIndex)
            .ToList();
        var best = scored.FirstOrDefault();

        if (best == null || best.Score < 25)
        {
            this.log.Warning("[RestoreNative] resolve failed stableKey={StableKey} candidateCount={Count} bestScore={Score}", record.StableKey, candidates.Count, best?.Score ?? 0);
            return null;
        }

        var ambiguousCount = scored.Count(item => item.Score == best.Score);
        if (ambiguousCount > 1)
        {
            record.Status = "Ambiguous";
            this.log.Warning("[RestoreNative] resolve ambiguous stableKey={StableKey} candidateCount={Count} bestScore={Score} tied={Tied}", record.StableKey, candidates.Count, best.Score, ambiguousCount);
            return null;
        }

        this.log.Information("[RestoreNative] resolve fuzzy stableKey={StableKey} ptr={Ptr} score={Score} candidate={Candidate}", record.StableKey, best.Item.NativePtr, best.Score, best.Item.DisplayName);
        return best.Item;
    }

    private int ScoreNativeCandidate(SceneEditorNativeModificationRecord record, SceneEditableRef item, Vector3 original, Vector3 current, Vector3 hidden)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(record.MdlPath) && string.Equals(record.MdlPath, item.MdlPath, StringComparison.OrdinalIgnoreCase))
            score += 50;
        if (!string.IsNullOrWhiteSpace(record.DataId) && string.Equals(record.DataId, item.DataId, StringComparison.OrdinalIgnoreCase))
            score += 35;
        if (!string.IsNullOrWhiteSpace(record.DisplayName) && string.Equals(record.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase))
            score += 20;
        if (!string.IsNullOrWhiteSpace(record.MdlPath) && item.DisplayName.Contains(record.MdlPath, StringComparison.OrdinalIgnoreCase))
            score += 10;
        if (Vector3.Distance(item.Transform.WorldPosition, original) <= 2f)
            score += 25;
        if (Vector3.Distance(item.Transform.WorldPosition, current) <= 2f)
            score += 20;
        if (hidden != Vector3.Zero && Vector3.Distance(item.Transform.WorldPosition, hidden) <= 2f)
            score += 20;
        if (record.ObjectIndexAtRecordTime >= 0 && record.ObjectIndexAtRecordTime == item.ObjectIndex)
            score += 5;
        return score;
    }

    private bool IsHiddenRecord(string stableKey)
        => !string.IsNullOrWhiteSpace(stableKey) &&
           this.NativeRecords.Any(item =>
               item.IsHidden &&
               string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));

    private string GetStableKey(SceneEditableRef selected)
    {
        if (!string.IsNullOrWhiteSpace(selected.StableKey))
            return selected.StableKey;

        return $"{this.getTerritoryType()}:{selected.Kind}:{selected.ObjectKind}:data={selected.DataId}:mdl={selected.MdlPath}:name={selected.DisplayName}:pos={FormatStablePosition(selected.Transform.WorldPosition)}";
    }

    private static string FormatStablePosition(Vector3 position)
        => $"{MathF.Round(position.X, 1):F1},{MathF.Round(position.Y, 1):F1},{MathF.Round(position.Z, 1):F1}";

    private static bool CanTrackNativeModification(SceneEditableRef selected)
        => selected.IsNativeGameObject &&
           !selected.IsPlayer &&
           selected.Kind is SceneEditableKind.NativeBgPart or SceneEditableKind.NativeActor or SceneEditableKind.EventNpc or SceneEditableKind.NativeLight;

    private static bool TransformsApproximatelyEqual(WorldTransform left, WorldTransform right)
        => Vector3.Distance(left.WorldPosition, right.WorldPosition) <= 0.01f &&
           Vector3.Distance(left.WorldEulerRadians, right.WorldEulerRadians) <= 0.001f &&
           Vector3.Distance(left.WorldScale, right.WorldScale) <= 0.001f;

    private static WorldTransform GetOriginalTransform(SceneEditorNativeModificationRecord record)
        => WorldTransform.FromEuler(
            ToVector3(record.OriginalPosition),
            ToVector3(record.OriginalRotationEuler),
            ToVector3(record.OriginalScale));

    private static WorldTransform GetCurrentTransform(SceneEditorNativeModificationRecord record)
        => WorldTransform.FromEuler(
            ToVector3(record.CurrentPosition),
            ToVector3(record.CurrentRotationEuler),
            ToVector3(record.CurrentScale));

    private static WorldTransform GetHiddenOrCurrentTransform(SceneEditorNativeModificationRecord record)
    {
        var hidden = ToVector3(record.HiddenPosition);
        return hidden == Vector3.Zero
            ? GetCurrentTransform(record)
            : WorldTransform.FromEuler(
                hidden,
                ToVector3(record.HiddenRotationEuler),
                ToVector3(record.HiddenScale));
    }

    private static void SetRecordOriginalTransform(SceneEditorNativeModificationRecord record, WorldTransform transform)
    {
        SetVector3Data(record.OriginalPosition, transform.WorldPosition);
        SetVector3Data(record.OriginalRotationEuler, transform.WorldEulerRadians);
        SetVector3Data(record.OriginalScale, WorldTransformUtil.NormalizeScale(transform.WorldScale));
    }

    private static void SetRecordCurrentTransform(SceneEditorNativeModificationRecord record, WorldTransform transform)
    {
        SetVector3Data(record.CurrentPosition, transform.WorldPosition);
        SetVector3Data(record.CurrentRotationEuler, transform.WorldEulerRadians);
        SetVector3Data(record.CurrentScale, WorldTransformUtil.NormalizeScale(transform.WorldScale));
    }

    private static Vector3 ToVector3(Vector3Data data)
        => new(data.X, data.Y, data.Z);

    private static void SetVector3Data(Vector3Data data, Vector3 value)
    {
        data.X = value.X;
        data.Y = value.Y;
        data.Z = value.Z;
    }

    private static int ParseObjectIndex(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index;

        return -1;
    }

    private static nint ParsePointer(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        return long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? (nint)parsed
            : 0;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private IReadOnlyList<SceneEditableRef> GetNativeEditables()
    {
        var now = DateTime.UtcNow;
        if ((now - this.lastNativeScanUtc).TotalMilliseconds < 850)
            return this.nativeCache;

        this.lastNativeScanUtc = now;
        this.nativeCache.Clear();

        var pluginPointers = this.GetPluginNativePointers();
        try
        {
            if (this.ShowActors)
                this.AddNativeActors(this.nativeCache, pluginPointers);

            if (this.ShowBgParts || this.ShowLights)
                this.AddNativeLayoutObjects(this.nativeCache, pluginPointers, restoreScan: false);

            this.LastNativeScanStatus = $"Native scan: {this.nativeCache.Count} readonly markers.";
        }
        catch (Exception ex)
        {
            this.LastNativeScanStatus = $"Native scan failed: {ex.Message}";
            this.log.Warning(ex, "[SceneEditor] Native scan failed.");
        }

        return this.nativeCache;
    }

    private HashSet<nint> GetPluginNativePointers()
    {
        var result = new HashSet<nint>();
        foreach (var actor in this.actors.Actors)
        {
            var ptr = ParsePointer(actor.Address);
            if (ptr != 0)
                result.Add(ptr);
        }

        foreach (var instance in this.localLayoutObjects.Instances)
        {
            var ptr = ParsePointer(instance.GraphicsObjectAddress);
            if (ptr != 0)
                result.Add(ptr);
        }

        foreach (var light in this.localLights.Instances)
        {
            if (light.NativeSceneLight != 0)
                result.Add(light.NativeSceneLight);
        }

        return result;
    }

    private IReadOnlyList<SceneEditableRef> GetNativeEditablesForRestore()
    {
        var result = new List<SceneEditableRef>();
        var pluginPointers = this.GetPluginNativePointers();
        try
        {
            this.AddNativeActors(result, pluginPointers);
            this.AddNativeLayoutObjects(result, pluginPointers, restoreScan: true);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[RestoreNative] fresh native scan failed.");
        }

        return result;
    }

    private void AddNativeActors(List<SceneEditableRef> result, HashSet<nint> pluginPointers)
    {
        nint localPlayerAddress = 0;
        if (this.objectTable.LocalPlayer != null)
            TryGetAddress(this.objectTable.LocalPlayer, out localPlayerAddress);

        foreach (var obj in this.objectTable)
        {
            if (obj == null || !TryGetAddress(obj, out var address) || address == 0)
                continue;

            if (pluginPointers.Contains(address))
                continue;

            var objectKind = ReadManagedMember(obj, "ObjectKind");
            var isPlayer = address == localPlayerAddress;
            if (!isPlayer && !IsActorObjectKind(objectKind))
                continue;

            if (!TryReadVector3(obj, "Position", out var position))
                continue;

            var index = ReadIntMember(obj, "ObjectIndex", "ObjectTableIndex", "Index");
            var dataId = ReadManagedMember(obj, "DataId", "BaseId");
            var rotation = ReadFloatMember(obj, "Rotation", "Yaw") is { } yaw
                ? new Vector3(0f, yaw, 0f)
                : Vector3.Zero;
            var name = ReadManagedMember(obj, "Name");
            if (string.IsNullOrWhiteSpace(name))
                name = isPlayer ? "LocalPlayer" : objectKind;

            var kind = isPlayer
                ? SceneEditableKind.Player
                : objectKind.Contains("Event", StringComparison.OrdinalIgnoreCase)
                    ? SceneEditableKind.EventNpc
                    : SceneEditableKind.NativeActor;
            var stableKey = $"{this.getTerritoryType()}:{kind}:{objectKind}:data={dataId}:name={name}:pos={FormatStablePosition(position)}";
            result.Add(new SceneEditableRef(
                $"native-actor:{address:X}",
                kind,
                address,
                index,
                name,
                objectKind,
                false,
                WorldTransform.FromEuler(position, rotation, Vector3.One),
                true,
                this.IsHiddenRecord(stableKey))
            {
                IsNativeGameObject = true,
                IsPlayer = isPlayer,
                IsInteractableNpc = kind == SceneEditableKind.EventNpc || (!isPlayer && objectKind.Contains("Npc", StringComparison.OrdinalIgnoreCase)),
                StableKey = stableKey,
                TransformEditable = !isPlayer && this.AllowNativeTransformWrites,
                ObjectKind = objectKind,
                DataId = dataId,
                NativeInfo = $"ObjectIndex={index}; DataId={dataId}; Address=0x{address:X}",
            });
        }
    }

    private void AddNativeLayoutObjects(List<SceneEditableRef> result, HashSet<nint> pluginPointers, bool restoreScan)
    {
        var previousNearbyOnly = this.layoutProbe.NearbyOnly;
        var previousMaxDistance = this.layoutProbe.MaxDistance;
        var previousSortByDistance = this.layoutProbe.SortByDistance;
        var previousShowBgPart = this.layoutProbe.ShowBgPart;
        var previousShowSharedGroup = this.layoutProbe.ShowSharedGroup;
        var previousShowLight = this.layoutProbe.ShowLight;
        var previousShowTerrain = this.layoutProbe.ShowTerrain;
        var previousShowCamera = this.layoutProbe.ShowCamera;
        var previousShowCharacter = this.layoutProbe.ShowCharacter;
        var previousTypeFilter = this.layoutProbe.TypeFilter;

        try
        {
            this.layoutProbe.NearbyOnly = !restoreScan;
            this.layoutProbe.MaxDistance = restoreScan ? 10000f : 80f;
            this.layoutProbe.SortByDistance = true;
            this.layoutProbe.ShowBgPart = restoreScan || this.ShowBgParts;
            this.layoutProbe.ShowSharedGroup = false;
            this.layoutProbe.ShowLight = restoreScan || this.ShowLights;
            this.layoutProbe.ShowTerrain = false;
            this.layoutProbe.ShowCamera = false;
            this.layoutProbe.ShowCharacter = false;
            this.layoutProbe.TypeFilter = string.Empty;
            this.layoutProbe.EnumerateInstances(this.playerPositionProvider());

            foreach (var instance in this.layoutProbe.Instances.Take(restoreScan ? 4000 : 160))
            {
                var address = ParsePointer(instance.Address);
                if (address != 0 && pluginPointers.Contains(address))
                    continue;

                var kind = instance.Type.Equals("Light", StringComparison.OrdinalIgnoreCase)
                    ? SceneEditableKind.NativeLight
                    : SceneEditableKind.NativeBgPart;
                if (!restoreScan && kind == SceneEditableKind.NativeBgPart && !this.ShowBgParts)
                    continue;
                if (!restoreScan && kind == SceneEditableKind.NativeLight && !this.ShowLights)
                    continue;

                result.Add(new SceneEditableRef(
                    $"native-layout:{instance.Type}:{instance.Address}:{instance.Key}",
                    kind,
                    address,
                    -1,
                    string.IsNullOrWhiteSpace(instance.ResourcePath) ? instance.Type : instance.ResourcePath,
                    instance.ResourcePath,
                    false,
                    WorldTransform.FromEuler(instance.Position, Vector3.Zero, instance.Scale == Vector3.Zero ? Vector3.One : instance.Scale),
                    true,
                    !instance.Visible || this.IsHiddenRecord(instance.Key))
                {
                    IsNativeGameObject = true,
                    StableKey = instance.Key,
                    TransformEditable = this.AllowNativeTransformWrites,
                    ObjectKind = instance.Type,
                    RotationText = instance.Rotation,
                    NativeInfo = instance.DebugInfo,
                    LightInfo = kind == SceneEditableKind.NativeLight ? instance.DebugInfo : string.Empty,
                    LayoutProbe = instance,
                });
            }
        }
        finally
        {
            this.layoutProbe.NearbyOnly = previousNearbyOnly;
            this.layoutProbe.MaxDistance = previousMaxDistance;
            this.layoutProbe.SortByDistance = previousSortByDistance;
            this.layoutProbe.ShowBgPart = previousShowBgPart;
            this.layoutProbe.ShowSharedGroup = previousShowSharedGroup;
            this.layoutProbe.ShowLight = previousShowLight;
            this.layoutProbe.ShowTerrain = previousShowTerrain;
            this.layoutProbe.ShowCamera = previousShowCamera;
            this.layoutProbe.ShowCharacter = previousShowCharacter;
            this.layoutProbe.TypeFilter = previousTypeFilter;
        }
    }

    private static bool IsActorObjectKind(string objectKind)
        => objectKind.Contains("Player", StringComparison.OrdinalIgnoreCase) ||
           objectKind.Contains("Npc", StringComparison.OrdinalIgnoreCase) ||
           objectKind.Contains("Battle", StringComparison.OrdinalIgnoreCase) ||
           objectKind.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
           objectKind.Contains("Companion", StringComparison.OrdinalIgnoreCase);

    private enum LocalBgPartRestoreState
    {
        Pending,
        ResolvingSource,
        CreatingCopy,
        WaitingInstanceRegistered,
        WaitingNativeReady,
        ApplyingTransform,
        VerifyingTransform,
        Done,
        Failed,
    }

    private sealed class LocalBgPartRestoreItem
    {
        public LocalBgPartRestoreItem(SceneEditorLocalBgPartRecord record, WorldTransform target)
        {
            this.Record = record;
            this.Target = target;
            this.InstanceId = record.InstanceId;
        }

        public SceneEditorLocalBgPartRecord Record { get; }

        public WorldTransform Target { get; }

        public LocalBgPartRestoreState State { get; set; } = LocalBgPartRestoreState.Pending;

        public string InstanceId { get; set; }

        public int WaitTicks { get; set; }

        public int Attempts { get; set; }

        public LayoutProbeInstance? Template { get; set; }

        public IReadOnlyList<LayoutProbeInstance> Candidates { get; set; } = [];
    }

    private readonly record struct FreshNativeLayoutTarget(
        nint Address,
        uint TerritoryId,
        int SceneGeneration,
        SceneEditableKind Kind,
        string StableKey,
        bool IsFresh,
        string ResolveReason);

    private static bool TryGetAddress(object source, out nint address)
    {
        address = 0;
        var raw = ReadRawMember(source, "Address");
        if (raw == null)
            return false;

        if (raw is nint nativeInt)
        {
            address = nativeInt;
            return address != 0;
        }

        if (raw is IntPtr pointer)
        {
            address = pointer;
            return address != 0;
        }

        var text = raw.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            address = (nint)hexValue;
            return address != 0;
        }

        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            address = (nint)value;
            return address != 0;
        }

        return false;
    }

    private static string ReadManagedMember(object source, params string[] names)
        => ReadRawMember(source, names)?.ToString() ?? string.Empty;

    private static object? ReadRawMember(object source, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null)
                    return property.GetValue(source);

                var field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field != null)
                    return field.GetValue(source);
            }
            catch
            {
            }
        }

        return null;
    }

    private static int ReadIntMember(object source, params string[] names)
    {
        var raw = ReadRawMember(source, names);
        if (raw == null)
            return -1;

        if (raw is int intValue)
            return intValue;
        if (raw is uint uintValue)
            return unchecked((int)uintValue);
        if (raw is short shortValue)
            return shortValue;
        if (raw is ushort ushortValue)
            return ushortValue;
        return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
    }

    private static float? ReadFloatMember(object source, params string[] names)
    {
        var raw = ReadRawMember(source, names);
        if (raw == null)
            return null;

        if (raw is float floatValue)
            return floatValue;
        if (raw is double doubleValue)
            return (float)doubleValue;
        return float.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryReadVector3(object source, string name, out Vector3 value)
    {
        value = Vector3.Zero;
        var raw = ReadRawMember(source, name);
        if (raw is Vector3 vector)
        {
            value = vector;
            return true;
        }

        return false;
    }

    private bool ApplyNativeLayoutTransform(string runtimeId, WorldTransform transform)
    {
        var current = this.GetEditables().FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        var slot = current?.LayoutProbe;
        var address = slot == null ? 0 : ParsePointer(slot.Address);
        if (slot == null || address == 0)
        {
            this.LastStatus = "Native layout transform failed: layout instance address unavailable.";
            return false;
        }

        var target = new FreshNativeLayoutTarget(
            address,
            this.getTerritoryType(),
            this.sceneGeneration,
            current!.Kind,
            this.GetStableKey(current),
            IsFresh: true,
            ResolveReason: "interactive current frame");
        return this.ApplyFreshNativeLayoutTransform(runtimeId, target, transform, this.NativeFullLayoutTransformConfirmed);
    }

    private bool ApplyFreshNativeLayoutTransform(SceneEditorNativeModificationRecord record, SceneEditableRef current, WorldTransform transform)
    {
        var target = new FreshNativeLayoutTarget(
            current.NativePtr,
            this.getTerritoryType(),
            this.sceneGeneration,
            current.Kind,
            this.GetStableKey(current),
            IsFresh: true,
            ResolveReason: "ResolveNativeObjectFresh");
        return this.ApplyFreshNativeLayoutTransform(record.RuntimeIdAtRecordTime, target, transform, record.UseFullLayoutTransform);
    }

    private bool ApplyFreshNativeLayoutTransform(string runtimeId, FreshNativeLayoutTarget target, WorldTransform transform, bool fullLayoutConfirmed)
    {
        if (!this.ValidateFreshNativeLayoutTarget(target, out var validationReason))
        {
            this.LastStatus = $"Native layout transform skipped: {validationReason}";
            this.log.Warning("[RestoreNative] Apply native skipped stale pointer id={Id} stableKey={StableKey} reason={Reason}", runtimeId, target.StableKey, validationReason);
            return false;
        }

        try
        {
            var pointer = (ILayoutInstance*)target.Address;
            if (pointer->Id.Type == InstanceType.BgPart && !fullLayoutConfirmed)
            {
                if (!TryGetGraphicsObjectAddress(pointer, out var graphicsObjectAddress))
                {
                    this.LastStatus = "Native BgPart visual transform failed: GraphicsObject unavailable.";
                    this.LastBgPartCollisionOperation = $"source=Native; collisionMode=Off; collision operation=Failed; handle=0x{target.Address:X}; GraphicsObject unavailable";
                    return false;
                }

                var graphicsTarget = target with { Address = graphicsObjectAddress, ResolveReason = $"{target.ResolveReason}; GraphicsObject" };
                if (!this.TryWriteSceneObjectTransform(graphicsTarget, transform, out var writeReason))
                {
                    this.LastStatus = $"Native BgPart visual transform failed: {writeReason}";
                    this.LastBgPartCollisionOperation = $"source=Native; collisionMode=Off; collision operation=Failed; handle=0x{target.Address:X}; {writeReason}";
                    return false;
                }

                this.TransformGeneration++;
                this.lastNativeScanUtc = DateTime.MinValue;
                this.LastBgPartCollisionOperation = $"source=Native; collisionMode=Off; collision operation=Skipped; handle=0x{target.Address:X}; graphics=0x{graphicsObjectAddress:X}";
                this.LastStatus = $"Native BgPart visual-only transform applied: {runtimeId}";
                return true;
            }

            var nativeTransform = new Transform
            {
                Translation = transform.WorldPosition,
                Rotation = transform.WorldRotation,
                Scale = WorldTransformUtil.NormalizeScale(transform.WorldScale),
            };
            pointer->SetTransform(&nativeTransform);
            this.TransformGeneration++;
            this.lastNativeScanUtc = DateTime.MinValue;
            if (pointer->Id.Type == InstanceType.BgPart)
                this.LastBgPartCollisionOperation = $"source=Native; collisionMode=On; collision operation=Moved; handle=0x{target.Address:X}";
            this.LastStatus = $"Native layout transform applied: {runtimeId}";
            return true;
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Native layout transform failed: {ex.Message}";
            this.LastBgPartCollisionOperation = $"source=Native; collisionMode={(fullLayoutConfirmed ? "On" : "Off")}; collision operation=Failed; handle=0x{target.Address:X}; {ex.Message}";
            this.log.Warning(ex, "[SceneEditor] Native layout transform failed. id={Id}", runtimeId);
            return false;
        }
    }

    private bool ValidateFreshNativeLayoutTarget(FreshNativeLayoutTarget target, out string reason)
    {
        if (target.Address == 0)
        {
            reason = "target pointer is zero";
            return false;
        }

        if (!target.IsFresh)
        {
            reason = "target was not resolved fresh";
            return false;
        }

        if (target.TerritoryId != this.getTerritoryType())
        {
            reason = $"territory mismatch target={target.TerritoryId}, current={this.getTerritoryType()}";
            return false;
        }

        if (target.SceneGeneration != this.sceneGeneration)
        {
            reason = $"scene generation mismatch target={target.SceneGeneration}, current={this.sceneGeneration}";
            return false;
        }

        if (!this.CanRunRestoreNow(out var pauseReason))
        {
            reason = pauseReason;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetGraphicsObjectAddress(ILayoutInstance* pointer, out nint graphicsObjectAddress)
    {
        graphicsObjectAddress = 0;
        try
        {
            if (pointer == null || pointer->Id.Type != InstanceType.BgPart)
                return false;

            var bgPart = (BgPartsLayoutInstance*)pointer;
            if (bgPart->GraphicsObject == null)
                return false;

            graphicsObjectAddress = (nint)bgPart->GraphicsObject;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryWriteSceneObjectTransform(FreshNativeLayoutTarget target, WorldTransform transform, out string reason)
    {
        if (!this.ValidateFreshNativeLayoutTarget(target, out reason))
            return false;

        var obj = (SceneObject*)target.Address;
        var bg = (SceneBgObject*)target.Address;
        obj->Position = transform.WorldPosition;
        obj->Rotation = transform.WorldRotation;
        obj->Scale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
        reason = string.Empty;
        return true;
    }

    private bool ApplyNativeActorTransform(string runtimeId, WorldTransform transform)
    {
        var current = this.GetEditables().FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (current == null || current.NativePtr == 0 || current.IsPlayer)
        {
            this.LastStatus = "Native actor transform failed: invalid target or LocalPlayer.";
            return false;
        }

        return this.ApplyNativeActorTransformAddress(runtimeId, current.NativePtr, transform);
    }

    private bool ApplyNativeActorTransformAddress(string runtimeId, nint address, WorldTransform transform)
    {
        if (address == 0)
        {
            this.LastStatus = "Native actor transform failed: invalid address.";
            return false;
        }

        try
        {
            if (this.objectTable.LocalPlayer != null &&
                TryGetAddress(this.objectTable.LocalPlayer, out var localPlayerAddress) &&
                localPlayerAddress == address)
            {
                this.LastStatus = "Native actor transform failed: LocalPlayer address is blocked.";
                return false;
            }

            var gameObject = (GameObject*)address;
            gameObject->Position = transform.WorldPosition;
            if (gameObject->DrawObject != null)
            {
                var drawObject = (DrawObject*)gameObject->DrawObject;
                drawObject->Position = transform.WorldPosition;
                drawObject->Rotation = transform.WorldRotation;
                drawObject->Scale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
            }

            this.TransformGeneration++;
            this.lastNativeScanUtc = DateTime.MinValue;
            this.LastStatus = $"Native actor transform applied: {runtimeId}";
            return true;
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Native actor transform failed: {ex.Message}";
            this.log.Warning(ex, "[SceneEditor] Native actor transform failed. id={Id}", runtimeId);
            return false;
        }
    }

    private LayoutProbeInstance? ResolveBgPartProbe(SceneEditableRef selected)
    {
        if (selected.LayoutProbe != null && selected.Kind == SceneEditableKind.NativeBgPart)
            return selected.LayoutProbe;

        if (selected.Kind != SceneEditableKind.LocalBgPart)
            return null;

        var instance = this.localLayoutObjects.GetById(selected.RuntimeId);
        if (instance == null)
            return null;

        return new LayoutProbeInstance
        {
            Type = "BgPart",
            Address = instance.OccupiedSlotAddress,
            Key = instance.OccupiedSlotAddress,
            SourceKind = instance.SourceKind,
            ResourcePath = FirstNonEmpty(instance.CurrentResourcePath, instance.CustomModelPath, instance.TemplateResourcePath, instance.SourceResourcePath),
            Position = instance.CurrentPosition,
            Rotation = instance.CurrentRotation.ToString(),
            Scale = instance.CurrentScale,
            SharedGroupPath = instance.SourceSharedGroupPath,
            ParentAddress = instance.SourceParentAddress,
            ParentKey = instance.SourceParentKey,
            ChildIndex = instance.SourceChildIndex,
        };
    }

    private IEnumerable<LayoutProbeInstance> GetCurrentBgPartCandidates()
    {
        var previousNearbyOnly = this.layoutProbe.NearbyOnly;
        var previousMaxDistance = this.layoutProbe.MaxDistance;
        var previousSortByDistance = this.layoutProbe.SortByDistance;
        var previousShowBgPart = this.layoutProbe.ShowBgPart;
        var previousShowSharedGroup = this.layoutProbe.ShowSharedGroup;
        var previousShowLight = this.layoutProbe.ShowLight;
        var previousShowTerrain = this.layoutProbe.ShowTerrain;
        var previousShowCamera = this.layoutProbe.ShowCamera;
        var previousShowCharacter = this.layoutProbe.ShowCharacter;
        var previousTypeFilter = this.layoutProbe.TypeFilter;

        try
        {
            this.layoutProbe.NearbyOnly = false;
            this.layoutProbe.MaxDistance = 10000f;
            this.layoutProbe.SortByDistance = true;
            this.layoutProbe.ShowBgPart = true;
            this.layoutProbe.ShowSharedGroup = false;
            this.layoutProbe.ShowLight = false;
            this.layoutProbe.ShowTerrain = false;
            this.layoutProbe.ShowCamera = false;
            this.layoutProbe.ShowCharacter = false;
            this.layoutProbe.TypeFilter = string.Empty;
            this.layoutProbe.EnumerateInstances(this.playerPositionProvider());
            return this.layoutProbe.Instances.Where(item => item.Type.Equals("BgPart", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        finally
        {
            this.layoutProbe.NearbyOnly = previousNearbyOnly;
            this.layoutProbe.MaxDistance = previousMaxDistance;
            this.layoutProbe.SortByDistance = previousSortByDistance;
            this.layoutProbe.ShowBgPart = previousShowBgPart;
            this.layoutProbe.ShowSharedGroup = previousShowSharedGroup;
            this.layoutProbe.ShowLight = previousShowLight;
            this.layoutProbe.ShowTerrain = previousShowTerrain;
            this.layoutProbe.ShowCamera = previousShowCamera;
            this.layoutProbe.ShowCharacter = previousShowCharacter;
            this.layoutProbe.TypeFilter = previousTypeFilter;
        }
    }
}
