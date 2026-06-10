using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Yourcraft.Models;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Yourcraft.Services;

public sealed unsafe class SceneEditorService
{
    private const int RequiredRestoreStableTicks = 60;
    private const float NativeHideDepthMeters = 50f;
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
    private readonly Queue<string> pendingLocalBgPartDeletes = new();
    private int activeInteractiveTransformEdits;

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

    public bool FixedOverlayPanelPosition { get; set; }

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

    public bool IsBgPartCollisionConfirmationRequired(SceneEditableKind kind)
        => kind is SceneEditableKind.LocalBgPart or SceneEditableKind.NativeBgPart &&
           this.BgPartCollisionModeEnabled &&
           !this.BgPartCollisionModeConfirmed;

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

    private bool BlockUnconfirmedBgPartCollisionTransform(SceneEditableKind kind, string source, string handle)
    {
        this.LastStatus = "BgPart transform blocked: Collision is enabled, but FullLayoutWithCollision has not been confirmed.";
        this.LastBgPartCollisionOperation = $"source={source}; collisionMode=OnPendingConfirmation; collision operation=Failed; handle={handle}; confirmation required";
        this.log.Warning("[SceneEditor] Blocked unconfirmed BgPart collision transform kind={Kind} source={Source} handle={Handle}", kind, source, handle);
        return false;
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

        this.ProcessPendingLocalBgPartDeletes();

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
            var territory = this.getTerritoryType();
            foreach (var light in this.localLights.Instances.Where(item => item.TerritoryId == territory))
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
        => this.ApplyWorldTransform(kind, runtimeId, transform, SceneEditorTransformComponents.All);

    public WorldTransform MergeWorldTransformForComponents(
        SceneEditableKind kind,
        string runtimeId,
        WorldTransform requested,
        SceneEditorTransformComponents components)
    {
        components = NormalizeTransformComponents(components);
        if (components == SceneEditorTransformComponents.All)
            return requested;

        var baseTransform = requested;
        var current = this.FindEditable(kind, runtimeId);
        if (current?.IsNativeGameObject == true)
        {
            var record = this.GetNativeModificationRecord(current);
            if (record != null)
                baseTransform = record.IsHidden ? GetHiddenOrCurrentTransform(record) : GetCurrentTransform(record);
        }
        else if (current != null)
        {
            baseTransform = current.Transform;
        }

        return MergeWorldTransformForComponents(baseTransform, requested, components);
    }

    public void BeginInteractiveTransformEdit()
        => this.activeInteractiveTransformEdits++;

    public void EndInteractiveTransformEdit()
    {
        if (this.activeInteractiveTransformEdits > 0)
            this.activeInteractiveTransformEdits--;

        if (this.activeInteractiveTransformEdits != 0)
            return;

        this.lastPersistDirtyUtc = DateTime.UtcNow;
        this.lastNativeScanUtc = DateTime.MinValue;
        this.nativeCache.Clear();
        this.TransformGeneration++;
    }

    public bool ApplyInteractiveWorldTransform(
        SceneEditableRef selectedAtDragStart,
        WorldTransform transform,
        SceneEditorTransformComponents components,
        out WorldTransform appliedTransform)
    {
        components = NormalizeTransformComponents(components);
        appliedTransform = this.MergeWorldTransformForComponents(selectedAtDragStart, transform, components);
        if (components == SceneEditorTransformComponents.None)
        {
            this.LastStatus = "Transform apply skipped: no component changed.";
            return true;
        }

        if (selectedAtDragStart.Kind is SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight &&
            selectedAtDragStart.IsNativeGameObject)
        {
            if (this.IsBgPartCollisionConfirmationRequired(selectedAtDragStart.Kind))
            {
                var source = selectedAtDragStart.Kind == SceneEditableKind.NativeBgPart ? "Native" : "Plugin";
                return this.BlockUnconfirmedBgPartCollisionTransform(selectedAtDragStart.Kind, source, selectedAtDragStart.RuntimeId);
            }

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

            var target = new FreshNativeLayoutTarget(
                selectedAtDragStart.NativePtr,
                this.getTerritoryType(),
                this.sceneGeneration,
                selectedAtDragStart.Kind,
                this.GetStableKey(selectedAtDragStart),
                IsFresh: true,
                ResolveReason: "interactive drag cached target");
            var result = this.ApplyFreshNativeLayoutTransform(
                selectedAtDragStart.RuntimeId,
                target,
                appliedTransform,
                this.NativeFullLayoutTransformConfirmed,
                components,
                updateGeneration: false,
                invalidateNativeCache: false);
            if (result)
                this.RecordNativeTransformChange(selectedAtDragStart, appliedTransform, "SceneEditorGizmoDrag");
            return result;
        }

        if (selectedAtDragStart.Kind is SceneEditableKind.NativeActor or SceneEditableKind.EventNpc &&
            selectedAtDragStart.IsNativeGameObject)
        {
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

            var result = this.ApplyNativeActorTransformAddress(
                selectedAtDragStart.RuntimeId,
                selectedAtDragStart.NativePtr,
                appliedTransform,
                components,
                updateGeneration: false,
                invalidateNativeCache: false);
            if (result && selectedAtDragStart is { IsPlayer: false })
                this.RecordNativeTransformChange(selectedAtDragStart, appliedTransform, "SceneEditorGizmoDrag");
            return result;
        }

        return this.ApplyWorldTransform(selectedAtDragStart.Kind, selectedAtDragStart.RuntimeId, transform, components);
    }

    public bool ApplyWorldTransform(
        SceneEditableKind kind,
        string runtimeId,
        WorldTransform transform,
        SceneEditorTransformComponents components)
    {
        components = NormalizeTransformComponents(components);
        if (components == SceneEditorTransformComponents.None)
        {
            this.LastStatus = "Transform apply skipped: no component changed.";
            return true;
        }

        if (this.IsBgPartCollisionConfirmationRequired(kind))
        {
            var source = kind == SceneEditableKind.NativeBgPart ? "Native" : "Plugin";
            return this.BlockUnconfirmedBgPartCollisionTransform(kind, source, runtimeId);
        }

        var requestedTransform = this.MergeWorldTransformForComponents(kind, runtimeId, transform, components);
        var scale = kind == SceneEditableKind.LocalActor
            ? ActorTransformUtil.NormalizeScale(requestedTransform.WorldScale)
            : WorldTransformUtil.NormalizeScale(requestedTransform.WorldScale);
        var rotation = kind == SceneEditableKind.LocalActor
            ? ActorTransformUtil.NormalizeRotation(requestedTransform.WorldEulerRadians)
            : requestedTransform.WorldEulerRadians;
        this.log.Debug("[SceneEditor] ApplyWorldTransform kind={Kind} id={Id} components={Components} pos={Position} rot={Rotation} scale={Scale}",
            kind,
            runtimeId,
            components,
            requestedTransform.WorldPosition,
            rotation,
            scale);

        switch (kind)
        {
            case SceneEditableKind.LocalActor:
                var actorResult = this.actors.ApplyActorTransform(runtimeId, requestedTransform.WorldPosition, rotation, scale);
                this.LastStatus = this.actors.LastMessage;
                if (actorResult)
                {
                    this.actors.PersistActorWorldTransformToNpc(runtimeId, requestedTransform.WorldPosition, rotation, scale);
                    this.SyncLocalActorRecord(runtimeId, requestedTransform.WorldPosition, rotation, scale);
                    this.TransformGeneration++;
                    this.MarkPersistDirty("LocalActor transform");
                }
                return actorResult;
            case SceneEditableKind.LocalBgPart:
                if (!this.ApplyLocalBgPartTransform(runtimeId, requestedTransform.WorldPosition, requestedTransform.WorldEulerRadians, scale, "SceneEditor LocalBgPart transform", components))
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

                light.Position = requestedTransform.WorldPosition;
                light.Rotation = requestedTransform.WorldEulerRadians;
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
                var layoutResult = this.ApplyNativeLayoutTransform(runtimeId, requestedTransform, components);
                if (layoutResult && beforeLayout is { IsNativeGameObject: true, IsPlayer: false })
                    this.RecordNativeTransformChange(beforeLayout, WorldTransform.FromEuler(requestedTransform.WorldPosition, requestedTransform.WorldEulerRadians, scale), "SceneEditorTransform");
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
                var nativeActorResult = this.ApplyNativeActorTransform(runtimeId, requestedTransform, components);
                if (nativeActorResult && beforeActor is { IsNativeGameObject: true, IsPlayer: false })
                    this.RecordNativeTransformChange(beforeActor, WorldTransform.FromEuler(requestedTransform.WorldPosition, requestedTransform.WorldEulerRadians, scale), "SceneEditorTransform");
                return nativeActorResult;
            case SceneEditableKind.Player:
                this.LastStatus = "LocalPlayer transform editing is always blocked.";
                return false;
            default:
                this.LastStatus = $"Unsupported editable kind: {kind}";
                return false;
        }
    }

    private static SceneEditorTransformComponents NormalizeTransformComponents(SceneEditorTransformComponents components)
        => components & SceneEditorTransformComponents.All;

    private static bool HasTransformComponent(SceneEditorTransformComponents components, SceneEditorTransformComponents component)
        => (components & component) == component;

    private WorldTransform MergeWorldTransformForComponents(
        SceneEditableRef selected,
        WorldTransform requested,
        SceneEditorTransformComponents components)
    {
        var baseTransform = selected.Transform;
        if (selected.IsNativeGameObject)
        {
            var record = this.GetNativeModificationRecord(selected);
            if (record != null)
                baseTransform = record.IsHidden ? GetHiddenOrCurrentTransform(record) : GetCurrentTransform(record);
        }

        return MergeWorldTransformForComponents(baseTransform, requested, components);
    }

    private static WorldTransform MergeWorldTransformForComponents(
        WorldTransform current,
        WorldTransform requested,
        SceneEditorTransformComponents components)
    {
        components = NormalizeTransformComponents(components);
        if (components == SceneEditorTransformComponents.All)
            return requested;

        return WorldTransform.FromEuler(
            HasTransformComponent(components, SceneEditorTransformComponents.Position) ? requested.WorldPosition : current.WorldPosition,
            HasTransformComponent(components, SceneEditorTransformComponents.Rotation) ? requested.WorldEulerRadians : current.WorldEulerRadians,
            HasTransformComponent(components, SceneEditorTransformComponents.Scale) ? requested.WorldScale : current.WorldScale);
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

    public void ForgetLocalActorRecord(string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return;

        var removed = this.LocalActorRecords.RemoveAll(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            this.MarkPersistDirty("LocalActor record removed");
    }

    public bool RequestDeleteLocalBgPartCopy(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (!this.pendingLocalBgPartDeletes.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            this.pendingLocalBgPartDeletes.Enqueue(instanceId);

        this.LastStatus = $"Queued local BgPart copy delete: {instanceId}";
        return true;
    }

    private void ProcessPendingLocalBgPartDeletes()
    {
        if (this.pendingLocalBgPartDeletes.Count == 0)
            return;

        if (this.restoreRunning)
        {
            this.LastStatus = "Local BgPart delete waiting: restore queue is running.";
            return;
        }

        if (!this.CanRunRestoreNow(out var reason))
        {
            this.LastStatus = $"Local BgPart delete waiting: {reason}.";
            return;
        }

        var instanceId = this.pendingLocalBgPartDeletes.Dequeue();
        this.DeleteLocalBgPartCopyNow(instanceId);
    }

    private bool DeleteLocalBgPartCopyNow(string instanceId)
    {
        var instance = this.localLayoutObjects.GetById(instanceId);
        if (instance == null)
        {
            this.ForgetLocalBgPartRecord(instanceId);
            this.LastStatus = $"Local BgPart copy already gone: {instanceId}";
            return true;
        }

        if (this.TryGetHiddenNativeCarrierRecordForCopy(instance, out var nativeRecord))
        {
            var hiddenTransform = GetHiddenOrCurrentTransform(nativeRecord);
            var released = this.localLayoutObjects.ReleaseHiddenNativeCarrierCopy(
                instance.Id,
                hiddenTransform.WorldPosition,
                hiddenTransform.WorldEulerRadians,
                hiddenTransform.WorldScale,
                out var releaseResult);
            if (!released)
            {
                nativeRecord.Status = $"ReleaseCopyFailed: {releaseResult}";
                nativeRecord.LastModifiedAt = DateTime.UtcNow;
                this.MarkPersistDirty("Hidden native carrier copy release failed");
                this.LastStatus = releaseResult;
                return false;
            }

            nativeRecord.UsedByLocalBgPartInstanceId = string.Empty;
            nativeRecord.UsedByLocalBgPartSlotAddress = string.Empty;
            nativeRecord.UsedByLocalBgPartMdlPath = string.Empty;
            nativeRecord.Status = nativeRecord.IsHidden ? "Hidden" : nativeRecord.IsModified ? "Modified" : "Restored";
            nativeRecord.LastModifiedAt = DateTime.UtcNow;
            this.ForgetLocalBgPartRecord(instance.Id);
            this.MarkPersistDirty("Hidden native carrier copy released");
            this.SaveDirtyConfigurationNow("Hidden native carrier copy released");
            this.TransformGeneration++;
            this.LastStatus = releaseResult;
            return true;
        }

        var deleted = this.localLayoutObjects.Delete(instance.Id);
        this.LastStatus = this.localLayoutObjects.LastStatus;
        if (!deleted)
            return false;

        this.ForgetLocalBgPartRecord(instance.Id);
        this.MarkPersistDirty("LocalBgPart deleted");
        this.SaveDirtyConfigurationNow("LocalBgPart deleted");
        this.TransformGeneration++;
        return true;
    }

    public void ForgetAllLocalBgPartRecords()
    {
        if (this.LocalBgPartRecords.Count == 0)
            return;

        this.LocalBgPartRecords.Clear();
        this.MarkPersistDirty("LocalBgPart records cleared");
    }

    public void ForgetCurrentTerritoryLocalBgPartRecords()
    {
        var territory = this.getTerritoryType();
        var removed = this.LocalBgPartRecords.RemoveAll(item => item.TerritoryId == territory);
        if (removed > 0)
            this.MarkPersistDirty("Current territory LocalBgPart records cleared");
    }

    public void ForgetCurrentTerritoryLocalActorRecords()
    {
        var territory = this.getTerritoryType();
        var removed = this.LocalActorRecords.RemoveAll(item => item.TerritoryId == territory);
        if (removed > 0)
            this.MarkPersistDirty("Current territory LocalActor records cleared");
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

    private bool ApplyLocalBgPartTransform(
        string runtimeId,
        Vector3 position,
        Vector3 rotationEuler,
        Vector3 scale,
        string reason,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All)
    {
        if (this.IsBgPartCollisionConfirmationRequired(SceneEditableKind.LocalBgPart))
            return this.BlockUnconfirmedBgPartCollisionTransform(SceneEditableKind.LocalBgPart, "Plugin", runtimeId);

        var mode = this.CurrentBgPartTransformMode;

        var instance = this.localLayoutObjects.GetById(runtimeId);
        if (instance == null)
        {
            this.LastStatus = $"Local BgPart not found: {runtimeId}";
            this.LastBgPartCollisionOperation = "source=Plugin; collisionMode=Unknown; collision operation=Failed; handle=unavailable";
            return false;
        }

        if (instance.TransformMode != mode)
        {
            if (mode == LocalLayoutTransformMode.VisualOnly)
            {
                instance.TransformMode = LocalLayoutTransformMode.VisualOnly;
                instance.CollisionApplied = false;
                instance.CollisionSourceResolveResult = "VisualOnly：全局 Collision 开关关闭，本次 transform 只写 Graphics.Scene.Object；不会写 LayoutInstance，也不会移动/重建 collision。";
                instance.CollisionError = string.Empty;
            }
            else
            {
                var changed = this.localLayoutObjects.ChangeCollisionMode(
                    runtimeId,
                    mode,
                    this.GetCurrentBgPartCandidates(),
                    this.AllowNativeTransformWrites,
                    this.NativeFullLayoutTransformConfirmed);
                if (!changed)
                {
                    this.LastStatus = this.localLayoutObjects.LastStatus;
                    this.LastBgPartCollisionOperation = $"source=Plugin; collisionMode=On; collision operation=Failed; handle={instance.OccupiedSlotAddress}; {this.LastStatus}";
                    return false;
                }
            }
        }

        var applied = this.localLayoutObjects.ApplyVisualTransform(runtimeId, position, rotationEuler, scale, components);
        this.LastBgPartCollisionOperation = $"source=Plugin; collisionMode={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "On" : "Off")}; collision operation={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "Moved" : "Skipped")}; handle={instance.OccupiedSlotAddress}; reason={reason}";
        return applied;
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
            WorldTransformUtil.QuaternionToWorldEulerRadians(source.RotationQuaternion),
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
        this.MarkHiddenNativeCarrierUsedByCopy(created);
        this.LastBgPartCollisionOperation = $"source={(template.Source.Contains("Native", StringComparison.OrdinalIgnoreCase) ? "Native" : "Plugin/Probe")}; collisionMode={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "On" : "Off")}; collision operation={(mode == LocalLayoutTransformMode.FullLayoutWithCollision ? "Cloned/Moved" : "Skipped")}; handle={created.OccupiedSlotAddress}";
        this.LastQuickActionStatus = $"Copied one BgPart: {created.Id}; mode={mode}; {this.LastBgPartCollisionOperation}";
        this.TransformGeneration++;
        this.SyncLocalBgPartSnapshots();
        this.MarkPersistDirty("LocalBgPart created");
        return true;
    }

    private void MarkHiddenNativeCarrierUsedByCopy(LocalLayoutObjectInstance created)
    {
        if (!this.TryGetHiddenNativeCarrierRecordForCopy(created, out var record))
            return;

        record.UsedByLocalBgPartInstanceId = created.Id;
        record.UsedByLocalBgPartSlotAddress = created.OccupiedSlotAddress;
        record.UsedByLocalBgPartMdlPath = FirstNonEmpty(created.CurrentResourcePath, created.CustomModelPath, created.TemplateResourcePath);
        record.Status = $"Hidden; used by copy {created.Id}";
        record.LastModifiedAt = DateTime.UtcNow;
        this.MarkPersistDirty("Hidden native carrier used by copy");
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

        var stableRecord = this.GetNativeModificationRecordByStableKey(this.GetStableKey(selected));
        if (stableRecord != null)
            return stableRecord;

        var sameKindRecords = this.NativeRecords
            .Where(record => record.Kind == selected.Kind)
            .Where(record => this.IsCurrentTerritoryRecord(record))
            .ToList();

        var nativeStableRecord = sameKindRecords.FirstOrDefault(record => this.NativeStableKeyMatches(record, selected));
        if (nativeStableRecord != null)
            return nativeStableRecord;

        var locatorRecords = sameKindRecords
            .Where(record => this.LayoutLocatorMatches(record, selected))
            .ToList();
        if (locatorRecords.Count == 1)
            return locatorRecords[0];

        var nameMdlRecords = sameKindRecords
            .Where(record => this.NativeNameMdlMatches(record, selected))
            .OrderBy(record => this.DistanceFromRecordAnchors(record, selected.Transform.WorldPosition))
            .ToList();
        if (nameMdlRecords.Count == 1)
            return nameMdlRecords[0];
        if (nameMdlRecords.Count > 1)
        {
            var best = nameMdlRecords[0];
            var bestDistance = this.DistanceFromRecordAnchors(best, selected.Transform.WorldPosition);
            var secondDistance = this.DistanceFromRecordAnchors(nameMdlRecords[1], selected.Transform.WorldPosition);
            if (secondDistance - bestDistance >= 0.10f)
                return best;
        }

        return null;
    }

    private SceneEditorNativeModificationRecord? GetNativeModificationRecordByStableKey(string stableKey)
        => string.IsNullOrWhiteSpace(stableKey)
            ? null
            : this.NativeRecords.FirstOrDefault(item =>
                string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));

    private bool TryGetHiddenNativeCarrierRecordForCopy(LocalLayoutObjectInstance instance, out SceneEditorNativeModificationRecord record)
    {
        record = this.NativeRecords.FirstOrDefault(item =>
            item.Kind == SceneEditableKind.NativeBgPart &&
            item.IsHidden &&
            string.Equals(item.UsedByLocalBgPartInstanceId, instance.Id, StringComparison.OrdinalIgnoreCase))!;
        if (record != null)
            return true;

        record = this.NativeRecords.FirstOrDefault(item =>
            item.Kind == SceneEditableKind.NativeBgPart &&
            item.IsHidden &&
            this.NativeRecordMatchesLocalCarrier(item, instance))!;
        return record != null;
    }

    private bool NativeRecordMatchesLocalCarrier(SceneEditorNativeModificationRecord record, LocalLayoutObjectInstance instance)
    {
        if (record.TerritoryId != 0 && record.TerritoryId != this.getTerritoryType())
            return false;

        var snapshot = instance.OriginalSlotSnapshot;
        var instanceStableKey = FirstNonEmpty(instance.SourceStableKey, snapshot?.SourceStableKey ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(instanceStableKey) &&
            string.Equals(instanceStableKey, record.StableKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(record.RuntimeIdAtRecordTime) &&
            !string.IsNullOrWhiteSpace(instance.OccupiedSlotAddress) &&
            record.RuntimeIdAtRecordTime.Contains(instance.OccupiedSlotAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var recordPath = record.MdlPath;
        var instanceOriginalPath = FirstNonEmpty(snapshot?.OriginalResourcePath ?? string.Empty, instance.OriginalResourcePath, instance.SourceResourcePath);
        if (!string.IsNullOrWhiteSpace(recordPath) &&
            !string.Equals(recordPath, instanceOriginalPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.SourceKind) &&
            !string.IsNullOrWhiteSpace(instance.SourceKind) &&
            !string.Equals(record.SourceKind, instance.SourceKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.SharedGroupPath) &&
            !string.Equals(record.SharedGroupPath, instance.SourceSharedGroupPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (record.ChildIndex >= 0 && record.ChildIndex != instance.SourceChildIndex)
            return false;

        var originalPosition = snapshot?.OriginalGraphicsPosition ?? instance.OccupiedSlotOriginalPosition;
        var hiddenPosition = ToVector3(record.HiddenPosition);
        return Vector3.Distance(ToVector3(record.OriginalPosition), originalPosition) <= 2f ||
               (hiddenPosition != Vector3.Zero && Vector3.Distance(hiddenPosition, instance.CurrentPosition) <= 3f);
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
        var hiddenPosition = originalTransform.WorldPosition + new Vector3(0f, -NativeHideDepthMeters, 0f);
        var hiddenTransform = WorldTransform.FromEuler(hiddenPosition, originalTransform.WorldEulerRadians, originalTransform.WorldScale);

        if (!this.ApplyWorldTransform(selected.Kind, selected.RuntimeId, hiddenTransform))
        {
            record.Status = "HideFailed";
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native hide failed");
            return false;
        }

        SetRecordCurrentTransform(record, hiddenTransform);
        if (selected.Kind == SceneEditableKind.NativeLight)
            SetRecordCurrentLightState(record, CaptureNativeLightState(selected, hiddenTransform, visible: false));
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
        this.LastStatus = $"Native object hidden {NativeHideDepthMeters:F0}m underground: {selected.DisplayName}";
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

        if (this.IsNativeRestoreBlockedByCopy(record, out var copyBlockReason))
        {
            record.Status = NativeResolveStatus.BlockedByCopy.ToString();
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native restore blocked by copy");
            this.SaveDirtyConfigurationNow("Native restore blocked by copy");
            this.LastStatus = $"Native restore blocked: {copyBlockReason}";
            this.log.Warning("[SceneEditor] Native restore blocked by copy kind={Kind} key={Key} name={Name} reason={Reason}", record.Kind, record.StableKey, record.DisplayName, copyBlockReason);
            return false;
        }

        if (!this.CanRunRestoreNow(out var pausedReason))
        {
            record.Status = $"RestorePaused: {pausedReason}";
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native restore paused");
            this.SaveDirtyConfigurationNow("Native restore paused");
            this.LastStatus = $"Native restore paused: {pausedReason}.";
            return false;
        }

        var original = GetOriginalTransform(record);
        if (!this.RestoreNativeRecordToOriginalTransform(record, original, out var restoreReason))
        {
            record.Status = ToNativeRestoreFailureStatus(restoreReason);
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native restore failed");
            this.SaveDirtyConfigurationNow("Native restore failed");
            this.LastStatus = $"Native restore failed; record kept: {record.DisplayName}; {restoreReason}";
            this.log.Warning("[SceneEditor] Native restore failed; record kept kind={Kind} key={Key} name={Name} reason={Reason}", record.Kind, record.StableKey, record.DisplayName, restoreReason);
            return false;
        }

        SetRecordCurrentTransform(record, original);
        record.IsHidden = false;
        record.IsModified = false;
        record.UsedByLocalBgPartInstanceId = string.Empty;
        record.UsedByLocalBgPartSlotAddress = string.Empty;
        record.UsedByLocalBgPartMdlPath = string.Empty;
        this.RemoveAutoPreferredModifyForRestoredNativeBgPart(record);
        record.Status = "Restored";
        record.LastModifiedAt = DateTime.UtcNow;
        this.NativeRecords.Remove(record);
        this.MarkPersistDirty("Native restore");
        this.SaveDirtyConfigurationNow("Native restore");
        this.LastStatus = $"Native object restored: {record.DisplayName}";
        this.log.Information("[SceneEditor] Native restore kind={Kind} key={Key} name={Name}", record.Kind, record.StableKey, record.DisplayName);
        return true;
    }

    private bool IsNativeRestoreBlockedByCopy(SceneEditorNativeModificationRecord record, out string reason)
    {
        reason = string.Empty;
        if (record.Kind != SceneEditableKind.NativeBgPart)
            return false;

        if (!string.IsNullOrWhiteSpace(record.UsedByLocalBgPartInstanceId))
        {
            var owner = this.localLayoutObjects.GetById(record.UsedByLocalBgPartInstanceId);
            if (IsActiveLocalBgPartCopy(owner))
            {
                reason = $"copy {record.UsedByLocalBgPartInstanceId} still occupies this hidden carrier";
                return true;
            }

            record.UsedByLocalBgPartInstanceId = string.Empty;
            record.UsedByLocalBgPartSlotAddress = string.Empty;
            record.UsedByLocalBgPartMdlPath = string.Empty;
            record.Status = record.IsHidden ? "Hidden" : record.IsModified ? "Modified" : "Restored";
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Cleared stale native copy occupancy");
        }

        var activeOwner = this.localLayoutObjects.Instances.FirstOrDefault(instance =>
            IsActiveLocalBgPartCopy(instance) &&
            this.NativeRecordMatchesLocalCarrier(record, instance));
        if (activeOwner == null)
            return false;

        record.UsedByLocalBgPartInstanceId = activeOwner.Id;
        record.UsedByLocalBgPartSlotAddress = activeOwner.OccupiedSlotAddress;
        record.UsedByLocalBgPartMdlPath = FirstNonEmpty(activeOwner.CurrentResourcePath, activeOwner.CustomModelPath, activeOwner.TemplateResourcePath);
        record.LastModifiedAt = DateTime.UtcNow;
        this.MarkPersistDirty("Detected native copy occupancy");
        reason = $"copy {activeOwner.Id} occupies this carrier";
        return true;
    }

    private static bool IsActiveLocalBgPartCopy(LocalLayoutObjectInstance? instance)
        => instance != null &&
           !instance.IsInvalid &&
           !instance.IsRestored &&
           !instance.IsDuplicate &&
           instance.IsOccupied;

    private void RemoveAutoPreferredModifyForRestoredNativeBgPart(SceneEditorNativeModificationRecord record)
    {
        if (record.Kind != SceneEditableKind.NativeBgPart || !record.PreferredModifyAdded)
            return;

        var registry = this.localLayoutObjects.PreferredModifyBgParts;
        if (registry == null)
            return;

        var slot = new LayoutProbeInstance
        {
            Type = "BgPart",
            Key = record.StableKey,
            StableKey = record.StableKey,
            Address = FirstNonEmpty(record.StableKey, record.RuntimeIdAtRecordTime),
            SourceKind = FirstNonEmpty(record.SourceKind, record.ObjectKind, "LoadedLayout"),
            ResourcePath = record.MdlPath,
            Position = ToVector3(record.OriginalPosition),
            Rotation = ToVector3(record.OriginalRotationEuler).ToString(),
            RotationQuaternion = WorldTransformUtil.WorldEulerRadiansToQuaternion(ToVector3(record.OriginalRotationEuler)),
            Scale = WorldTransformUtil.NormalizeScale(ToVector3(record.OriginalScale)),
            SharedGroupPath = record.SharedGroupPath,
            ParentKey = record.ParentStableKey,
            ChildIndex = record.ChildIndex,
        };

        registry.UnprotectSlot(slot);
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
            string.Equals(item.Status, "Restored", StringComparison.OrdinalIgnoreCase));
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
            .Where(item => this.IsRecordInCurrentOrLegacyTerritory(item.TerritoryId, territory))
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
            .Where(item => this.IsRecordInCurrentOrLegacyTerritory(item.TerritoryId, territory))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.LastSavedAt)
            .ToList();

        var lightTotal = this.localLights.Instances.Count;
        var lightLegacy = this.localLights.Instances.Count(item => item.TerritoryId == 0);
        var lightWrongTerritory = this.localLights.Instances.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreLightRecords = this.localLights.Instances
            .Where(item => this.IsRecordInCurrentOrLegacyTerritory(item.TerritoryId, territory))
            .Select((item, index) => new { item, index })
            .OrderBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToList();

        var actorTotal = this.LocalActorRecords.Count;
        var actorLegacy = this.LocalActorRecords.Count(item => item.TerritoryId == 0);
        var actorWrongTerritory = this.LocalActorRecords.Count(item => item.TerritoryId != 0 && item.TerritoryId != territory);
        this.restoreActorRecords = this.LocalActorRecords
            .Where(item => item.Enabled)
            .Where(item => this.IsRecordInCurrentOrLegacyTerritory(item.TerritoryId, territory))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.LastSavedAt)
            .ToList();

        var actorRestoreCount = this.restoreActorRecords.Count;
        this.RestoreStatus = $"[Restore] Start generation reason={this.restoreReason}; native={this.restoreNativeRecords.Count}; bgParts={this.restoreBgPartRecords.Count}; lights={this.restoreLightRecords.Count}; actors={actorRestoreCount}";
        this.log.Information(
            "[RestorePlan] reason={Reason}; currentTerritory={Territory}; native total={NativeTotal}, matched={NativeCount}, skippedWrongTerritory={NativeWrongTerritory}, includedLegacyNoTerritory={NativeLegacy}; localBgParts total={BgTotal}, matched={BgPartCount}, skippedWrongTerritory={BgWrongTerritory}, includedLegacyNoTerritory={BgLegacy}; localLights total={LightTotal}, matched={LightCount}, skippedWrongTerritory={LightWrongTerritory}, includedLegacyNoTerritory={LightLegacy}; localActors total={ActorTotal}, matched={ActorCount}, skippedWrongTerritory={ActorWrongTerritory}, includedLegacyNoTerritory={ActorLegacy}",
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
        foreach (var record in this.LocalActorRecords.Where(item => item.TerritoryId != 0 && item.TerritoryId != territory))
            this.log.Information("[RestorePlan] Skipped actor wrong territory id={Id} actorTerritory={ActorTerritory} currentTerritory={CurrentTerritory}", record.RecordId, record.TerritoryId, territory);
    }

    private bool IsRecordInCurrentOrLegacyTerritory(uint recordTerritory, uint currentTerritory)
        => currentTerritory != 0 && (recordTerritory == currentTerritory || recordTerritory == 0);

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
                    if (light.TerritoryId == 0)
                    {
                        light.TerritoryId = this.getTerritoryType();
                        light.LastOperation = "LegacyNoTerritory: restoring on current map.";
                        this.MarkPersistDirty("LocalLight legacy territory backfilled");
                    }

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
            if (record.TerritoryId == 0)
            {
                record.TerritoryId = this.getTerritoryType();
                record.RestoreStatus = "LegacyNoTerritory: restoring on current map.";
                this.MarkPersistDirty("LocalActor legacy territory backfilled");
            }

            var position = record.WorldPosition;
            var rotation = ActorTransformUtil.NormalizeRotation(record.WorldRotationEuler);
            var scale = ActorTransformUtil.NormalizeScale(record.WorldScale);
            var key = $"{FirstNonEmpty(record.RuntimeId, record.NpcId, record.RecordId)}|{record.SortOrder}|{MathF.Round(position.X, 2)}|{MathF.Round(position.Y, 2)}|{MathF.Round(position.Z, 2)}";
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

            if (this.actors.QueueRestoreExistingActorConfig(record.RuntimeId, position, rotation, scale, record.SortOrder, out var configRestoreReason))
            {
                record.RestoreStatus = configRestoreReason;
                queued++;
                continue;
            }

            var npc = this.actors.GetNpcById(record.NpcId);
            if (npc == null)
            {
                record.RestoreStatus = $"Failed: missing NPC config and ActorConfig. {configRestoreReason}";
                this.log.Warning("[ActorRestore] skip local actor record missing npc/config record={Record} runtime={RuntimeId} npc={NpcId} reason={Reason}", record.RecordId, record.RuntimeId, record.NpcId, configRestoreReason);
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

        var transform = record.IsHidden
            ? GetHiddenOrCurrentTransform(record)
            : GetCurrentTransform(record);

        if (!this.ApplyNativeRecordTransformViaPersistedPath(record, transform, "persisted native modification replay"))
        {
            if (!IsNativeResolveStatus(record.Status))
                record.Status = ToNativeRestoreFailureStatus(this.LastStatus);
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Persisted native restore missing");
            this.log.Warning("[Restore] Native restore unresolved status={Status} stableKey={StableKey} kind={Kind} name={Name} reason={Reason}", record.Status, record.StableKey, record.Kind, record.DisplayName, this.LastStatus);
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
        if (record.TerritoryId == 0)
        {
            record.TerritoryId = this.getTerritoryType();
            record.RestoreStatus = "LegacyNoTerritory: restoring on current map.";
            this.MarkPersistDirty("LocalBgPart legacy territory backfilled");
        }

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
            StableKey = FirstNonEmpty(record.SourceBgPartStableKey, $"persisted:{record.InstanceId}"),
            ResourcePath = targetPath,
            SourceKind = FirstNonEmpty(record.SourceKind, "PersistedSceneEditor"),
            Position = ToVector3(record.WorldPosition),
            RotationQuaternion = WorldTransformUtil.WorldEulerRadiansToQuaternion(ToVector3(record.WorldRotationEuler)),
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
        if (this.activeInteractiveTransformEdits > 0)
            return;

        this.log.Debug("[Persist] MarkDirty reason={Reason}", reason);
    }

    private void SaveDirtyConfigurationIfDue()
    {
        if (!this.persistDirty)
            return;

        if (this.activeInteractiveTransformEdits > 0)
            return;

        if ((DateTime.UtcNow - this.lastPersistDirtyUtc).TotalMilliseconds < 800)
            return;

        this.log.Debug("[Persist] Save begin.");
        this.saveConfiguration();
        this.persistDirty = false;
        this.log.Debug("[Persist] Save end.");
    }

    private void SaveDirtyConfigurationNow(string reason)
    {
        if (!this.persistDirty)
            return;

        this.log.Debug("[Persist] Save now reason={Reason}", reason);
        this.saveConfiguration();
        this.persistDirty = false;
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
        changed |= SetPropertyIfChanged(record.SourceBgPartStableKey, FirstNonEmpty(instance.SourceStableKey, instance.SourceParentKey, instance.OccupiedSlotAddress, instance.TemplateSourceSlotAddress), value => record.SourceBgPartStableKey = value);
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
        => record.TerritoryId == 0 || record.TerritoryId == this.getTerritoryType();

    private WorldTransform ReadNativeTransformOrFallback(SceneEditableRef selected)
    {
        return selected.Transform;
    }

    private void RecordNativeTransformChange(SceneEditableRef before, WorldTransform after, string reason)
    {
        if (!CanTrackNativeModification(before))
            return;

        var beforeTransform = before.Transform;
        var record = this.EnsureNativeRecord(before, beforeTransform, reason);
        if (before.Kind == SceneEditableKind.NativeBgPart)
            record.UseFullLayoutTransform = this.NativeFullLayoutTransformConfirmed;
        SetRecordCurrentTransform(record, after);
        if (before.Kind == SceneEditableKind.NativeLight)
            SetRecordCurrentLightState(record, CaptureNativeLightState(before, after));
        record.IsModified = !TransformsApproximatelyEqual(GetOriginalTransform(record), after) || record.IsHidden;
        if (!record.IsHidden)
            record.Status = record.IsModified ? "Modified" : "Restored";
        record.Reason = reason;
        record.LastModifiedAt = DateTime.UtcNow;
        if (!record.IsHidden &&
            !record.IsModified &&
            string.IsNullOrWhiteSpace(record.UsedByLocalBgPartInstanceId))
        {
            this.NativeRecords.Remove(record);
            this.MarkPersistDirty($"Native transform {reason} restored");
            return;
        }

        this.MarkPersistDirty($"Native transform {reason}");
    }

    private SceneEditorNativeModificationRecord EnsureNativeRecord(SceneEditableRef selected, WorldTransform original, string reason)
    {
        var stableKey = this.GetStableKey(selected);
        var record = this.GetNativeModificationRecord(selected) ??
                     this.NativeRecords.FirstOrDefault(item =>
                         string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (record != null)
        {
            this.UpdateNativeRecordLocator(record, selected);
            return record;
        }

        record = new SceneEditorNativeModificationRecord
        {
            RecordId = Guid.NewGuid().ToString("N"),
            StableKey = stableKey,
            RuntimeIdAtRecordTime = selected.RuntimeId,
            NativeAddress = selected.NativePtr == 0 ? string.Empty : $"0x{selected.NativePtr:X}",
            Kind = selected.Kind,
            DisplayName = selected.DisplayName,
            MdlPath = selected.MdlPath,
            TerritoryKey = FormatTerritoryKey(this.getTerritoryType()),
            NativeBgPartStableKey = selected.Kind == SceneEditableKind.NativeBgPart ? selected.StableKey : string.Empty,
            NativeBgPartSgbPath = selected.Kind == SceneEditableKind.NativeBgPart ? selected.LayoutProbe?.ResourcePath ?? string.Empty : string.Empty,
            NativeBgPartAssetPath = selected.Kind == SceneEditableKind.NativeBgPart ? FirstNonEmpty(selected.LayoutProbe?.ResourcePath, selected.DisplayName) : string.Empty,
            NativeBgPartModelPath = selected.Kind == SceneEditableKind.NativeBgPart ? FirstNonEmpty(selected.LayoutProbe?.ModelResourcePath, selected.MdlPath) : string.Empty,
            NativeBgPartInitialIndex = selected.Kind == SceneEditableKind.NativeBgPart ? selected.LayoutProbe?.Index ?? -1 : -1,
            NativeBgPartInitialAddress = selected.Kind == SceneEditableKind.NativeBgPart ? selected.LayoutProbe?.Address ?? string.Empty : string.Empty,
            NamePath = FirstNonEmpty(selected.LayoutProbe?.ResourcePath, selected.DisplayName),
            ObjectKind = selected.ObjectKind,
            TerritoryId = this.getTerritoryType(),
            ObjectIndexAtRecordTime = selected.ObjectIndex,
            LayoutInstanceKey = selected.LayoutProbe?.Key ?? string.Empty,
            LayoutInstanceAddress = selected.LayoutProbe?.Address ?? string.Empty,
            LayoutInstanceIndexAtRecordTime = selected.LayoutProbe?.Index ?? -1,
            DataId = selected.DataId,
            IsInteractableNpc = selected.IsInteractableNpc || selected.Kind == SceneEditableKind.EventNpc,
            RuntimeOnly = string.IsNullOrWhiteSpace(selected.StableKey),
            UseFullLayoutTransform = selected.Kind is SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight && this.NativeFullLayoutTransformConfirmed,
            Reason = reason,
            Status = "Modified",
        };
        SetRecordOriginalTransform(record, original);
        SetRecordCurrentTransform(record, original);
        if (selected.Kind == SceneEditableKind.NativeLight)
        {
            var originalLightState = CaptureNativeLightState(selected, original);
            SetRecordOriginalLightState(record, originalLightState);
            SetRecordCurrentLightState(record, originalLightState);
        }
        this.UpdateNativeRecordLocator(record, selected);
        this.NativeRecords.Add(record);
        return record;
    }

    private void UpdateNativeRecordLocator(SceneEditorNativeModificationRecord record, SceneEditableRef selected)
    {
        record.RuntimeIdAtRecordTime = selected.RuntimeId;
        record.TerritoryKey = FormatTerritoryKey(record.TerritoryId == 0 ? this.getTerritoryType() : record.TerritoryId);
        if (!string.IsNullOrWhiteSpace(selected.StableKey))
            record.StableKey = selected.StableKey;
        if (selected.NativePtr != 0)
            record.NativeAddress = $"0x{selected.NativePtr:X}";
        if (selected.Kind == SceneEditableKind.NativeBgPart && !string.IsNullOrWhiteSpace(selected.StableKey))
            record.NativeBgPartStableKey = selected.StableKey;
        if (!string.IsNullOrWhiteSpace(selected.LayoutProbe?.StableKey) && selected.Kind == SceneEditableKind.NativeBgPart)
            record.NativeBgPartStableKey = selected.LayoutProbe.StableKey;
        if (!string.IsNullOrWhiteSpace(selected.LayoutProbe?.Key))
            record.LayoutInstanceKey = selected.LayoutProbe.Key;
        if (!string.IsNullOrWhiteSpace(selected.LayoutProbe?.Address))
            record.LayoutInstanceAddress = selected.LayoutProbe.Address;
        if (selected.LayoutProbe != null && (record.Kind != SceneEditableKind.NativeBgPart || record.LayoutInstanceIndexAtRecordTime < 0))
            record.LayoutInstanceIndexAtRecordTime = selected.LayoutProbe.Index;
        if (!string.IsNullOrWhiteSpace(selected.DisplayName))
            record.DisplayName = selected.DisplayName;
        if (!string.IsNullOrWhiteSpace(selected.MdlPath))
            record.MdlPath = selected.MdlPath;
        if (!string.IsNullOrWhiteSpace(selected.LayoutProbe?.ResourcePath) || !string.IsNullOrWhiteSpace(selected.DisplayName))
            record.NamePath = FirstNonEmpty(selected.LayoutProbe?.ResourcePath, selected.DisplayName, record.NamePath);
        if (!string.IsNullOrWhiteSpace(selected.ObjectKind))
            record.ObjectKind = selected.ObjectKind;
        if (!string.IsNullOrWhiteSpace(selected.DataId))
            record.DataId = selected.DataId;
        record.IsInteractableNpc = selected.IsInteractableNpc || record.IsInteractableNpc;
        record.LayoutSource = FirstNonEmpty(selected.LayoutProbe?.Source, record.LayoutSource, selected.NativeInfo);
        record.SourceKind = FirstNonEmpty(selected.LayoutProbe?.SourceKind, record.SourceKind, selected.ObjectKind);
        record.SharedGroupPath = FirstNonEmpty(selected.LayoutProbe?.SharedGroupPath, record.SharedGroupPath);
        record.ParentStableKey = FirstNonEmpty(selected.LayoutProbe?.ParentKey, record.ParentStableKey);
        record.ChildIndex = selected.LayoutProbe?.ChildIndex ?? record.ChildIndex;
        if (record.Kind == SceneEditableKind.NativeBgPart)
        {
            record.NativeBgPartSgbPath = FirstNonEmpty(record.NativeBgPartSgbPath, selected.LayoutProbe?.ResourcePath);
            record.NativeBgPartAssetPath = FirstNonEmpty(record.NativeBgPartAssetPath, selected.LayoutProbe?.ResourcePath, selected.DisplayName);
            record.NativeBgPartModelPath = FirstNonEmpty(record.NativeBgPartModelPath, selected.LayoutProbe?.ModelResourcePath, selected.MdlPath);
            if (record.NativeBgPartInitialIndex < 0 && selected.LayoutProbe != null)
                record.NativeBgPartInitialIndex = selected.LayoutProbe.Index;
            record.NativeBgPartInitialAddress = FirstNonEmpty(record.NativeBgPartInitialAddress, selected.LayoutProbe?.Address, record.NativeAddress);
        }
    }

    private bool ApplyNativeRecordTransform(SceneEditorNativeModificationRecord record, WorldTransform transform)
    {
        this.EnsureNativeRecordLocatorFields(record);

        if (record.TerritoryId != 0 && record.TerritoryId != this.getTerritoryType())
        {
            record.Status = NativeResolveStatus.NotLoadedYet.ToString();
            this.LastStatus = "Native restore skipped: record belongs to another territory.";
            return false;
        }

        if (record.TerritoryId == 0)
        {
            record.TerritoryId = this.getTerritoryType();
            record.TerritoryKey = FormatTerritoryKey(record.TerritoryId);
            this.MarkPersistDirty("Native record territory backfilled");
        }

        if (!this.CanRunRestoreNow(out var reason))
        {
            this.LastStatus = $"Native restore skipped: {reason}.";
            return false;
        }

        this.lastNativeScanUtc = DateTime.MinValue;
        this.nativeCache.Clear();
        var current = this.ResolveNativeObjectFresh(record, out var resolveResult);

        if (current == null || current.NativePtr == 0 || current.IsPlayer)
        {
            record.Status = resolveResult.Status.ToString();
            this.LastStatus = $"Native restore resolve failed: {resolveResult.Status}; {resolveResult.Reason}";
            this.log.Warning("[Restore] Resolve native failed status={Status} stableKey={StableKey} kind={Kind} name={Name} reason={Reason}", resolveResult.Status, record.StableKey, record.Kind, record.DisplayName, resolveResult.Reason);
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

    private bool RestoreNativeRecordToOriginalTransform(SceneEditorNativeModificationRecord record, WorldTransform original, out string reason)
    {
        reason = string.Empty;
        if (record.Kind == SceneEditableKind.NativeBgPart)
            return this.RestoreNativeBgPartViaPersistedPath(record, original, out reason);

        if (record.Kind == SceneEditableKind.NativeLight)
            return this.RestoreNativeLightViaOriginalState(record, original, out reason);

        if (!this.ApplyNativeRecordTransform(record, original))
        {
            reason = this.LastStatus;
            return false;
        }

        if (!this.VerifyNativeRestoreApplied(record, original, out reason))
            return false;

        return true;
    }

    private bool RestoreNativeLightViaOriginalState(SceneEditorNativeModificationRecord record, WorldTransform fallbackOriginal, out string reason)
    {
        var originalState = record.OriginalLightState ?? new SceneEditorNativeLightState();
        var originalTransform = originalState.HasState
            ? NativeLightStateToWorldTransform(originalState)
            : fallbackOriginal;

        if (!this.ApplyNativeRecordTransformViaPersistedPath(record, originalTransform, "manual native Light restore to original"))
        {
            reason = this.LastStatus;
            return false;
        }

        SetRecordCurrentTransform(record, originalTransform);
        if (originalState.HasState)
            SetRecordCurrentLightState(record, originalState);
        reason = string.Empty;
        return true;
    }

    private bool RestoreNativeBgPartViaPersistedPath(SceneEditorNativeModificationRecord record, WorldTransform original, out string reason)
    {
        this.log.Debug(
            "[RestoreNativeBgPart] restore via persisted apply path stableKey={StableKey} sgb={Sgb} model={Model} original={Original}",
            record.StableKey,
            GetNativeBgPartRecordSgbPath(record),
            GetNativeBgPartRecordAssetPath(record),
            original.WorldPosition);

        if (!this.ApplyNativeRecordTransformViaPersistedPath(record, original, "manual native BgPart restore to original"))
        {
            reason = this.LastStatus;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool ApplyNativeRecordTransformViaPersistedPath(SceneEditorNativeModificationRecord record, WorldTransform transform, string operation)
    {
        var previousNativeAddress = record.NativeAddress;
        record.NativeAddress = string.Empty;
        this.log.Debug(
            "[RestoreNative] persisted-path apply operation={Operation} kind={Kind} stableKey={StableKey} previousNativeAddress={PreviousNativeAddress} target={Target}",
            operation,
            record.Kind,
            record.StableKey,
            previousNativeAddress,
            transform.WorldPosition);
        return this.ApplyNativeRecordTransform(record, transform);
    }

    private bool VerifyNativeRestoreApplied(SceneEditorNativeModificationRecord record, WorldTransform expected, out string reason)
    {
        this.lastNativeScanUtc = DateTime.MinValue;
        this.nativeCache.Clear();
        var restored = this.ResolveNativeObjectFresh(record, out var resolveResult);
        if (restored == null)
        {
            reason = $"verify resolve failed after write: {resolveResult.Status}; {resolveResult.Reason}";
            return false;
        }

        if (record.Kind == SceneEditableKind.NativeBgPart && restored.IsHidden)
        {
            reason = "verify failed: BgPart is still hidden or not visible";
            return false;
        }

        if (!TransformsRestoredEnough(restored.Transform, expected))
        {
            reason = $"verify failed: transform still differs; actual pos={restored.Transform.WorldPosition}, expected pos={expected.WorldPosition}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private SceneEditableRef? ResolveNativeObjectFresh(SceneEditorNativeModificationRecord record)
        => this.ResolveNativeObjectFresh(record, out _);

    private SceneEditableRef? ResolveNativeObjectFresh(SceneEditorNativeModificationRecord record, out NativeResolveResult resolveResult)
    {
        resolveResult = this.ResolveNativeObjectFreshDetailed(record);
        return resolveResult.Item;
    }

    private NativeResolveResult ResolveNativeObjectFreshDetailed(SceneEditorNativeModificationRecord record)
    {
        this.EnsureNativeRecordLocatorFields(record);
        var currentTerritory = this.getTerritoryType();
        if (record.TerritoryId != 0 && record.TerritoryId != currentTerritory)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.NotLoadedYet,
                null,
                [],
                [],
                [],
                "territory mismatch",
                "territory");
        }

        if (record.Kind == SceneEditableKind.NativeBgPart)
            return this.ResolveNativeBgPartFreshDetailed(record, currentTerritory);

        this.log.Information(
            "[RestoreNative] resolve begin map={MapKey} currentMap={CurrentMap} stableKey={StableKey} nativeBgPartStableKey={NativeBgPartStableKey} kind={Kind} name={Name} mdl={Mdl}",
            FirstNonEmpty(record.TerritoryKey, FormatTerritoryKey(record.TerritoryId)),
            FormatTerritoryKey(currentTerritory),
            record.StableKey,
            record.NativeBgPartStableKey,
            record.Kind,
            FirstNonEmpty(record.NamePath, record.DisplayName),
            record.MdlPath);

        var scanned = this.GetNativeEditablesForRestore()
            .Where(item => item.IsNativeGameObject)
            .Where(item => !item.IsPlayer)
            .ToList();
        var sameKind = scanned
            .Where(item => item.Kind == record.Kind)
            .ToList();

        if (sameKind.Count == 0)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.NotLoadedYet,
                null,
                scanned,
                sameKind,
                [],
                "current map scan returned no runtime objects of this kind",
                "scan");
        }

        var stableMatches = sameKind
            .Where(item => this.NativeStableKeyMatches(record, item))
            .ToList();
        if (this.TryChooseNativeCandidate(record, stableMatches, out var stableChoice, out var stableReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByStableKey,
                stableChoice,
                scanned,
                sameKind,
                stableMatches,
                stableReason,
                "stable-key");
        }

        if (stableMatches.Count > 1)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.MultipleCandidates,
                null,
                scanned,
                sameKind,
                stableMatches,
                stableReason,
                "stable-key");
        }

        var locatorMatches = sameKind
            .Where(item => this.LayoutLocatorMatches(record, item))
            .ToList();
        if (this.TryChooseNativeCandidate(record, locatorMatches, out var locatorChoice, out var locatorReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByStableKey,
                locatorChoice,
                scanned,
                sameKind,
                locatorMatches,
                locatorReason,
                "layout-locator");
        }

        if (locatorMatches.Count > 1)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.MultipleCandidates,
                null,
                scanned,
                sameKind,
                locatorMatches,
                locatorReason,
                "layout-locator");
        }

        var nameMdlMatches = sameKind
            .Where(item => this.NativeNameMdlMatches(record, item))
            .ToList();
        if (this.TryChooseNativeCandidate(record, nameMdlMatches, out var nameMdlChoice, out var nameMdlReason))
        {
            return this.CompleteNativeResolve(
                record,
                nameMdlMatches.Count == 1 ? NativeResolveStatus.FoundByNameMdl : NativeResolveStatus.FoundByFallback,
                nameMdlChoice,
                scanned,
                sameKind,
                nameMdlMatches,
                nameMdlReason,
                "name-mdl");
        }

        if (nameMdlMatches.Count > 1)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.MultipleCandidates,
                null,
                scanned,
                sameKind,
                nameMdlMatches,
                nameMdlReason,
                "name-mdl");
        }

        var fallbackMatches = sameKind
            .Where(item => this.NativeFallbackMatches(record, item))
            .ToList();
        if (this.TryChooseNativeCandidate(record, fallbackMatches, out var fallbackChoice, out var fallbackReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByFallback,
                fallbackChoice,
                scanned,
                sameKind,
                fallbackMatches,
                fallbackReason,
                "fallback");
        }

        if (fallbackMatches.Count > 1)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.MultipleCandidates,
                null,
                scanned,
                sameKind,
                fallbackMatches,
                fallbackReason,
                "fallback");
        }

        return this.CompleteNativeResolve(
            record,
            NativeResolveStatus.TrulyMissing,
            null,
            scanned,
            sameKind,
            [],
            "no stable, name+mdl, or validated fallback candidate matched",
            "none");
    }

    private NativeResolveResult ResolveNativeBgPartFreshDetailed(SceneEditorNativeModificationRecord record, uint currentTerritory)
    {
        var recordSgb = GetNativeBgPartRecordSgbPath(record);
        var recordAsset = GetNativeBgPartRecordAssetPath(record);
        var recordInitialIndex = GetNativeBgPartRecordInitialIndex(record);
        this.log.Debug(
            "[RestoreNativeBgPart] resolve begin map={MapKey} currentMap={CurrentMap} stableKey={StableKey} nativeBgPartStableKey={NativeBgPartStableKey} sgb={Sgb} asset={Asset} initialIndex={InitialIndex} address={Address} layoutAddress={LayoutAddress} initialAddress={InitialAddress} original={Original}",
            FirstNonEmpty(record.TerritoryKey, FormatTerritoryKey(record.TerritoryId)),
            FormatTerritoryKey(currentTerritory),
            record.StableKey,
            record.NativeBgPartStableKey,
            recordSgb,
            recordAsset,
            recordInitialIndex,
            record.NativeAddress,
            record.LayoutInstanceAddress,
            record.NativeBgPartInitialAddress,
            GetOriginalTransform(record).WorldPosition);

        if (this.TryResolveNativeBgPartByStoredPointer(record, out var pointerItem, out var pointerReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByPointer,
                pointerItem,
                [],
                [],
                pointerItem == null ? [] : [pointerItem],
                pointerReason,
                "stored-pointer");
        }

        this.log.Debug("[RestoreNativeBgPart] stored pointer unavailable stableKey={StableKey} reason={Reason}", record.StableKey, pointerReason);

        var scanned = this.GetNativeEditablesForRestore()
            .Where(item => item.IsNativeGameObject)
            .Where(item => !item.IsPlayer)
            .ToList();
        var sameKind = scanned
            .Where(item => item.Kind == SceneEditableKind.NativeBgPart)
            .ToList();
        this.log.Debug(
            "[RestoreNativeBgPart] scan complete stableKey={StableKey} scanned={Scanned} bgPartCandidates={BgPartCandidates} key=sgb:{Sgb};asset:{Asset};initialIndex:{InitialIndex}",
            record.StableKey,
            scanned.Count,
            sameKind.Count,
            recordSgb,
            recordAsset,
            recordInitialIndex);

        if (sameKind.Count == 0)
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.NotLoadedYet,
                null,
                scanned,
                sameKind,
                [],
                "current map scan returned no native BgPart objects",
                "scan");
        }

        var stableMatches = sameKind
            .Where(item => this.NativeStableKeyMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, stableMatches, "stable-key", out var stableChoice, out var stableReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByStableKey,
                stableChoice,
                scanned,
                sameKind,
                stableMatches,
                stableReason,
                "stable-key");
        }

        var stableIdentityMatches = sameKind
            .Where(item => this.NativeBgPartStableIdentityMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, stableIdentityMatches, "stable-identity", out var identityChoice, out var identityReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByStableIdentity,
                identityChoice,
                scanned,
                sameKind,
                stableIdentityMatches,
                identityReason,
                "stable-identity");
        }

        var sgbAssetMatches = sameKind
            .Where(item => this.NativeBgPartSgbAssetMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, sgbAssetMatches, "sgb-asset", out var sgbAssetChoice, out var sgbAssetReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByFallback,
                sgbAssetChoice,
                scanned,
                sameKind,
                sgbAssetMatches,
                sgbAssetReason,
                "sgb-asset");
        }

