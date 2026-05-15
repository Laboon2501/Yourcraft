using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using RenderLightFalloffType = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFalloffType;
using RenderLightFlags = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFlags;
using RenderLightShape = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightShape;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace LocalQuestReborn.Services;

public sealed unsafe class LocalLightNativeService
{
    private const string PoolName = "LocalQuestReborn.LocalLights.SceneLight";
    private const int MaxNativeOpsPerFrame = 16;
    private const int SceneTeardownCooldownFrames = 2;

    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Action save;
    private readonly Func<uint> getTerritoryType;
    private readonly Queue<NativeOperation> pendingOperations = [];
    private readonly HashSet<nint> activeLights = [];
    private readonly HashSet<nint> reusableLights = [];

    private uint nativeGeneration;
    private bool disposed;
    private bool sceneTearingDown;
    private int sceneTeardownCooldown;

    public LocalLightNativeService(Configuration configuration, IPluginLog log, Action save, Func<uint>? getTerritoryType = null)
    {
        this.configuration = configuration;
        this.log = log;
        this.save = save;
        this.getTerritoryType = getTerritoryType ?? (() => 0);

        foreach (var light in this.configuration.LocalLights)
            this.ClearRuntimePointers(light, needsRecreate: light.Enabled, operation: "loaded-config");
    }

    public IReadOnlyList<LocalLightInstance> Instances => this.configuration.LocalLights;

    public string LastStatus { get; private set; } = "LocalLights native service ready.";

    public int PendingOperationCount => this.pendingOperations.Count;

    public LocalLightInstance? GetById(string id)
        => this.configuration.LocalLights.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public LocalLightInstance CreateDebugPointAt(Vector3 position)
        => this.Create(LocalLightKind.Point, "Debug PointLight", position + new Vector3(0f, 2f, 0f), Vector3.Zero, Vector3.One);

    public LocalLightInstance Create(LocalLightKind kind, string name, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        this.EndSceneTeardownForUserAction();

        var light = new LocalLightInstance
        {
            Id = $"local-light-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..45],
            Name = name,
            Enabled = true,
            Hidden = false,
            TerritoryId = this.getTerritoryType(),
            LightKind = kind,
            Position = position,
            Rotation = rotation,
            Scale = NormalizeScale(scale),
            ColorRgb = kind == LocalLightKind.Point ? new Vector3(1f, 0.18f, 0.1f) : Vector3.One,
            Intensity = kind == LocalLightKind.Point ? 35f : 25f,
            Range = kind == LocalLightKind.Point ? 30f : 25f,
            FalloffType = LocalLightFalloffType.Quadratic,
            Falloff = 1f,
            LightAngle = 35f,
            FalloffAngle = 15f,
            AreaAngleX = 35f,
            AreaAngleY = 35f,
            EnableSpecular = true,
            LastOperation = "created-config",
            NeedsNativeRecreate = true,
        };

        this.configuration.LocalLights.Add(light);
        this.save();
        this.EnqueueCreateOrApply(light, "create-native");
        return light;
    }

    public void RequestApply(string id)
    {
        var light = this.GetById(id);
        if (light == null)
        {
            this.LastStatus = $"Light not found: {id}";
            return;
        }

        if (this.disposed)
        {
            this.LastStatus = "LocalLights is disposed; apply skipped.";
            return;
        }

        this.EndSceneTeardownForUserAction();
        if (light.TerritoryId == 0)
            light.TerritoryId = this.getTerritoryType();
        this.Normalize(light);
        this.save();
        if (!light.Enabled)
        {
            this.LastStatus = $"Light {light.Name} is disabled; config saved without native apply.";
            return;
        }

        this.EnqueueCreateOrApply(light, "apply");
    }

    public void RequestSetEnabled(string id, bool enabled)
    {
        var light = this.GetById(id);
        if (light == null)
        {
            this.LastStatus = $"Light not found: {id}";
            return;
        }

        if (this.disposed)
        {
            this.LastStatus = "LocalLights is disposed; enabled toggle skipped.";
            return;
        }

        this.EndSceneTeardownForUserAction();
        light.Enabled = enabled;
        light.NeedsNativeRecreate = enabled && light.NativeSceneLight == 0;
        this.save();

        if (enabled)
            this.EnqueueCreateOrApply(light, "enable");
        else
            this.EnqueueSoftDisable(light, "Enabled=false");
    }

