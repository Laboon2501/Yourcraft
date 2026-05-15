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

    public float MarkerRadius { get; set; } = 5.5f;

    public SceneEditorGizmoService Gizmo { get; }

    public SceneEditorUndoService Undo { get; }

    public SceneEditorGizmoMode GizmoMode => this.Gizmo.Mode;

    public uint TransformGeneration { get; private set; }

    public string LastStatus { get; private set; } = "Scene Editor ready.";

    public string LastHoveredMarkerDebug { get; private set; } = "none";

    public string LastClickedMarkerDebug { get; private set; } = "none";

    public string LastGizmoDebug { get; private set; } = "idle";

    public string LastNativeScanStatus { get; private set; } = "Native scan not started.";

    public string LastQuickActionStatus { get; private set; } = "No SceneEditor quick action yet.";

    private List<SceneEditorNativeModificationRecord> NativeRecords => this.configuration.SceneEditorNativeModifications ??= [];

    public IReadOnlyList<SceneEditorNativeModificationRecord> NativeModificationRecords => this.NativeRecords;

    public RuntimeActorInstance? GetLocalActor(string runtimeId)
        => this.actors.Actors.FirstOrDefault(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    public LocalLightInstance? GetLocalLight(string runtimeId)
        => this.localLights.GetById(runtimeId);

    public void SetGizmoMode(SceneEditorGizmoMode mode)
        => this.Gizmo.SetMode(mode);

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
                var rotation = actor.TransformEditRotationEuler != Vector3.Zero ? actor.TransformEditRotationEuler : actor.LastKnownRotationEuler;
                var scale = actor.TransformEditScale == Vector3.Zero ? actor.LastKnownScale : actor.TransformEditScale;
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
        var scale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
        this.log.Debug("[SceneEditor] ApplyWorldTransform kind={Kind} id={Id} pos={Position} rot={Rotation} scale={Scale}",
            kind,
            runtimeId,
            transform.WorldPosition,
            transform.WorldEulerRadians,
            scale);

        switch (kind)
        {
            case SceneEditableKind.LocalActor:
                var actorResult = this.actors.ApplyActorTransform(runtimeId, transform.WorldPosition, transform.WorldEulerRadians, scale);
                this.LastStatus = this.actors.LastMessage;
                if (actorResult)
                    this.TransformGeneration++;
                return actorResult;
            case SceneEditableKind.LocalBgPart:
                this.localLayoutObjects.ApplyVisualTransform(runtimeId, transform.WorldPosition, transform.WorldEulerRadians, scale);
                this.LastStatus = this.localLayoutObjects.LastStatus;
                this.TransformGeneration++;
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
                this.localLights.RequestApply(runtimeId);
                this.LastStatus = this.localLights.LastStatus;
                this.TransformGeneration++;
                return true;
            case SceneEditableKind.NativeBgPart:
            case SceneEditableKind.NativeLight:
                if (!this.AllowNativeTransformWrites)
                {
                    this.LastStatus = "Native transform write blocked: unsafe/native writes are disabled.";
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
                this.actors.SaveActorTransformSnapshot(selected.RuntimeId, transform.WorldPosition, transform.WorldEulerRadians, transform.WorldScale);
                this.LastStatus = this.actors.LastMessage;
                return true;
            case SceneEditableKind.LocalBgPart:
                this.localLayoutObjects.ApplyVisualTransform(selected.RuntimeId, transform.WorldPosition, transform.WorldEulerRadians, transform.WorldScale);
                this.LastStatus = this.localLayoutObjects.LastStatus;
                return true;
            case SceneEditableKind.LocalLight:
                this.ApplyWorldTransform(selected.Kind, selected.RuntimeId, transform);
                return true;
            default:
                return false;
        }
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
        var mode = this.NativeFullLayoutTransformConfirmed
            ? LocalLayoutTransformMode.FullLayoutWithCollision
            : LocalLayoutTransformMode.VisualOnly;
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
        this.LastQuickActionStatus = $"Copied one BgPart: {created.Id}; mode={mode}";
        this.TransformGeneration++;
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
            this.saveConfiguration();
            return false;
        }

        SetRecordCurrentTransform(record, hiddenTransform);
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

        this.saveConfiguration();
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
            this.saveConfiguration();
            this.LastStatus = "LocalPlayer restore is blocked.";
            return false;
        }

        var original = GetOriginalTransform(record);
        if (!this.ApplyNativeRecordTransform(record, original))
        {
            record.Status = "Missing";
            record.LastModifiedAt = DateTime.UtcNow;
            this.saveConfiguration();
            this.LastStatus = $"Native restore failed or object missing: {record.DisplayName}";
            return false;
        }

        SetRecordCurrentTransform(record, original);
        record.IsHidden = false;
        record.IsModified = false;
        record.Status = "Restored";
        record.LastModifiedAt = DateTime.UtcNow;
        this.saveConfiguration();
        this.LastStatus = $"Native object restored: {record.DisplayName}";
        this.log.Information("[SceneEditor] Native restore kind={Kind} key={Key} name={Name}", record.Kind, record.StableKey, record.DisplayName);
        return true;
    }

    public int RestoreAllHiddenNativeObjects()
    {
        var records = this.NativeRecords.Where(item => item.IsHidden).Select(item => item.RecordId).ToList();
        var restored = 0;
        foreach (var id in records)
        {
            if (this.RestoreNativeModification(id))
                restored++;
        }

        this.LastStatus = $"Restored hidden native objects: {restored}/{records.Count}";
        return restored;
    }

    public int RestoreAllNativeModifications()
    {
        var records = this.NativeRecords
            .Where(item => item.IsHidden || item.IsModified)
            .Select(item => item.RecordId)
            .ToList();
        var restored = 0;
        foreach (var id in records)
        {
            if (this.RestoreNativeModification(id))
                restored++;
        }

        this.LastStatus = $"Restored native modifications: {restored}/{records.Count}";
        return restored;
    }

    public bool RemoveNativeModificationRecord(string recordId)
    {
        var removed = this.NativeRecords.RemoveAll(item => string.Equals(item.RecordId, recordId, StringComparison.OrdinalIgnoreCase));
        if (removed <= 0)
            return false;

        this.saveConfiguration();
        this.LastStatus = "Removed native modification record.";
        return true;
    }

    public int CleanupInactiveNativeModificationRecords()
    {
        var removed = this.NativeRecords.RemoveAll(item =>
            string.Equals(item.Status, "Restored", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Status, "Missing", StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            this.saveConfiguration();

        this.LastStatus = $"Cleaned native modification records: {removed}";
        return removed;
    }

    private SceneEditableRef? FindEditable(SceneEditableKind kind, string runtimeId)
        => this.GetEditables().FirstOrDefault(item =>
            item.Kind == kind &&
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    private WorldTransform ReadNativeTransformOrFallback(SceneEditableRef selected)
    {
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
        this.saveConfiguration();
    }

    private SceneEditorNativeModificationRecord EnsureNativeRecord(SceneEditableRef selected, WorldTransform original, string reason)
    {
        var stableKey = this.GetStableKey(selected);
        var record = this.NativeRecords.FirstOrDefault(item =>
            string.Equals(item.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
        if (record != null)
            return record;

        record = new SceneEditorNativeModificationRecord
        {
            RecordId = Guid.NewGuid().ToString("N"),
            StableKey = stableKey,
            RuntimeIdAtRecordTime = selected.RuntimeId,
            NativeAddress = selected.NativePtr == 0 ? string.Empty : $"0x{selected.NativePtr:X}",
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
        if (record.TerritoryId != 0 && record.TerritoryId != this.getTerritoryType())
        {
            this.LastStatus = "Native restore skipped: record belongs to another territory.";
            return false;
        }

        this.lastNativeScanUtc = DateTime.MinValue;
        var current = this.GetEditables().FirstOrDefault(item =>
            item.IsNativeGameObject &&
            item.Kind == record.Kind &&
            string.Equals(this.GetStableKey(item), record.StableKey, StringComparison.OrdinalIgnoreCase));

        if (current != null)
        {
            return record.Kind switch
            {
                SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight
                    => this.ApplyNativeLayoutTransformAddress(record.RuntimeIdAtRecordTime, current.NativePtr, transform, record.UseFullLayoutTransform),
                SceneEditableKind.NativeActor or SceneEditableKind.EventNpc
                    => this.ApplyNativeActorTransformAddress(record.RuntimeIdAtRecordTime, current.NativePtr, transform),
                _ => false,
            };
        }

        var address = ParsePointer(record.NativeAddress);
        if (address == 0)
            return false;

        return record.Kind switch
        {
            SceneEditableKind.NativeBgPart or SceneEditableKind.NativeLight
                => this.ApplyNativeLayoutTransformAddress(record.RuntimeIdAtRecordTime, address, transform, record.UseFullLayoutTransform),
            _ => false,
        };
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

        return $"{this.getTerritoryType()}:{selected.Kind}:{selected.RuntimeId}:{selected.NativePtr:X}:{selected.ObjectIndex}:{selected.DisplayName}";
    }

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
                this.AddNativeLayoutObjects(this.nativeCache, pluginPointers);

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
            var stableKey = $"{objectKind}:{index}:{dataId}:{address:X}";
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

    private void AddNativeLayoutObjects(List<SceneEditableRef> result, HashSet<nint> pluginPointers)
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
            this.layoutProbe.NearbyOnly = true;
            this.layoutProbe.MaxDistance = 80f;
            this.layoutProbe.SortByDistance = true;
            this.layoutProbe.ShowBgPart = this.ShowBgParts;
            this.layoutProbe.ShowSharedGroup = false;
            this.layoutProbe.ShowLight = this.ShowLights;
            this.layoutProbe.ShowTerrain = false;
            this.layoutProbe.ShowCamera = false;
            this.layoutProbe.ShowCharacter = false;
            this.layoutProbe.TypeFilter = string.Empty;
            this.layoutProbe.EnumerateInstances(this.playerPositionProvider());

            foreach (var instance in this.layoutProbe.Instances.Take(160))
            {
                var address = ParsePointer(instance.Address);
                if (address != 0 && pluginPointers.Contains(address))
                    continue;

                var kind = instance.Type.Equals("Light", StringComparison.OrdinalIgnoreCase)
                    ? SceneEditableKind.NativeLight
                    : SceneEditableKind.NativeBgPart;
                if (kind == SceneEditableKind.NativeBgPart && !this.ShowBgParts)
                    continue;
                if (kind == SceneEditableKind.NativeLight && !this.ShowLights)
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

        return this.ApplyNativeLayoutTransformAddress(runtimeId, address, transform, this.NativeFullLayoutTransformConfirmed);
    }

    private bool ApplyNativeLayoutTransformAddress(string runtimeId, nint address, WorldTransform transform, bool fullLayoutConfirmed)
    {
        try
        {
            var pointer = (ILayoutInstance*)address;
            if (pointer->Id.Type == InstanceType.BgPart && !fullLayoutConfirmed)
            {
                if (!TryGetGraphicsObjectAddress(pointer, out var graphicsObjectAddress))
                {
                    this.LastStatus = "Native BgPart visual transform failed: GraphicsObject unavailable.";
                    return false;
                }

                WriteSceneObjectTransform(graphicsObjectAddress, transform);
                this.TransformGeneration++;
                this.lastNativeScanUtc = DateTime.MinValue;
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
            this.LastStatus = $"Native layout transform applied: {runtimeId}";
            return true;
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Native layout transform failed: {ex.Message}";
            this.log.Warning(ex, "[SceneEditor] Native layout transform failed. id={Id}", runtimeId);
            return false;
        }
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

    private static void WriteSceneObjectTransform(nint graphicsObjectAddress, WorldTransform transform)
    {
        var obj = (SceneObject*)graphicsObjectAddress;
        var bg = (SceneBgObject*)graphicsObjectAddress;
        obj->Position = transform.WorldPosition;
        obj->Rotation = transform.WorldRotation;
        obj->Scale = WorldTransformUtil.NormalizeScale(transform.WorldScale);
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
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
            this.layoutProbe.MaxDistance = 200f;
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