        var assetIndexMatches = sameKind
            .Where(item => this.NativeBgPartAssetIndexMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, assetIndexMatches, "asset-index", out var assetIndexChoice, out var assetIndexReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByFallback,
                assetIndexChoice,
                scanned,
                sameKind,
                assetIndexMatches,
                assetIndexReason,
                "asset-index");
        }

        var locatorMatches = sameKind
            .Where(item => this.LayoutLocatorMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, locatorMatches, "layout-locator", out var locatorChoice, out var locatorReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByStableIdentity,
                locatorChoice,
                scanned,
                sameKind,
                locatorMatches,
                locatorReason,
                "layout-locator");
        }

        var nameMdlMatches = sameKind
            .Where(item => this.NativeNameMdlMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, nameMdlMatches, "name-mdl", out var nameMdlChoice, out var nameMdlReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByNameMdl,
                nameMdlChoice,
                scanned,
                sameKind,
                nameMdlMatches,
                nameMdlReason,
                "name-mdl");
        }

        var fallbackMatches = sameKind
            .Where(item => this.NativeFallbackMatches(record, item))
            .ToList();
        if (this.TryChooseNativeBgPartCandidate(record, fallbackMatches, "legacy-fallback", out var fallbackChoice, out var fallbackReason))
        {
            return this.CompleteNativeResolve(
                record,
                NativeResolveStatus.FoundByFallback,
                fallbackChoice,
                scanned,
                sameKind,
                fallbackMatches,
                fallbackReason,
                "legacy-fallback");
        }

        this.LogNativeBgPartCandidateSet(record, "missing-scan-sample", sameKind);
        return this.CompleteNativeResolve(
            record,
            NativeResolveStatus.TrulyMissing,
            null,
            scanned,
            sameKind,
            [],
            $"no native BgPart matched stable identity after full scan; key=sgb:{recordSgb}; asset:{recordAsset}; initialIndex:{recordInitialIndex}",
            "none");
    }

    private bool TryResolveNativeBgPartByStoredPointer(
        SceneEditorNativeModificationRecord record,
        out SceneEditableRef? item,
        out string reason)
    {
        item = null;
        var checkedPointers = new List<string>();
        var storedPointers = NativeBgPartStoredPointers(record).ToList();
        if (storedPointers.Count == 0)
        {
            reason = "no stored pointer fields were available";
            return false;
        }

        var currentBgParts = this.GetNativeEditablesForRestore()
            .Where(candidate => candidate is { IsNativeGameObject: true, Kind: SceneEditableKind.NativeBgPart })
            .ToList();
        foreach (var (address, label) in storedPointers)
        {
            checkedPointers.Add($"{label}=0x{address:X}");
            var matches = currentBgParts
                .Where(candidate => candidate.NativePtr == address || ParsePointer(candidate.LayoutProbe?.Address ?? string.Empty) == address)
                .ToList();
            if (matches.Count == 0)
            {
                this.log.Debug("[RestoreNativeBgPart] stored pointer not present in current scan label={Label} address=0x{Address:X} scannedBgParts={Scanned}", label, address, currentBgParts.Count);
                continue;
            }

            var candidate = matches
                .OrderBy(candidate => Vector3.Distance(candidate.Transform.WorldPosition, GetOriginalTransform(record).WorldPosition))
                .First();
            if (!this.NativeBgPartPointerIdentityMatches(record, candidate, out var identityReason))
            {
                this.log.Debug(
                    "[RestoreNativeBgPart] pointer rejected label={Label} address=0x{Address:X} reason={Reason} sgb={Sgb} mdl={Mdl} originalDistance={Distance:F3}",
                    label,
                    address,
                    identityReason,
                    GetNativeBgPartItemSgbPath(candidate),
                    GetNativeBgPartItemAssetPath(candidate),
                    Vector3.Distance(candidate.Transform.WorldPosition, GetOriginalTransform(record).WorldPosition));
                continue;
            }

            item = candidate;
            reason = $"stored pointer matched current scan label={label}; {identityReason}";
            this.log.Debug(
                "[RestoreNativeBgPart] pointer selected label={Label} address=0x{Address:X} sgb={Sgb} mdl={Mdl} originalDistance={Distance:F3}",
                label,
                address,
                GetNativeBgPartItemSgbPath(candidate),
                GetNativeBgPartItemAssetPath(candidate),
                Vector3.Distance(candidate.Transform.WorldPosition, GetOriginalTransform(record).WorldPosition));
            return true;
        }

        reason = checkedPointers.Count == 0
            ? "no stored pointer fields were available"
            : $"stored pointers invalid or identity-mismatched: {string.Join(", ", checkedPointers)}";
        return false;
    }

    private static IEnumerable<(nint Address, string Label)> NativeBgPartStoredPointers(SceneEditorNativeModificationRecord record)
    {
        var seen = new HashSet<nint>();
        foreach (var (value, label) in new[]
                 {
                     (record.NativeAddress, "NativeAddress"),
                     (record.LayoutInstanceAddress, "LayoutInstanceAddress"),
                     (record.NativeBgPartInitialAddress, "NativeBgPartInitialAddress"),
                 })
        {
            var address = ParsePointer(value);
            if (address != 0 && seen.Add(address))
                yield return (address, label);
        }
    }

    private bool NativeBgPartPointerIdentityMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item, out string reason)
    {
        if (item.Kind != SceneEditableKind.NativeBgPart)
        {
            reason = $"candidate kind is {item.Kind}";
            return false;
        }

        var recordPaths = NativeBgPartRecordIdentityPaths(record).ToList();
        var itemPaths = NativeBgPartItemIdentityPaths(item).ToList();
        if (recordPaths.Count > 0 && itemPaths.Count > 0)
        {
            if (recordPaths.Any(recordPath => itemPaths.Any(itemPath => NativeTextEquals(recordPath, itemPath))))
            {
                reason = $"stored pointer accepted; identity path matched record={string.Join(",", recordPaths)}";
                return true;
            }

            reason = $"stored pointer accepted; identity path mismatch record={string.Join(",", recordPaths)}; candidate={string.Join(",", itemPaths)}";
            return true;
        }

        reason = "stored pointer accepted; identity path unavailable";
        return true;
    }

    private bool TryChooseNativeBgPartCandidate(
        SceneEditorNativeModificationRecord record,
        IReadOnlyList<SceneEditableRef> candidates,
        string stage,
        out SceneEditableRef? selected,
        out string reason)
    {
        selected = null;
        reason = "no candidates";
        this.LogNativeBgPartCandidateSet(record, stage, candidates);
        if (candidates.Count == 0)
            return false;

        var original = GetOriginalTransform(record).WorldPosition;
        var initialIndex = GetNativeBgPartRecordInitialIndex(record);
        var ranked = candidates
            .Select(item =>
            {
                var candidateIndex = item.LayoutProbe?.Index ?? -1;
                var indexDistance = initialIndex >= 0 && candidateIndex >= 0
                    ? Math.Abs(candidateIndex - initialIndex)
                    : int.MaxValue;
                return new
                {
                    Item = item,
                    OriginalDistance = Vector3.Distance(item.Transform.WorldPosition, original),
                    CandidateIndex = candidateIndex,
                    IndexDistance = indexDistance,
                };
            })
            .OrderBy(item => item.IndexDistance == 0 ? 0 : 1)
            .ThenBy(item => item.OriginalDistance)
            .ThenBy(item => item.IndexDistance)
            .ThenBy(item => item.CandidateIndex < 0 ? int.MaxValue : item.CandidateIndex)
            .ToList();
        if (ranked.Count == 0)
            return false;

        var best = ranked[0];
        selected = best.Item;
        var exactIndex = initialIndex >= 0 && best.CandidateIndex == initialIndex;
        reason = candidates.Count == 1
            ? $"single native BgPart candidate by {stage}; originalDistance={best.OriginalDistance:F3}; index={best.CandidateIndex}"
            : $"selected native BgPart by {stage}; exactInitialIndex={exactIndex}; initialIndex={initialIndex}; selectedIndex={best.CandidateIndex}; originalDistance={best.OriginalDistance:F3}; candidateCount={candidates.Count}";
        this.log.Debug("[RestoreNativeBgPart] selected stage={Stage} reason={Reason} selected={Selected}", stage, reason, DescribeNativeCandidate(selected));
        return true;
    }

    private void LogNativeBgPartCandidateSet(
        SceneEditorNativeModificationRecord record,
        string stage,
        IReadOnlyList<SceneEditableRef> candidates)
    {
        if (candidates.Count == 0)
        {
            this.log.Debug("[RestoreNativeBgPart] candidates stage={Stage} count=0", stage);
            return;
        }

        var original = GetOriginalTransform(record).WorldPosition;
        this.log.Debug("[RestoreNativeBgPart] candidates stage={Stage} count={Count}", stage, candidates.Count);
        foreach (var candidate in candidates)
        {
            this.log.Debug(
                "[RestoreNativeBgPart] candidate stage={Stage} ptr=0x{Ptr:X} stable={Stable} key={Key} sgb={Sgb} mdl={Mdl} index={Index} childIndex={ChildIndex} originalDistance={Distance:F3} pos={Position}",
                stage,
                candidate.NativePtr,
                FirstNonEmpty(candidate.StableKey, candidate.LayoutProbe?.StableKey),
                candidate.LayoutProbe?.Key ?? string.Empty,
                GetNativeBgPartItemSgbPath(candidate),
                GetNativeBgPartItemAssetPath(candidate),
                candidate.LayoutProbe?.Index ?? -1,
                candidate.LayoutProbe?.ChildIndex ?? -1,
                Vector3.Distance(candidate.Transform.WorldPosition, original),
                candidate.Transform.WorldPosition);
        }
    }

    private bool NativeBgPartStableIdentityMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        if (!this.NativeBgPartAssetMatches(record, item))
            return false;

        var recordSgb = GetNativeBgPartRecordSgbPath(record);
        if (HasNativeIdentityText(recordSgb) &&
            !NativeBgPartItemIdentityPaths(item).Any(itemPath => NativeTextEquals(recordSgb, itemPath)))
            return false;

        var initialIndex = GetNativeBgPartRecordInitialIndex(record);
        if (initialIndex >= 0)
            return item.LayoutProbe?.Index == initialIndex;

        if (record.ChildIndex >= 0)
            return item.LayoutProbe?.ChildIndex == record.ChildIndex;

        return HasNativeIdentityText(recordSgb);
    }

    private bool NativeBgPartSgbAssetMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        if (!this.NativeBgPartAssetMatches(record, item))
            return false;

        var recordSgb = GetNativeBgPartRecordSgbPath(record);
        return !HasNativeIdentityText(recordSgb) ||
               NativeBgPartItemIdentityPaths(item).Any(itemPath => NativeTextEquals(recordSgb, itemPath));
    }

    private bool NativeBgPartAssetIndexMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        if (!this.NativeBgPartAssetMatches(record, item))
            return false;

        var initialIndex = GetNativeBgPartRecordInitialIndex(record);
        return initialIndex >= 0 && item.LayoutProbe?.Index == initialIndex;
    }

    private bool NativeBgPartAssetMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        var recordPaths = NativeBgPartRecordIdentityPaths(record).ToList();
        var itemPaths = NativeBgPartItemIdentityPaths(item).ToList();
        return recordPaths.Count > 0 &&
               itemPaths.Count > 0 &&
               recordPaths.Any(recordPath => itemPaths.Any(itemPath => NativeTextEquals(recordPath, itemPath)));
    }

    private static IEnumerable<string> NativeBgPartRecordIdentityPaths(SceneEditorNativeModificationRecord record)
        => DistinctNativeIdentityPaths(
            record.NativeBgPartSgbPath,
            record.NativeBgPartAssetPath,
            record.NativeBgPartModelPath,
            record.MdlPath,
            record.NamePath,
            record.DisplayName);

    private static IEnumerable<string> NativeBgPartItemIdentityPaths(SceneEditableRef item)
        => DistinctNativeIdentityPaths(
            item.LayoutProbe?.ResourcePath,
            item.LayoutProbe?.ModelResourcePath,
            item.MdlPath,
            item.DisplayName);

    private static IEnumerable<string> DistinctNativeIdentityPaths(params string?[] values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizeNativeIdentityText(value ?? string.Empty);
            if (HasNativeIdentityText(normalized) && seen.Add(normalized))
                yield return normalized;
        }
    }

    private static string GetNativeBgPartRecordSgbPath(SceneEditorNativeModificationRecord record)
        => FirstNonEmpty(record.NativeBgPartSgbPath, record.NamePath, record.NativeBgPartAssetPath);

    private static string GetNativeBgPartRecordAssetPath(SceneEditorNativeModificationRecord record)
        => FirstNonEmpty(record.NativeBgPartModelPath, record.MdlPath, record.NativeBgPartAssetPath, record.NamePath, record.DisplayName);

    private static int GetNativeBgPartRecordInitialIndex(SceneEditorNativeModificationRecord record)
        => record.NativeBgPartInitialIndex >= 0
            ? record.NativeBgPartInitialIndex
            : record.LayoutInstanceIndexAtRecordTime;

    private static string GetNativeBgPartItemSgbPath(SceneEditableRef item)
        => item.LayoutProbe?.ResourcePath ?? string.Empty;

    private static string GetNativeBgPartItemAssetPath(SceneEditableRef item)
        => FirstNonEmpty(item.LayoutProbe?.ModelResourcePath, item.MdlPath, item.LayoutProbe?.ResourcePath, item.DisplayName);

    private static bool TryReadNativeBgPartWorldTransform(ILayoutInstance* pointer, out WorldTransform transform, out string reason)
    {
        transform = WorldTransform.FromEuler(Vector3.Zero, Vector3.Zero, Vector3.One);
        reason = string.Empty;
        try
        {
            if (pointer == null || pointer->Id.Type != InstanceType.BgPart)
            {
                reason = pointer == null ? "layout pointer is null" : $"layout pointer type is {pointer->Id.Type}";
                return false;
            }

            var bgPart = (BgPartsLayoutInstance*)pointer;
            if (bgPart->GraphicsObject != null && TryReadSceneObjectTransform((nint)bgPart->GraphicsObject, out transform))
                return true;

            var layoutTransform = pointer->GetTransformImpl();
            if (layoutTransform == null)
            {
                reason = "layout transform unavailable";
                return false;
            }

            transform = WorldTransform.FromEuler(
                layoutTransform->Translation,
                WorldTransformUtil.QuaternionToWorldEulerRadians(layoutTransform->Rotation),
                layoutTransform->Scale);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"read transform failed: {ex.Message}";
            return false;
        }
    }

    private static string ReadNativeLayoutPrimaryPath(ILayoutInstance* pointer)
    {
        try
        {
            var path = pointer->GetPrimaryPath();
            return path.HasValue ? path.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadNativeBgPartModelPath(BgPartsLayoutInstance* bgPart)
    {
        try
        {
            if (bgPart == null || bgPart->GraphicsObject == null)
                return string.Empty;

            var bg = (SceneBgObject*)bgPart->GraphicsObject;
            return bg->ModelResourceHandle == null
                ? string.Empty
                : bg->ModelResourceHandle->FileName.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private NativeResolveResult CompleteNativeResolve(
        SceneEditorNativeModificationRecord record,
        NativeResolveStatus status,
        SceneEditableRef? item,
        IReadOnlyList<SceneEditableRef> scanned,
        IReadOnlyList<SceneEditableRef> sameKind,
        IReadOnlyList<SceneEditableRef> candidates,
        string reason,
        string stage)
    {
        var result = new NativeResolveResult(
            status,
            item,
            reason,
            scanned.Count,
            scanned.Count(candidate => candidate.Kind == SceneEditableKind.NativeBgPart),
            sameKind.Count,
            candidates.Count);
        this.LogNativeResolveResult(record, result, sameKind, candidates, stage);
        return result;
    }

    private void LogNativeResolveResult(
        SceneEditorNativeModificationRecord record,
        NativeResolveResult result,
        IReadOnlyList<SceneEditableRef> sameKind,
        IReadOnlyList<SceneEditableRef> candidates,
        string stage)
    {
        this.log.Information(
            "[RestoreNative] resolve result={Status} stage={Stage} recordMap={RecordMap} currentMap={CurrentMap} kind={Kind} name={Name} mdl={Mdl} stableKey={StableKey} nativeBgPartStableKey={NativeBgPartStableKey} scanned={Scanned} scannedBgParts={ScannedBgParts} sameKind={SameKind} candidates={CandidateCount} reason={Reason} selected={Selected}",
            result.Status,
            stage,
            FirstNonEmpty(record.TerritoryKey, FormatTerritoryKey(record.TerritoryId)),
            FormatTerritoryKey(this.getTerritoryType()),
            record.Kind,
            FirstNonEmpty(record.NamePath, record.DisplayName),
            record.MdlPath,
            record.StableKey,
            record.NativeBgPartStableKey,
            result.ScannedCount,
            result.ScannedBgPartCount,
            result.SameKindCount,
            result.CandidateCount,
            result.Reason,
            result.Item == null ? "none" : DescribeNativeCandidate(result.Item));

        if (result.Status is NativeResolveStatus.TrulyMissing or NativeResolveStatus.MultipleCandidates or NativeResolveStatus.NotLoadedYet)
        {
            var exclusions = sameKind
                .Take(24)
                .Select(item => $"{DescribeNativeCandidate(item)} exclude={DescribeNativeCandidateExclusion(record, item)}");
            this.log.Debug(
                "[RestoreNative] resolve diagnostics stableKey={StableKey} stage={Stage} candidates={CandidateList} sameKindSample={SameKindSample}",
                record.StableKey,
                stage,
                string.Join(" || ", candidates.Take(24).Select(DescribeNativeCandidate)),
                string.Join(" || ", exclusions));
        }
    }

    private bool TryChooseNativeCandidate(
        SceneEditorNativeModificationRecord record,
        IReadOnlyList<SceneEditableRef> candidates,
        out SceneEditableRef? selected,
        out string reason)
    {
        selected = null;
        reason = "no candidates";
        if (candidates.Count == 0)
            return false;

        if (candidates.Count == 1)
        {
            selected = candidates[0];
            reason = "single candidate";
            return true;
        }

        var ranked = this.RankNativeCandidatesByTransform(record, candidates)
            .OrderBy(item => item.Metric)
            .ThenBy(item => item.Item.LayoutProbe?.Index ?? int.MaxValue)
            .ThenBy(item => item.Item.ObjectIndex < 0 ? int.MaxValue : item.Item.ObjectIndex)
            .ToList();
        if (ranked.Count == 0)
        {
            reason = "multiple candidates but no transform ranking was available";
            return false;
        }

        var best = ranked[0];
        if (ranked.Count == 1)
        {
            selected = best.Item;
            reason = $"only ranked candidate metric={best.Metric:F3}";
            return true;
        }

        var second = ranked[1];
        var hasExactTransformAnchor = best.Metric <= 0.05f && second.Metric > 0.15f;
        var hasClearDistanceMargin = second.Metric - best.Metric >= 0.10f;
        if (hasExactTransformAnchor || hasClearDistanceMargin)
        {
            selected = best.Item;
            reason = $"tie-break by original/current transform metric={best.Metric:F3}; next={second.Metric:F3}; originalDist={best.OriginalDistance:F3}; currentDist={best.CurrentDistance:F3}; hiddenDist={best.HiddenDistance:F3}";
            return true;
        }

        reason = $"multiple candidates tied after transform tie-break metric={best.Metric:F3}; next={second.Metric:F3}; count={candidates.Count}";
        return false;
    }

    private IEnumerable<NativeCandidateRank> RankNativeCandidatesByTransform(
        SceneEditorNativeModificationRecord record,
        IEnumerable<SceneEditableRef> candidates)
    {
        var original = GetOriginalTransform(record).WorldPosition;
        var current = GetCurrentTransform(record).WorldPosition;
        var hidden = ToVector3(record.HiddenPosition);
        foreach (var item in candidates)
        {
            var position = item.Transform.WorldPosition;
            var originalDistance = Vector3.Distance(position, original);
            var currentDistance = Vector3.Distance(position, current);
            var hiddenDistance = hidden == Vector3.Zero ? float.PositiveInfinity : Vector3.Distance(position, hidden);
            var metric = MathF.Min(MathF.Min(originalDistance, currentDistance), hiddenDistance);
            yield return new NativeCandidateRank(item, metric, originalDistance, currentDistance, hiddenDistance);
        }
    }

    private float DistanceFromRecordAnchors(SceneEditorNativeModificationRecord record, Vector3 position)
    {
        var originalDistance = Vector3.Distance(position, GetOriginalTransform(record).WorldPosition);
        var currentDistance = Vector3.Distance(position, GetCurrentTransform(record).WorldPosition);
        var hidden = ToVector3(record.HiddenPosition);
        var hiddenDistance = hidden == Vector3.Zero ? float.PositiveInfinity : Vector3.Distance(position, hidden);
        return MathF.Min(MathF.Min(originalDistance, currentDistance), hiddenDistance);
    }

    private bool NativeStableKeyMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        var recordKeys = NativeRecordStableKeys(record).ToList();
        if (recordKeys.Count == 0)
            return false;

        var itemKeys = NativeItemStableKeys(item).ToList();
        return recordKeys.Any(recordKey =>
            itemKeys.Any(itemKey => string.Equals(recordKey, itemKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> NativeRecordStableKeys(SceneEditorNativeModificationRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.NativeBgPartStableKey))
            yield return record.NativeBgPartStableKey;
        if (!string.IsNullOrWhiteSpace(record.StableKey))
            yield return record.StableKey;
    }

    private static IEnumerable<string> NativeItemStableKeys(SceneEditableRef item)
    {
        if (!string.IsNullOrWhiteSpace(item.StableKey))
            yield return item.StableKey;
        if (!string.IsNullOrWhiteSpace(item.LayoutProbe?.StableKey))
            yield return item.LayoutProbe.StableKey;
    }

    private bool NativeNameMdlMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        var recordName = FirstNonEmpty(record.NamePath, record.DisplayName);
        var recordMdl = record.MdlPath;
        var itemName = FirstNonEmpty(item.LayoutProbe?.ResourcePath, item.DisplayName);
        var itemMdl = item.MdlPath;

        if (!HasNativeIdentityText(recordName) || !HasNativeIdentityText(recordMdl))
            return false;

        var nameMatches = NativeTextEquals(recordName, itemName) ||
                          NativeTextEquals(recordName, itemMdl);
        var mdlMatches = NativeTextEquals(recordMdl, itemMdl) ||
                         NativeTextEquals(recordMdl, itemName);
        return nameMatches && mdlMatches;
    }

    private bool NativeFallbackMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        if (this.NativeAddressFallbackMatches(record, item))
            return true;

        if (this.LayoutLocatorMatches(record, item))
            return true;

        if (HasNativeIdentityText(record.MdlPath) &&
            (NativeTextEquals(record.MdlPath, item.MdlPath) ||
             NativeTextEquals(record.MdlPath, item.DisplayName) ||
             NativeTextEquals(record.MdlPath, item.LayoutProbe?.ResourcePath ?? string.Empty)))
        {
            return true;
        }

        var recordName = FirstNonEmpty(record.NamePath, record.DisplayName);
        if (HasNativeIdentityText(recordName) &&
            (NativeTextEquals(recordName, item.DisplayName) ||
             NativeTextEquals(recordName, item.LayoutProbe?.ResourcePath ?? string.Empty) ||
             NativeTextEquals(recordName, item.MdlPath)))
        {
            return true;
        }

        return record.Kind is SceneEditableKind.NativeActor or SceneEditableKind.EventNpc &&
               !string.IsNullOrWhiteSpace(record.DataId) &&
               string.Equals(record.DataId, item.DataId, StringComparison.OrdinalIgnoreCase) &&
               HasNativeIdentityText(recordName) &&
               NativeTextEquals(recordName, item.DisplayName);
    }

    private bool NativeAddressFallbackMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        var nativeAddress = ParsePointer(record.NativeAddress);
        if (nativeAddress == 0 || nativeAddress != item.NativePtr)
            return false;

        return this.NativeNameMdlMatches(record, item) ||
               this.LayoutLocatorMatches(record, item) ||
               (!string.IsNullOrWhiteSpace(record.RuntimeIdAtRecordTime) &&
                string.Equals(item.RuntimeId, record.RuntimeIdAtRecordTime, StringComparison.OrdinalIgnoreCase));
    }

    private bool LayoutLocatorMatches(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        var probe = item.LayoutProbe;
        if (probe == null)
            return false;

        var matched = false;
        if (!string.IsNullOrWhiteSpace(record.SourceKind))
        {
            if (!string.Equals(record.SourceKind, probe.SourceKind, StringComparison.OrdinalIgnoreCase))
                return false;
            matched = true;
        }

        if (!string.IsNullOrWhiteSpace(record.SharedGroupPath))
        {
            if (!NativeTextEquals(record.SharedGroupPath, probe.SharedGroupPath))
                return false;
            matched = true;
        }

        if (!string.IsNullOrWhiteSpace(record.ParentStableKey))
        {
            if (!NativeTextEquals(NormalizeNativeLocatorText(record.ParentStableKey), NormalizeNativeLocatorText(probe.ParentKey)))
                return false;
            matched = true;
        }

        if (record.ChildIndex >= 0)
        {
            if (record.ChildIndex != probe.ChildIndex)
                return false;
            matched = true;
        }

        if (matched && record.LayoutInstanceIndexAtRecordTime >= 0)
        {
            if (record.LayoutInstanceIndexAtRecordTime != probe.Index)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(record.LayoutSource))
        {
            var normalizedRecordSource = NormalizeNativeLocatorText(record.LayoutSource);
            var normalizedProbeSource = NormalizeNativeLocatorText(probe.Source);
            if (!string.IsNullOrWhiteSpace(normalizedRecordSource) &&
                !string.Equals(normalizedRecordSource, normalizedProbeSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matched = true;
        }

        if (matched && HasNativeIdentityText(record.MdlPath) && HasNativeIdentityText(item.MdlPath) &&
            !NativeTextEquals(record.MdlPath, item.MdlPath) &&
            !NativeTextEquals(record.MdlPath, item.LayoutProbe?.ResourcePath ?? string.Empty))
        {
            return false;
        }

        return matched;
    }

    private void EnsureNativeRecordLocatorFields(SceneEditorNativeModificationRecord record)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(record.TerritoryKey) && record.TerritoryId != 0)
        {
            record.TerritoryKey = FormatTerritoryKey(record.TerritoryId);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(record.NamePath) && !string.IsNullOrWhiteSpace(record.DisplayName))
        {
            record.NamePath = record.DisplayName;
            changed = true;
        }

        if (record.Kind == SceneEditableKind.NativeBgPart &&
            string.IsNullOrWhiteSpace(record.NativeBgPartStableKey) &&
            !string.IsNullOrWhiteSpace(record.StableKey))
        {
            record.NativeBgPartStableKey = record.StableKey;
            changed = true;
        }

        if (record.Kind == SceneEditableKind.NativeBgPart)
        {
            if (string.IsNullOrWhiteSpace(record.NativeBgPartSgbPath))
            {
                var sgbPath = FirstNonEmpty(record.NamePath, record.NativeBgPartAssetPath, record.MdlPath);
                if (!string.IsNullOrWhiteSpace(sgbPath))
                {
                    record.NativeBgPartSgbPath = sgbPath;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(record.NativeBgPartAssetPath))
            {
                var assetPath = FirstNonEmpty(record.NamePath, record.MdlPath, record.DisplayName);
                if (!string.IsNullOrWhiteSpace(assetPath))
                {
                    record.NativeBgPartAssetPath = assetPath;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(record.NativeBgPartModelPath) &&
                !string.IsNullOrWhiteSpace(record.MdlPath) &&
                !NativeTextEquals(record.MdlPath, record.NativeBgPartSgbPath) &&
                !NativeTextEquals(record.MdlPath, record.NativeBgPartAssetPath))
            {
                record.NativeBgPartModelPath = record.MdlPath;
                changed = true;
            }

            if (record.NativeBgPartInitialIndex < 0 && record.LayoutInstanceIndexAtRecordTime >= 0)
            {
                record.NativeBgPartInitialIndex = record.LayoutInstanceIndexAtRecordTime;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(record.NativeBgPartInitialAddress))
            {
                var address = FirstNonEmpty(record.LayoutInstanceAddress, record.NativeAddress);
                if (!string.IsNullOrWhiteSpace(address))
                {
                    record.NativeBgPartInitialAddress = address;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            record.LastModifiedAt = DateTime.UtcNow;
            this.MarkPersistDirty("Native record locator backfilled");
        }
    }

    private static bool IsNativeResolveStatus(string value)
        => Enum.GetNames(typeof(NativeResolveStatus))
            .Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));

    private static string ToNativeRestoreFailureStatus(string reason)
    {
        foreach (var name in Enum.GetNames(typeof(NativeResolveStatus)))
        {
            if (reason.Contains(name, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        if (reason.Contains("copy", StringComparison.OrdinalIgnoreCase))
            return NativeResolveStatus.BlockedByCopy.ToString();
        if (reason.Contains("territory", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("not loaded", StringComparison.OrdinalIgnoreCase))
            return NativeResolveStatus.NotLoadedYet.ToString();
        if (reason.Contains("multiple", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
            return NativeResolveStatus.MultipleCandidates.ToString();

        return "RestoreFailed";
    }

    private static string DescribeNativeCandidate(SceneEditableRef item)
        => $"ptr=0x{item.NativePtr:X}; runtime={item.RuntimeId}; stable={FirstNonEmpty(item.StableKey, item.LayoutProbe?.StableKey)}; key={item.LayoutProbe?.Key ?? string.Empty}; name={item.DisplayName}; mdl={item.MdlPath}; sgb={item.LayoutProbe?.SharedGroupPath ?? string.Empty}; index={item.LayoutProbe?.Index ?? -1}; pos={item.Transform.WorldPosition}; source={item.LayoutProbe?.Source ?? string.Empty}";

    private string DescribeNativeCandidateExclusion(SceneEditorNativeModificationRecord record, SceneEditableRef item)
    {
        if (this.NativeStableKeyMatches(record, item))
            return "matched stable key";
        if (this.LayoutLocatorMatches(record, item))
            return "matched layout locator";
        if (this.NativeNameMdlMatches(record, item))
            return "matched name+mdl";
        if (this.NativeFallbackMatches(record, item))
            return "matched fallback";

        var reasons = new List<string>();
        var recordName = FirstNonEmpty(record.NamePath, record.DisplayName);
        var itemName = FirstNonEmpty(item.LayoutProbe?.ResourcePath, item.DisplayName);
        if (HasNativeIdentityText(recordName) && !NativeTextEquals(recordName, itemName) && !NativeTextEquals(recordName, item.MdlPath))
            reasons.Add("name differs");
        if (HasNativeIdentityText(record.MdlPath) && !NativeTextEquals(record.MdlPath, item.MdlPath) && !NativeTextEquals(record.MdlPath, itemName))
            reasons.Add("mdl differs");
        if (!NativeRecordStableKeys(record).Any(recordKey => NativeItemStableKeys(item).Any(itemKey => string.Equals(recordKey, itemKey, StringComparison.OrdinalIgnoreCase))))
            reasons.Add("stable key differs");
        if (!string.IsNullOrWhiteSpace(record.SourceKind) && !string.Equals(record.SourceKind, item.LayoutProbe?.SourceKind, StringComparison.OrdinalIgnoreCase))
            reasons.Add("source kind differs");
        if (!string.IsNullOrWhiteSpace(record.SharedGroupPath) && !NativeTextEquals(record.SharedGroupPath, item.LayoutProbe?.SharedGroupPath ?? string.Empty))
            reasons.Add("shared group differs");
        if (record.ChildIndex >= 0 && record.ChildIndex != item.LayoutProbe?.ChildIndex)
            reasons.Add("child index differs");

        return reasons.Count == 0 ? "insufficient identity fields" : string.Join(", ", reasons);
    }

    private static bool NativeTextEquals(string left, string right)
        => string.Equals(NormalizeNativeIdentityText(left), NormalizeNativeIdentityText(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNativeIdentityText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace('\\', '/');
    }

    private static bool HasNativeIdentityText(string value)
    {
        var normalized = NormalizeNativeIdentityText(value);
        return !string.IsNullOrWhiteSpace(normalized) &&
               !string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNativeLocatorText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (i + 2 < text.Length &&
                text[i] == '0' &&
                (text[i + 1] == 'x' || text[i + 1] == 'X') &&
                IsHexDigit(text[i + 2]))
            {
                var j = i + 2;
                while (j < text.Length && IsHexDigit(text[j]))
                    j++;

                builder.Append("0x*");
                i = j;
                continue;
            }

            builder.Append(char.IsWhiteSpace(text[i]) ? ' ' : text[i]);
            i++;
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private bool IsHiddenRecord(string stableKey)
        => !string.IsNullOrWhiteSpace(stableKey) &&
           this.NativeRecords.Any(item =>
               item.IsHidden &&
               string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));

    private bool IsHiddenRecordApplied(string stableKey, Vector3 currentPosition)
    {
        if (string.IsNullOrWhiteSpace(stableKey))
            return false;

        var record = this.NativeRecords.FirstOrDefault(item =>
            item.IsHidden &&
            string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (record == null)
            return false;

        var hidden = ToVector3(record.HiddenPosition);
        return hidden != Vector3.Zero && Vector3.Distance(currentPosition, hidden) <= 3f;
    }

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
           WorldTransformUtil.RotationsEquivalent(left.WorldEulerRadians, right.WorldEulerRadians, 0.001f) &&
           Vector3.Distance(left.WorldScale, right.WorldScale) <= 0.001f;

    private static bool TransformsRestoredEnough(WorldTransform left, WorldTransform right)
        => Vector3.Distance(left.WorldPosition, right.WorldPosition) <= 0.05f &&
           WorldTransformUtil.RotationsEquivalent(left.WorldEulerRadians, right.WorldEulerRadians, 0.02f) &&
           Vector3.Distance(left.WorldScale, right.WorldScale) <= 0.02f;

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

    private static SceneEditorNativeLightState CaptureNativeLightState(
        SceneEditableRef selected,
        WorldTransform transform,
        bool? visible = null)
    {
        var state = new SceneEditorNativeLightState
        {
            HasState = true,
            Visible = visible ?? !selected.IsHidden,
            Enabled = visible ?? !selected.IsHidden,
        };
        SetVector3Data(state.Position, transform.WorldPosition);
        SetVector3Data(state.RotationEuler, transform.WorldEulerRadians);
        SetVector3Data(state.Scale, WorldTransformUtil.NormalizeScale(transform.WorldScale));
        return state;
    }

    private static void SetRecordOriginalLightState(SceneEditorNativeModificationRecord record, SceneEditorNativeLightState state)
        => CopyNativeLightState(state, record.OriginalLightState ??= new SceneEditorNativeLightState());

    private static void SetRecordCurrentLightState(SceneEditorNativeModificationRecord record, SceneEditorNativeLightState state)
        => CopyNativeLightState(state, record.CurrentLightState ??= new SceneEditorNativeLightState());

    private static void CopyNativeLightState(SceneEditorNativeLightState source, SceneEditorNativeLightState target)
    {
        target.HasState = source.HasState;
        SetVector3Data(target.Position, ToVector3(source.Position));
        SetVector3Data(target.RotationEuler, ToVector3(source.RotationEuler));
        SetVector3Data(target.Scale, WorldTransformUtil.NormalizeScale(ToVector3(source.Scale)));
        target.Visible = source.Visible;
        target.Enabled = source.Enabled;
        target.HasRenderState = source.HasRenderState;
        SetVector3Data(target.Color, ToVector3(source.Color));
        target.Intensity = source.Intensity;
        target.Range = source.Range;
        target.Falloff = source.Falloff;
        target.SpotAngle = source.SpotAngle;
        target.FalloffAngle = source.FalloffAngle;
    }

    private static WorldTransform NativeLightStateToWorldTransform(SceneEditorNativeLightState state)
        => WorldTransform.FromEuler(
            ToVector3(state.Position),
            ToVector3(state.RotationEuler),
            WorldTransformUtil.NormalizeScale(ToVector3(state.Scale)));

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

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatTerritoryKey(uint territoryId)
        => territoryId == 0 ? string.Empty : $"territory:{territoryId}";

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

        var territory = this.getTerritoryType();
        foreach (var light in this.localLights.Instances.Where(item => item.TerritoryId == territory))
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
            var stableKey = $"{this.getTerritoryType()}:{kind}:{objectKind}:data={dataId}:name={name}:index={index}";
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
                this.IsHiddenRecordApplied(stableKey, position))
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
        var previousMaxResults = this.layoutProbe.MaxResults;

        try
        {
            this.layoutProbe.NearbyOnly = !restoreScan;
            this.layoutProbe.MaxDistance = restoreScan ? 10000f : 80f;
            this.layoutProbe.MaxResults = restoreScan ? 10000 : 250;
            this.layoutProbe.SortByDistance = true;
            this.layoutProbe.ShowBgPart = restoreScan || this.ShowBgParts;
            this.layoutProbe.ShowSharedGroup = false;
            this.layoutProbe.ShowLight = restoreScan || this.ShowLights;
            this.layoutProbe.ShowTerrain = false;
            this.layoutProbe.ShowCamera = false;
            this.layoutProbe.ShowCharacter = false;
            this.layoutProbe.TypeFilter = string.Empty;
            this.layoutProbe.EnumerateInstances(this.playerPositionProvider());

            foreach (var instance in this.layoutProbe.Instances.Take(restoreScan ? 10000 : 160))
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

                var stableKey = FirstNonEmpty(instance.StableKey, instance.Key);
                var rotationEuler = WorldTransformUtil.QuaternionToWorldEulerRadians(instance.RotationQuaternion);
                var transform = WorldTransform.FromEuler(instance.Position, rotationEuler, instance.Scale == Vector3.Zero ? Vector3.One : instance.Scale);
                var record = restoreScan ? null : this.GetNativeModificationRecordByStableKey(stableKey);
                if (record is { IsHidden: true })
                    transform = GetHiddenOrCurrentTransform(record);
                else if (record is { IsModified: true })
                    transform = GetCurrentTransform(record);

                var appliedHidden = !instance.Visible ||
                                    this.IsHiddenRecordApplied(stableKey, instance.Position) ||
                                    this.IsHiddenRecordApplied(instance.Key, instance.Position);
                result.Add(new SceneEditableRef(
                    $"native-layout:{instance.Type}:{instance.Address}:{instance.Key}",
                    kind,
                    address,
                    -1,
                    string.IsNullOrWhiteSpace(instance.ResourcePath) ? instance.Type : instance.ResourcePath,
                    kind == SceneEditableKind.NativeBgPart
                        ? FirstNonEmpty(instance.ModelResourcePath, instance.ResourcePath)
                        : instance.ResourcePath,
                    false,
                    transform,
                    true,
                    appliedHidden)
                {
                    IsNativeGameObject = true,
                    StableKey = stableKey,
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
            this.layoutProbe.MaxResults = previousMaxResults;
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

    private enum NativeResolveStatus
    {
        FoundByPointer,
        FoundByStableKey,
        FoundByStableIdentity,
        FoundByNameMdl,
        FoundByFallback,
        BlockedByCopy,
        NotLoadedYet,
        MultipleCandidates,
        TrulyMissing,
    }

    private sealed record NativeResolveResult(
        NativeResolveStatus Status,
        SceneEditableRef? Item,
        string Reason,
        int ScannedCount,
        int ScannedBgPartCount,
        int SameKindCount,
        int CandidateCount);

    private sealed record NativeCandidateRank(
        SceneEditableRef Item,
        float Metric,
        float OriginalDistance,
        float CurrentDistance,
        float HiddenDistance);

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

    private bool ApplyNativeLayoutTransform(
        string runtimeId,
        WorldTransform transform,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All)
    {
        if (this.IsBgPartCollisionConfirmationRequired(SceneEditableKind.NativeBgPart))
            return this.BlockUnconfirmedBgPartCollisionTransform(SceneEditableKind.NativeBgPart, "Native", runtimeId);

        this.lastNativeScanUtc = DateTime.MinValue;
        this.nativeCache.Clear();
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
        return this.ApplyFreshNativeLayoutTransform(runtimeId, target, transform, this.NativeFullLayoutTransformConfirmed, components);
    }

    private bool ApplyFreshNativeLayoutTransform(SceneEditorNativeModificationRecord record, SceneEditableRef current, WorldTransform transform)
    {
        this.UpdateNativeRecordLocator(record, current);
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

    private bool ApplyFreshNativeLayoutTransform(
        string runtimeId,
        FreshNativeLayoutTarget target,
        WorldTransform transform,
        bool fullLayoutConfirmed,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All,
        bool updateGeneration = true,
        bool invalidateNativeCache = true)
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
                if (!this.TryWriteSceneObjectTransform(graphicsTarget, transform, out var writeReason, components))
                {
                    this.LastStatus = $"Native BgPart visual transform failed: {writeReason}";
                    this.LastBgPartCollisionOperation = $"source=Native; collisionMode=Off; collision operation=Failed; handle=0x{target.Address:X}; {writeReason}";
                    return false;
                }

                if (updateGeneration)
                    this.TransformGeneration++;
                if (invalidateNativeCache)
                    this.lastNativeScanUtc = DateTime.MinValue;
                this.LastBgPartCollisionOperation = $"source=Native; collisionMode=Off; collision operation=Skipped; handle=0x{target.Address:X}; graphics=0x{graphicsObjectAddress:X}";
                this.LastStatus = $"Native BgPart visual-only transform applied: {runtimeId}";
                return true;
            }

            var normalizedScale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
            if (!IsTransformNormal(transform.WorldPosition, transform.WorldRotation, normalizedScale))
            {
                this.LastStatus = "Native layout transform skipped: requested transform is not finite or has invalid scale.";
                if (pointer->Id.Type == InstanceType.BgPart)
                    this.LastBgPartCollisionOperation = $"source=Native; collisionMode=On; collision operation=Failed; handle=0x{target.Address:X}; invalid requested transform";
                return false;
            }

            var nativeTransform = new Transform
            {
                Translation = transform.WorldPosition,
                Rotation = transform.WorldRotation,
                Scale = normalizedScale,
            };
            if (components != SceneEditorTransformComponents.All)
            {
                var currentTransform = pointer->GetTransformImpl();
                if (currentTransform == null)
                {
                    this.LastStatus = "Native layout transform skipped: current transform unavailable.";
                    if (pointer->Id.Type == InstanceType.BgPart)
                        this.LastBgPartCollisionOperation = $"source=Native; collisionMode=On; collision operation=Failed; handle=0x{target.Address:X}; current transform unavailable";
                    return false;
                }

                nativeTransform.Translation = HasTransformComponent(components, SceneEditorTransformComponents.Position)
                    ? transform.WorldPosition
                    : currentTransform->Translation;
                nativeTransform.Rotation = HasTransformComponent(components, SceneEditorTransformComponents.Rotation)
                    ? transform.WorldRotation
                    : currentTransform->Rotation;
                nativeTransform.Scale = HasTransformComponent(components, SceneEditorTransformComponents.Scale)
                    ? normalizedScale
                    : WorldTransformUtil.NormalizeScale(currentTransform->Scale);
            }

            pointer->SetTransform(&nativeTransform);
            if (updateGeneration)
                this.TransformGeneration++;
            if (invalidateNativeCache)
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

    private bool TryWriteSceneObjectTransform(
        FreshNativeLayoutTarget target,
        WorldTransform transform,
        out string reason,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All)
    {
        if (!this.ValidateFreshNativeLayoutTarget(target, out reason))
            return false;

        var normalizedScale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
        if (!IsTransformNormal(transform.WorldPosition, transform.WorldRotation, normalizedScale))
        {
            reason = "requested transform is not finite or has invalid scale";
            return false;
        }

        if (!ValidateSceneBgObjectWritable(target.Address, out reason))
            return false;

        var obj = (SceneObject*)target.Address;
        var bg = (SceneBgObject*)target.Address;
        if (HasTransformComponent(components, SceneEditorTransformComponents.Position))
            obj->Position = transform.WorldPosition;
        if (HasTransformComponent(components, SceneEditorTransformComponents.Rotation))
            obj->Rotation = transform.WorldRotation;
        if (HasTransformComponent(components, SceneEditorTransformComponents.Scale))
            obj->Scale = normalizedScale;
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
        reason = string.Empty;
        return true;
    }

    private static bool TryReadSceneObjectTransform(nint graphicsObjectAddress, out WorldTransform transform)
    {
        transform = WorldTransform.FromEuler(Vector3.Zero, Vector3.Zero, Vector3.One);
        if (graphicsObjectAddress == 0)
            return false;

        try
        {
            var obj = (SceneObject*)graphicsObjectAddress;
            transform = WorldTransform.FromEuler(
                obj->Position,
                WorldTransformUtil.QuaternionToWorldEulerRadians(obj->Rotation),
                obj->Scale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateSceneBgObjectWritable(nint graphicsObjectAddress, out string reason)
    {
        reason = string.Empty;
        if (graphicsObjectAddress == 0)
        {
            reason = "GraphicsObject address is zero";
            return false;
        }

        try
        {
            var vtable = *(nint*)graphicsObjectAddress;
            if (vtable == 0)
            {
                reason = "GraphicsObject vtable is zero";
                return false;
            }

            var bg = (SceneBgObject*)graphicsObjectAddress;
            if (bg->ModelResourceHandle == null)
            {
                reason = "ModelResourceHandle=null";
                return false;
            }

            if (bg->ModelResourceHandle->LoadState < 7)
            {
                reason = $"ModelResourceHandle LoadState={bg->ModelResourceHandle->LoadState}";
                return false;
            }

            var obj = (SceneObject*)graphicsObjectAddress;
            if (!IsTransformNormal(obj->Position, obj->Rotation, obj->Scale))
            {
                reason = "current Graphics.Scene.Object transform is invalid";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"GraphicsObject validation failed: {ex.Message}";
            return false;
        }
    }

    private static bool IsTransformNormal(Vector3 position, Quaternion rotation, Vector3 scale)
        => IsVectorNormal(position)
            && IsQuaternionNormal(rotation)
            && IsVectorNormal(scale)
            && Math.Abs(scale.X) > 0.0001f
            && Math.Abs(scale.Y) > 0.0001f
            && Math.Abs(scale.Z) > 0.0001f;

    private static bool IsVectorNormal(Vector3 value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && Math.Abs(value.X) < 1_000_000f
            && Math.Abs(value.Y) < 1_000_000f
            && Math.Abs(value.Z) < 1_000_000f;

    private static bool IsQuaternionNormal(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() is > 0.0001f and < 10f;

    private bool ApplyNativeActorTransform(
        string runtimeId,
        WorldTransform transform,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All)
    {
        var current = this.GetEditables().FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (current == null || current.NativePtr == 0 || current.IsPlayer)
        {
            this.LastStatus = "Native actor transform failed: invalid target or LocalPlayer.";
            return false;
        }

        return this.ApplyNativeActorTransformAddress(runtimeId, current.NativePtr, transform, components);
    }

    private bool ApplyNativeActorTransformAddress(
        string runtimeId,
        nint address,
        WorldTransform transform,
        SceneEditorTransformComponents components = SceneEditorTransformComponents.All,
        bool updateGeneration = true,
        bool invalidateNativeCache = true)
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
            if (HasTransformComponent(components, SceneEditorTransformComponents.Position))
                gameObject->Position = transform.WorldPosition;
            if (gameObject->DrawObject != null)
            {
                var drawObject = (DrawObject*)gameObject->DrawObject;
                if (HasTransformComponent(components, SceneEditorTransformComponents.Position))
                    drawObject->Position = transform.WorldPosition;
                if (HasTransformComponent(components, SceneEditorTransformComponents.Rotation))
                    drawObject->Rotation = transform.WorldRotation;
                if (HasTransformComponent(components, SceneEditorTransformComponents.Scale))
                    drawObject->Scale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
            }

            if (updateGeneration)
                this.TransformGeneration++;
            if (invalidateNativeCache)
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
            StableKey = FirstNonEmpty(instance.SourceParentKey, instance.OccupiedSlotAddress),
            SourceKind = instance.SourceKind,
            ResourcePath = FirstNonEmpty(instance.CurrentResourcePath, instance.CustomModelPath, instance.TemplateResourcePath, instance.SourceResourcePath),
            Position = instance.CurrentPosition,
            RotationQuaternion = instance.CurrentRotation,
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
        var previousMaxResults = this.layoutProbe.MaxResults;

        try
        {
            this.layoutProbe.NearbyOnly = false;
            this.layoutProbe.MaxDistance = 10000f;
            this.layoutProbe.MaxResults = 10000;
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
            this.layoutProbe.MaxResults = previousMaxResults;
        }
    }
}