    public void RequestDelete(string id)
    {
        var light = this.GetById(id);
        if (light == null)
        {
            this.LastStatus = $"Light not found: {id}";
            return;
        }

        this.ReleaseLightToReusable(light, removeConfig: true, reason: "delete");
    }

    public void RequestDeleteAll()
        => this.RequestDestroyAllSafe("delete-all-local-lights", keepInstances: false);

    public void DestroyAllNative(string reason, bool keepInstances)
    {
        this.RequestDestroyAllSafe(reason, keepInstances);
        if (reason.Contains("插件卸载", StringComparison.Ordinal) || reason.Contains("unload", StringComparison.OrdinalIgnoreCase))
            this.disposed = true;
    }

    public void Update()
    {
        if (this.disposed)
        {
            this.pendingOperations.Clear();
            return;
        }

        if (this.sceneTearingDown)
        {
            if (this.sceneTeardownCooldown > 0)
            {
                this.sceneTeardownCooldown--;
                return;
            }

            this.sceneTearingDown = false;
            this.LastStatus = "LocalLights scene teardown cooldown ended; enabled lights may recreate.";
        }

        var initialCount = this.pendingOperations.Count;
        for (var processed = 0; this.pendingOperations.Count > 0 && processed < MaxNativeOpsPerFrame && processed < initialCount; processed++)
        {
            var op = this.pendingOperations.Dequeue();
            if (op.DelayFrames > 0)
            {
                op.DelayFrames--;
                this.pendingOperations.Enqueue(op);
                continue;
            }

            if (!this.CanRunOperation(op, out var skipReason))
            {
                this.MarkOperationSkipped(op, skipReason);
                continue;
            }

            try
            {
                switch (op.Kind)
                {
                    case NativeOperationKind.CreateOrApply:
                        this.RunCreateOrApply(op);
                        break;
                    case NativeOperationKind.SoftDisablePointer:
                        this.SoftDisablePointerNow(op.Pointer, op.Name);
                        break;
                    case NativeOperationKind.ReleaseToReusable:
                        this.ReleasePointerToReusableNow(op.Pointer, op.Name);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (op.Light != null)
                {
                    op.Light.NativeOperationPending = false;
                    op.Light.LastError = ex.Message;
                    op.Light.LastOperation = $"{op.Name} failed";
                }

                this.LastStatus = $"LocalLights native op failed: {op.Name}; pointer=0x{op.Pointer:X}; {ex.Message}";
                this.log.Warning(ex, "LocalLights native operation failed: {Operation} {Pointer:X}", op.Name, op.Pointer);
            }
        }

        foreach (var light in this.configuration.LocalLights)
        {
            if (!light.Enabled || !light.NeedsNativeRecreate || light.NativeOperationPending || light.NativeSceneLight != 0)
                continue;
            if (light.TerritoryId == 0 || light.TerritoryId != this.getTerritoryType())
            {
                light.LastOperation = light.TerritoryId == 0
                    ? "LegacyNoTerritory: skipped automatic native recreate."
                    : $"Skipped wrong territory: light={light.TerritoryId}, current={this.getTerritoryType()}";
                continue;
            }

            this.EnqueueCreateOrApply(light, "recreate-enabled");
            break;
        }
    }

    public void MarkVisibleResult(string id, bool visible)
    {
        var light = this.GetById(id);
        if (light == null)
            return;

        light.ManuallyConfirmedVisible = visible;
        light.ManuallyConfirmedNotVisible = !visible;
        this.save();
    }

    private void RequestDestroyAllSafe(string reason, bool keepInstances)
    {
        if (this.sceneTearingDown && this.activeLights.Count == 0 && this.reusableLights.Count == 0 && this.pendingOperations.Count == 0)
        {
            this.LastStatus = $"LocalLights already handled scene teardown; skipped duplicate cleanup: {reason}";
            return;
        }

        this.nativeGeneration++;
        this.sceneTearingDown = keepInstances;
        this.sceneTeardownCooldown = keepInstances ? SceneTeardownCooldownFrames : 0;

        var activePointers = this.activeLights
            .Concat(this.configuration.LocalLights.Select(light => light.NativeSceneLight))
            .Where(pointer => pointer != 0)
            .Distinct()
            .ToArray();
        var reusablePointers = this.reusableLights
            .Where(pointer => pointer != 0)
            .Distinct()
            .ToArray();

        this.pendingOperations.Clear();

        foreach (var light in this.configuration.LocalLights.ToList())
        {
            this.ClearRuntimePointers(light, needsRecreate: keepInstances && light.Enabled, operation: $"cleanup-detached ({reason})");
            light.NativeOperationPending = false;
            if (!keepInstances)
                this.configuration.LocalLights.Remove(light);
        }

        if (keepInstances)
        {
            this.log.Debug(
                "LocalLights Territory cleanup: activeCount={ActiveCount} reusableCount={ReusableCount}; soft-disable only, no dtor; reason={Reason}",
                activePointers.Length,
                reusablePointers.Length,
                reason);
            foreach (var pointer in activePointers.Concat(reusablePointers).Distinct())
                this.SoftDisablePointerBestEffort(pointer, $"scene-cleanup:{reason}");
            this.activeLights.Clear();
            this.reusableLights.Clear();
        }
        else
        {
            foreach (var pointer in activePointers)
                this.QueueReleaseToReusable(pointer, $"delete-all:{reason}");
        }

        this.save();
        this.LastStatus = keepInstances
            ? $"LocalLights scene cleanup soft-disabled active={activePointers.Length}, reusable={reusablePointers.Length}; no dtor; {reason}"
            : $"LocalLights delete-all removed config and queued {activePointers.Length} pointers into reusable pool; {reason}";
    }

    private void ReleaseLightToReusable(LocalLightInstance light, bool removeConfig, string reason)
    {
        this.nativeGeneration++;

        var pointer = light.NativeSceneLight;
        this.log.Debug("LocalLights Delete requested id={LightId} ptr=0x{Pointer:X} reason={Reason}", light.Id, pointer, reason);
        this.PurgePendingOperations(op => op.Light == light || (pointer != 0 && op.Pointer == pointer));
        this.ClearRuntimePointers(light, needsRecreate: false, operation: $"SoftDeleteQueued ({reason})");
        light.Enabled = false;
        light.NativeOperationPending = false;

        if (removeConfig)
            this.configuration.LocalLights.Remove(light);

        this.QueueReleaseToReusable(pointer, reason);

        this.save();
        this.LastStatus = $"LocalLights removed config and queued soft delete: {light.Name}; pointer=0x{pointer:X}; reason={reason}";
    }

    private void QueueReleaseToReusable(nint pointer, string reason)
    {
        if (pointer == 0)
            return;

        this.pendingOperations.Enqueue(new NativeOperation(
            null,
            string.Empty,
            reason,
            pointer,
            this.nativeGeneration,
            NativeOperationKind.ReleaseToReusable));
        this.log.Debug("LocalLights SoftDelete queued ptr=0x{Pointer:X} reason={Reason}", pointer, reason);
    }

    private void EnqueueCreateOrApply(LocalLightInstance light, string name)
    {
        if (this.disposed)
        {
            this.LastStatus = $"LocalLights is disposed; skipped {name}.";
            return;
        }

        if (this.sceneTearingDown)
        {
            this.LastStatus = $"LocalLights is in scene teardown cooldown; skipped {name}.";
            return;
        }

        if (light.NativeOperationPending)
        {
            this.LastStatus = $"Light {light.Name} already has a pending native op; skipped {name}.";
            return;
        }

        light.NativeOperationPending = true;
        light.LastOperation = $"{name} queued";
        this.pendingOperations.Enqueue(new NativeOperation(
            light,
            light.Id,
            name,
            light.NativeSceneLight,
            this.nativeGeneration,
            NativeOperationKind.CreateOrApply));
        this.LastStatus = $"Queued LocalLights native op: {name}; light={light.Name}";
    }

    private void RunCreateOrApply(NativeOperation op)
    {
        var light = op.Light;
        if (light == null)
            return;

        if (light.NativeSceneLight == 0)
            this.CreateNative(light);
        else
            this.ApplyNative(light);

        light.NativeOperationPending = false;
    }

    private bool CanRunOperation(NativeOperation op, out string reason)
    {
        if (this.disposed)
        {
            reason = "service disposed";
            return false;
        }

        if (this.sceneTearingDown)
        {
            reason = "scene tearing down";
            return false;
        }

        if (op.Generation != this.nativeGeneration)
        {
            reason = $"generation mismatch op={op.Generation}, current={this.nativeGeneration}";
            return false;
        }

        if (op.Kind == NativeOperationKind.ReleaseToReusable)
        {
            if (op.Pointer == 0)
            {
                reason = "release pointer is zero";
                return false;
            }

            if (!this.activeLights.Contains(op.Pointer) && !this.reusableLights.Contains(op.Pointer))
            {
                reason = "release pointer is not tracked";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (op.Kind == NativeOperationKind.SoftDisablePointer)
        {
            if (op.Pointer == 0)
            {
                reason = "soft disable pointer is zero";
                return false;
            }

            if (!this.activeLights.Contains(op.Pointer) && !this.reusableLights.Contains(op.Pointer))
            {
                reason = "soft disable pointer is not tracked";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (op.Light == null)
        {
            reason = "missing light instance";
            return false;
        }

        if (!this.configuration.LocalLights.Contains(op.Light))
        {
            reason = "light removed from configuration";
            return false;
        }

        var currentTerritory = this.getTerritoryType();
        if (op.Light.TerritoryId == 0)
        {
            reason = "legacy light has no TerritoryId";
            return false;
        }

        if (currentTerritory == 0 || op.Light.TerritoryId != currentTerritory)
        {
            reason = $"wrong territory light={op.Light.TerritoryId}, current={currentTerritory}";
            return false;
        }

        if (!op.Light.Enabled)
        {
            reason = "light disabled";
            return false;
        }

        var pointer = op.Light.NativeSceneLight;
        if (pointer == 0)
        {
            reason = string.Empty;
            return true;
        }

        if (!this.activeLights.Contains(pointer))
        {
            reason = $"pointer 0x{pointer:X} is not active";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void MarkOperationSkipped(NativeOperation op, string reason)
    {
        if (op.Light != null)
        {
            op.Light.NativeOperationPending = false;
            op.Light.LastOperation = $"{op.Name} skipped";
            op.Light.LastError = reason;
        }

        this.LastStatus = $"LocalLights native op skipped: {op.Name}; pointer=0x{op.Pointer:X}; {reason}";
    }

    private void EnqueueSoftDisable(LocalLightInstance light, string reason)
    {
        this.log.Debug("LocalLights Disable requested light={LightId} ptr=0x{Pointer:X} reason={Reason}", light.Id, light.NativeSceneLight, reason);
        this.PurgePendingOperations(op => op.Light == light && op.Kind == NativeOperationKind.CreateOrApply);
        light.NativeOperationPending = true;
        light.LastOperation = $"SoftDisable queued ({reason})";

        if (light.NativeSceneLight == 0)
        {
            light.NativeOperationPending = false;
            light.NeedsNativeRecreate = false;
            this.LastStatus = $"LocalLights soft disable skipped: {light.Name}; no native pointer";
            return;
        }

        this.pendingOperations.Enqueue(new NativeOperation(
            light,
            light.Id,
            reason,
            light.NativeSceneLight,
            this.nativeGeneration,
            NativeOperationKind.SoftDisablePointer));
        this.log.Debug("LocalLights SoftDisable op queued light={LightId} ptr=0x{Pointer:X}", light.Id, light.NativeSceneLight);
        this.LastStatus = $"LocalLights queued soft disable: {light.Name}; ptr=0x{light.NativeSceneLight:X}";
    }

    private void CreateNative(LocalLightInstance light)
    {
        if (!light.Enabled)
        {
            light.NeedsNativeRecreate = false;
            return;
        }

        if (light.NativeSceneLight != 0)
            this.ReleaseLightToReusable(light, removeConfig: false, reason: "recreate-before-create");

        SceneLight* sceneLight;
        nint pointer;
        if (this.TryTakeReusablePointer(out pointer))
        {
            sceneLight = (SceneLight*)pointer;
            this.activeLights.Add(pointer);
            this.log.Debug("LocalLights Create reused ptr=0x{Pointer:X}", pointer);
        }
        else
        {
            sceneLight = SceneLight.Create(ToRenderShape(light.LightKind), PoolName, null);
            if (sceneLight == null)
            {
                this.Fail(light, "Scene.Light.Create returned null.");
                return;
            }

            pointer = (nint)sceneLight;
            this.activeLights.Add(pointer);
            this.log.Debug("LocalLights Create new ptr=0x{Pointer:X}", pointer);
        }

        light.NativeSceneLight = pointer;
        light.NativeRenderLight = (nint)sceneLight->RenderLight;
        light.NeedsNativeRecreate = false;
        light.LastOperation = "Scene.Light ready";

        this.ApplyNative(light);
    }

    private bool TryTakeReusablePointer(out nint pointer)
    {
        pointer = 0;
        if (this.reusableLights.Count == 0)
            return false;

        pointer = this.reusableLights.First();
        this.reusableLights.Remove(pointer);
        return pointer != 0;
    }

    private void ApplyNative(LocalLightInstance light)
    {
        if (!this.TryGetAliveSceneLight(light, out var sceneLight, out var reason))
        {
            this.Fail(light, reason);
            return;
        }

        var renderLight = sceneLight->RenderLight;
        if (renderLight == null)
        {
            light.NativeRenderLight = 0;
            this.Fail(light, "Scene.Light.RenderLight=null.");
            return;
        }

        this.Normalize(light);

        sceneLight->Position = light.Position;
        sceneLight->Rotation = ToQuaternion(light.Rotation);
        sceneLight->Scale = light.Scale;
        sceneLight->IsVisible = light.Enabled && !light.Hidden;
        sceneLight->IsTransformChanged = true;

        renderLight->LightShape = ToRenderShape(light.LightKind);
        renderLight->LightFlags = light.EnableSpecular ? RenderLightFlags.SpecularHighlights : 0;
        if (light.EnableDynamicShadows)
            renderLight->LightFlags |= RenderLightFlags.DynamicShadows;
        renderLight->Color = ClampColor(light.ColorRgb);
        renderLight->Intensity = light.Hidden ? 0f : MathF.Max(0f, light.Intensity);
        renderLight->Range = MathF.Max(0.1f, light.Range);
        renderLight->MaxRange = BuildRangeBounds(renderLight->Range);
        renderLight->FalloffType = ToRenderFalloff(light.FalloffType);
        renderLight->FalloffFactor = MathF.Max(0f, light.Falloff);
        renderLight->SpotLightAngleDegrees = Math.Clamp(light.LightAngle, 0f, 90f);
        renderLight->AngularFalloffDegrees = Math.Clamp(light.FalloffAngle, 0f, 90f);
        renderLight->FlatLightSkewAngleDegrees = new Vector2(Math.Clamp(light.AreaAngleX, 0f, 90f), Math.Clamp(light.AreaAngleY, 0f, 90f));
        renderLight->CharacterShadowRange = 0f;

        sceneLight->NotifyTransformChanged();
        sceneLight->UpdateTransforms(true);
        sceneLight->UpdateMaterials();
        sceneLight->UpdateCulling();
        sceneLight->UpdateRender();

        light.NativeRenderLight = (nint)renderLight;
        light.LastError = string.Empty;
        light.LastOperation = "ApplyNative";
        light.LastReadback = this.BuildReadback(sceneLight);
        this.LastStatus = $"Applied LocalLight: {light.Name}; scene=0x{light.NativeSceneLight:X}; render=0x{light.NativeRenderLight:X}";
    }

    private void ReleasePointerToReusableNow(nint pointer, string reason)
    {
        this.log.Debug("LocalLights SoftDelete begin ptr=0x{Pointer:X} reason={Reason}", pointer, reason);
        this.SoftDisablePointerNow(pointer, $"soft-delete:{reason}");
        this.activeLights.Remove(pointer);
        this.reusableLights.Add(pointer);
        this.LastStatus = $"LocalLights soft-deleted pointer into reusable pool: 0x{pointer:X}; reason={reason}";
        this.log.Debug("LocalLights SoftDelete end ptr=0x{Pointer:X}", pointer);
        this.log.Debug("LocalLights Moved to reusable ptr=0x{Pointer:X} reusableCount={ReusableCount}", pointer, this.reusableLights.Count);
    }

    private void SoftDisablePointerBestEffort(nint pointer, string reason)
    {
        if (pointer == 0)
            return;

        try
        {
            this.SoftDisablePointerNow(pointer, reason);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "LocalLights best-effort soft disable failed: {Pointer:X}", pointer);
        }
    }

    private void SoftDisablePointerNow(nint pointer, string reason)
    {
        this.log.Debug("LocalLights SoftDisable begin ptr=0x{Pointer:X} reason={Reason}", pointer, reason);
        var sceneLight = (SceneLight*)pointer;
        var renderLight = sceneLight->RenderLight;
        if (renderLight != null)
        {
            renderLight->Color = Vector3.Zero;
            renderLight->Intensity = 0f;
            renderLight->LightFlags = 0;
        }

        var light = this.configuration.LocalLights.FirstOrDefault(item => item.NativeSceneLight == pointer);
        if (light != null)
        {
            light.NativeOperationPending = false;
            light.NeedsNativeRecreate = false;
            light.NativeRenderLight = (nint)renderLight;
            light.LastOperation = $"SoftDisabled ({reason})";
            light.LastError = string.Empty;
            light.LastReadback = renderLight == null
                ? $"soft disabled ptr=0x{pointer:X}; render=null"
                : $"soft disabled ptr=0x{pointer:X}; intensity=0; flags=0; range kept={renderLight->Range:F2}";
        }

        this.LastStatus = $"LocalLights soft-disabled pointer=0x{pointer:X}; reason={reason}";
        this.log.Debug("LocalLights SoftDisable end ptr=0x{Pointer:X}", pointer);
    }

    private bool TryGetAliveSceneLight(LocalLightInstance light, out SceneLight* sceneLight, out string reason)
    {
        sceneLight = null;
        reason = string.Empty;
        var pointer = light.NativeSceneLight;
        if (pointer == 0)
        {
            reason = "NativeSceneLight=0; cannot apply native params.";
            return false;
        }

        if (!this.activeLights.Contains(pointer))
        {
            reason = $"NativeSceneLight 0x{pointer:X} is not active; native write skipped.";
            return false;
        }

        sceneLight = (SceneLight*)pointer;
        return true;
    }

    private string BuildReadback(SceneLight* light)
    {
        var render = light->RenderLight;
        if (render == null)
            return $"scene=0x{(nint)light:X}; render=null; visible={light->IsVisible}; pos={Format(light->Position)}";

        return string.Join("; ", [
            $"scene=0x{(nint)light:X}",
            $"render=0x{(nint)render:X}",
            $"shape={render->LightShape}",
            $"visible={light->IsVisible}",
            $"pos={Format(light->Position)}",
            $"rot={light->Rotation}",
            $"scale={Format(light->Scale)}",
            $"color={Format(render->Color)}",
            $"intensity={render->Intensity:F2}",
            $"range={render->Range:F2}",
            $"falloff={render->FalloffType}/{render->FalloffFactor:F2}",
            $"spot={render->SpotLightAngleDegrees:F1}/{render->AngularFalloffDegrees:F1}",
            $"area={render->FlatLightSkewAngleDegrees.X:F1},{render->FlatLightSkewAngleDegrees.Y:F1}",
        ]);
    }

    private void PurgePendingOperations(Predicate<NativeOperation> shouldRemove)
    {
        if (this.pendingOperations.Count == 0)
            return;

        var kept = this.pendingOperations.Where(op => !shouldRemove(op)).ToArray();
        this.pendingOperations.Clear();
        foreach (var op in kept)
            this.pendingOperations.Enqueue(op);
    }

    private void ClearRuntimePointers(LocalLightInstance light, bool needsRecreate, string operation)
    {
        light.NativeSceneLight = 0;
        light.NativeRenderLight = 0;
        light.NativeOperationPending = false;
        light.NeedsNativeRecreate = needsRecreate;
        light.LastOperation = operation;
        light.LastReadback = string.Empty;
    }

    private void EndSceneTeardownForUserAction()
    {
        this.sceneTearingDown = false;
        this.sceneTeardownCooldown = 0;
    }

    private void Fail(LocalLightInstance light, string reason)
    {
        light.LastError = reason;
        light.LastOperation = "failed";
        light.NativeOperationPending = false;
        this.LastStatus = $"LocalLights failed: {reason}";
        this.log.Warning("LocalLights failed for {LightId}: {Reason}", light.Id, reason);
    }

    private void Normalize(LocalLightInstance light)
    {
        light.Scale = NormalizeScale(light.Scale);
        light.Range = MathF.Max(0.1f, light.Range);
        light.Intensity = MathF.Max(0f, light.Intensity);
        light.Falloff = MathF.Max(0f, light.Falloff);
        light.LightAngle = Math.Clamp(light.LightAngle, 0f, 90f);
        light.FalloffAngle = Math.Clamp(light.FalloffAngle, 0f, 90f);
        light.AreaAngleX = Math.Clamp(light.AreaAngleX, 0f, 90f);
        light.AreaAngleY = Math.Clamp(light.AreaAngleY, 0f, 90f);
        light.ColorRgb = ClampColor(light.ColorRgb);
    }

    private static Vector3 NormalizeScale(Vector3 scale)
        => new(SafeScale(scale.X), SafeScale(scale.Y), SafeScale(scale.Z));

    private static float SafeScale(float value)
        => float.IsFinite(value) && MathF.Abs(value) > 0.0001f ? value : 1f;

    private static Vector3 ClampColor(Vector3 color)
        => new(Math.Clamp(color.X, 0f, 4f), Math.Clamp(color.Y, 0f, 4f), Math.Clamp(color.Z, 0f, 4f));

    private static AxisAlignedBounds BuildRangeBounds(float range)
    {
        range = MathF.Max(0.1f, range);
        return new AxisAlignedBounds(new Vector3(-range, -range, -range), new Vector3(range, range, range));
    }

    private static Quaternion ToQuaternion(Vector3 euler)
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
        return Quaternion.Normalize(rotation);
    }

    private static RenderLightShape ToRenderShape(LocalLightKind kind)
        => kind switch
        {
            LocalLightKind.Directional => RenderLightShape.WorldLight,
            LocalLightKind.Spot => RenderLightShape.SpotLight,
            LocalLightKind.Area => RenderLightShape.FlatLight,
            _ => RenderLightShape.PointLight,
        };

    private static RenderLightFalloffType ToRenderFalloff(LocalLightFalloffType falloff)
        => falloff switch
        {
            LocalLightFalloffType.Linear => RenderLightFalloffType.Linear,
            LocalLightFalloffType.Cubic => RenderLightFalloffType.Cubic,
            _ => RenderLightFalloffType.Quadratic,
        };

    private static string Format(Vector3 value)
        => $"{value.X:F2}, {value.Y:F2}, {value.Z:F2}";

    private enum NativeOperationKind
    {
        CreateOrApply,
        SoftDisablePointer,
        ReleaseToReusable,
    }

    private sealed class NativeOperation(
        LocalLightInstance? light,
        string lightId,
        string name,
        nint pointer,
        uint generation,
        NativeOperationKind kind,
        int delayFrames = 0)
    {
        public LocalLightInstance? Light { get; } = light;

        public string LightId { get; } = lightId;

        public string Name { get; } = name;

        public nint Pointer { get; } = pointer;

        public uint Generation { get; } = generation;

        public NativeOperationKind Kind { get; } = kind;

        public int DelayFrames { get; set; } = delayFrames;
    }
}
