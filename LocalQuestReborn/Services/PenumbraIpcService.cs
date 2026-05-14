using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class PenumbraIpcService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<object?> initializedEvent;
    private readonly ICallGateSubscriber<object?> disposedEvent;
    private readonly Action initializedHandler;
    private readonly Action disposedHandler;
    private DateTime nextRetryAt = DateTime.MinValue;
    private bool wasAvailable;
    private bool reapplyRequested;
    private bool disposed;

    public PenumbraIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.initializedEvent = pluginInterface.GetIpcSubscriber<object?>("Penumbra.Initialized");
        this.disposedEvent = pluginInterface.GetIpcSubscriber<object?>("Penumbra.Disposed");
        this.initializedHandler = this.HandlePenumbraInitialized;
        this.disposedHandler = this.HandlePenumbraDisposed;
        this.LastStatus = "Penumbra IPC has not connected yet.";
        this.SubscribeEvents();
        this.TryConnectOrRefresh("startup");
    }

    public bool IsAvailable { get; private set; }

    public bool IsEnabled { get; private set; }

    public string LastStatus { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public string ApiVersionText { get; private set; } = "unknown";

    public DateTime LastRefreshTime { get; private set; }

    public IReadOnlyList<PenumbraCollectionInfo> Collections { get; private set; } = [];

    public void Update()
    {
        if (this.disposed)
            return;

        if (this.IsAvailable && this.IsEnabled)
            return;

        if (DateTime.UtcNow < this.nextRetryAt)
            return;

        this.TryConnectOrRefresh("retry");
    }

    public bool ConsumeReapplyRequested()
    {
        if (!this.reapplyRequested)
            return false;

        this.reapplyRequested = false;
        return true;
    }

    public void TryConnectOrRefresh(string reason)
    {
        if (this.disposed)
            return;

        this.log.Debug("Penumbra IPC init attempt. Reason={Reason}", reason);
        this.nextRetryAt = DateTime.UtcNow + RetryInterval;

        try
        {
            if (!this.TryReadApiVersion(out var version))
            {
                this.MarkUnavailable("Penumbra ApiVersion IPC unavailable.");
                return;
            }

            this.ApiVersionText = version;
            this.IsEnabled = this.TryReadEnabledState(out var enabled) ? enabled : true;
            if (!this.IsEnabled)
            {
                this.MarkUnavailable($"Penumbra IPC connected but Penumbra reports disabled. Api={version}");
                return;
            }

            if (!this.RefreshCollections())
                return;

            this.IsAvailable = true;
            this.LastError = string.Empty;
            this.LastStatus = $"Penumbra IPC available. Api={version}, collections={this.Collections.Count}";
            this.LastRefreshTime = DateTime.UtcNow;
            if (!this.wasAvailable)
                this.reapplyRequested = true;
            this.wasAvailable = true;
            this.log.Information("Penumbra IPC available. ApiVersion={ApiVersion}, CollectionCount={Count}", version, this.Collections.Count);
        }
        catch (Exception ex)
        {
            this.MarkUnavailable($"Penumbra IPC unavailable: {ex.Message}");
            this.log.Debug(ex, "Penumbra IPC init failed.");
        }
    }

    public bool RefreshCollections()
    {
        if (this.disposed)
            return false;

        try
        {
            foreach (var name in new[] { "Penumbra.GetCollections.V5", "Penumbra.GetCollections" })
            {
                try
                {
                    var subscriber = this.pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>(name);
                    if (!subscriber.HasFunction)
                        continue;

                    this.Collections = subscriber.InvokeFunc()
                        .Select(item => new PenumbraCollectionInfo(item.Key, item.Value))
                        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    this.LastRefreshTime = DateTime.UtcNow;
                    this.LastStatus = $"Collections refreshed: {this.Collections.Count}";
                    this.reapplyRequested = true;
                    return true;
                }
                catch (Exception ex)
                {
                    this.LastError = $"{name}: {ex.Message}";
                }
            }

            this.MarkUnavailable($"GetCollections IPC unavailable. {this.LastError}");
            return false;
        }
        catch (Exception ex)
        {
            this.MarkUnavailable($"GetCollections failed: {ex.Message}");
            this.log.Debug(ex, "Penumbra GetCollections failed.");
            return false;
        }
    }

    public bool ApplyCollection(CustomNpc npc, RuntimeActorInstance actor, out string reason)
    {
        actor.PenumbraMode = npc.PenumbraMode;
        actor.PenumbraCollectionId = npc.PenumbraCollectionId;
        actor.PenumbraCollectionNameCache = npc.PenumbraCollectionNameCache;

        if (npc.PenumbraMode == PenumbraCollectionMode.DoNotTouch)
        {
            reason = "Penumbra mode DoNotTouch; skipped.";
            actor.LastPenumbraCollectionResult = reason;
            actor.LastPenumbraCollectionError = string.Empty;
            return true;
        }

        if (!this.IsAvailable || !this.IsEnabled)
            return this.Fail(actor, $"Penumbra unavailable: {this.LastError}", out reason);

        if (!TryReadObjectIndex(actor, out var objectIndex))
            return this.Fail(actor, $"Invalid actor object index: {actor.ObjectIndex}", out reason);

        var target = npc.PenumbraMode == PenumbraCollectionMode.UseCollection
            ? npc.PenumbraCollectionId
            : null;

        if (npc.PenumbraMode == PenumbraCollectionMode.UseCollection && target == null)
            return this.Fail(actor, "UseCollection selected but PenumbraCollectionId is empty.", out reason);

        try
        {
            var setResult = target.HasValue
                ? this.TrySetCollection(objectIndex, target.Value, out var setReason)
                : this.TryClearCollection(objectIndex, out setReason);

            if (!setResult)
                return this.Fail(actor, setReason, out reason);

            var redraw = this.TryRedrawObject(objectIndex, out var redrawReason);
            actor.WeAppliedPenumbraCollection = true;
            actor.LastAppliedPenumbraGameObjectIndex = objectIndex;
            actor.LastAppliedPenumbraCollectionId = target;
            actor.LastPenumbraCollectionError = string.Empty;
            actor.LastPenumbraCollectionResult = $"Penumbra collection applied. mode={npc.PenumbraMode}, index={objectIndex}, collection={target?.ToString() ?? "inherit"}, redraw={redraw}: {redrawReason}";
            reason = actor.LastPenumbraCollectionResult;
            this.log.Information("Apply Penumbra collection actor={Actor}, index={Index}, mode={Mode}, collection={Collection}, redraw={Redraw}", actor.RuntimeId, objectIndex, npc.PenumbraMode, target?.ToString() ?? "inherit", redraw);
            return true;
        }
        catch (Exception ex)
        {
            this.MarkUnavailable($"SetCollectionForObject failed: {ex.Message}");
            this.log.Warning(ex, "Apply Penumbra collection failed. Actor={Actor}, Index={Index}", actor.RuntimeId, objectIndex);
            return this.Fail(actor, ex.Message, out reason);
        }
    }

    public bool RequestRedrawObject(RuntimeActorInstance actor, out string reason)
    {
        if (this.disposed)
        {
            reason = "Penumbra IPC service disposed.";
            return false;
        }

        if (!this.IsAvailable || !this.IsEnabled)
        {
            reason = $"Penumbra unavailable: {this.LastError}";
            return false;
        }

        if (!TryReadObjectIndex(actor, out var objectIndex))
        {
            reason = $"Invalid actor object index: {actor.ObjectIndex}";
            return false;
        }

        try
        {
            var success = this.TryRedrawObject(objectIndex, out var redrawReason);
            reason = $"Targeted redraw actor={actor.RuntimeId}, index={objectIndex}, success={success}: {redrawReason}";
            this.log.Information("Penumbra targeted redraw actor={Actor}, index={Index}, success={Success}, reason={Reason}", actor.RuntimeId, objectIndex, success, redrawReason);
            return success;
        }
        catch (Exception ex)
        {
            reason = $"Targeted Penumbra redraw failed: {ex.Message}";
            this.log.Warning(ex, "Targeted Penumbra redraw failed. Actor={Actor}, Index={Index}", actor.RuntimeId, objectIndex);
            return false;
        }
    }

    public void CleanupActorAssignment(RuntimeActorInstance actor)
    {
        if (!actor.WeAppliedPenumbraCollection)
            return;

        var index = actor.LastAppliedPenumbraGameObjectIndex;
        actor.WeAppliedPenumbraCollection = false;
        actor.LastAppliedPenumbraGameObjectIndex = -1;
        actor.LastAppliedPenumbraCollectionId = null;

        if (!this.IsAvailable || index < 0)
            return;

        try
        {
            if (this.TryClearCollection(index, out var reason))
                this.TryRedrawObject(index, out _);

            actor.LastPenumbraCollectionResult = $"Cleaned Penumbra assignment for old index={index}: {reason}";
            this.log.Information("Cleanup Penumbra assignment actor={Actor}, oldIndex={Index}: {Reason}", actor.RuntimeId, index, reason);
        }
        catch (Exception ex)
        {
            actor.LastPenumbraCollectionError = ex.Message;
            this.log.Debug(ex, "Cleanup Penumbra assignment failed. Actor={Actor}, Index={Index}", actor.RuntimeId, index);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        try
        {
            this.initializedEvent.Unsubscribe(this.initializedHandler);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Failed to unsubscribe Penumbra.Initialized event.");
        }

        try
        {
            this.disposedEvent.Unsubscribe(this.disposedHandler);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Failed to unsubscribe Penumbra.Disposed event.");
        }
    }

    private void SubscribeEvents()
    {
        try
        {
            this.initializedEvent.Subscribe(this.initializedHandler);
            this.disposedEvent.Subscribe(this.disposedHandler);
            this.log.Debug("Penumbra Initialized/Disposed IPC event subscribers registered.");
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Penumbra event subscriber registration failed; falling back to throttled retry.");
        }
    }

    private void HandlePenumbraInitialized()
    {
        if (this.disposed)
            return;

        this.log.Information("Penumbra Initialized event received.");
        this.TryConnectOrRefresh("Penumbra.Initialized event");
        this.reapplyRequested = true;
    }

    private void HandlePenumbraDisposed()
    {
        if (this.disposed)
            return;

        this.log.Information("Penumbra Disposed event received.");
        this.Collections = [];
        this.MarkUnavailable("Penumbra Disposed event received.");
    }

    private bool TryReadApiVersion(out string version)
    {
        version = "unknown";
        foreach (var name in new[] { "Penumbra.ApiVersion", "Penumbra.Api.ApiVersion" })
        {
            try
            {
                var tupleSubscriber = this.pluginInterface.GetIpcSubscriber<(int Major, int Minor)>(name);
                if (tupleSubscriber.HasFunction)
                {
                    var value = tupleSubscriber.InvokeFunc();
                    version = $"{value.Major}.{value.Minor}";
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var intSubscriber = this.pluginInterface.GetIpcSubscriber<int>(name);
                if (intSubscriber.HasFunction)
                {
                    version = intSubscriber.InvokeFunc().ToString();
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private bool TryReadEnabledState(out bool enabled)
    {
        enabled = true;
        foreach (var name in new[] { "Penumbra.GetEnabledState.V5", "Penumbra.GetEnabledState" })
        {
            try
            {
                var subscriber = this.pluginInterface.GetIpcSubscriber<bool>(name);
                if (!subscriber.HasFunction)
                    continue;

                enabled = subscriber.InvokeFunc();
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private bool TrySetCollection(int objectIndex, Guid collectionId, out string reason)
    {
        foreach (var name in new[] { "Penumbra.SetCollectionForObject.V5", "Penumbra.SetCollectionForObject" })
        {
            if (this.TryInvokeSetGuidTuple(name, objectIndex, collectionId, out reason) ||
                this.TryInvokeSetGuidObject(name, objectIndex, collectionId, out reason) ||
                this.TryInvokeSetGuidBool(name, objectIndex, collectionId, out reason))
            {
                return true;
            }
        }

        reason = "No compatible SetCollectionForObject IPC signature found.";
        return false;
    }

    private bool TryClearCollection(int objectIndex, out string reason)
    {
        foreach (var name in new[] { "Penumbra.SetCollectionForObject.V5", "Penumbra.SetCollectionForObject" })
        {
            if (this.TryInvokeSetNullableObject(name, objectIndex, null, out reason) ||
                this.TryInvokeSetGuidTuple(name, objectIndex, Guid.Empty, out reason) ||
                this.TryInvokeSetGuidObject(name, objectIndex, Guid.Empty, out reason) ||
                this.TryInvokeSetGuidBool(name, objectIndex, Guid.Empty, out reason))
            {
                return true;
            }
        }

        reason = "No compatible SetCollectionForObject clear signature found.";
        return false;
    }

    private bool TryInvokeSetGuidTuple(string name, int objectIndex, Guid collectionId, out string reason)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<int, Guid, bool, bool, (int Code, Guid? OldCollection)>(name);
            if (!subscriber.HasFunction)
            {
                reason = $"{name}: no function";
                return false;
            }

            var result = subscriber.InvokeFunc(objectIndex, collectionId, true, true);
            reason = $"{name}: code={result.Code}, old={result.OldCollection}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{name}: {ex.Message}";
            return false;
        }
    }

    private bool TryInvokeSetGuidObject(string name, int objectIndex, Guid collectionId, out string reason)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<int, Guid, bool, bool, object>(name);
            if (!subscriber.HasFunction)
            {
                reason = $"{name}: no function";
                return false;
            }

            var result = subscriber.InvokeFunc(objectIndex, collectionId, true, true);
            reason = $"{name}: result={result ?? "null"}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{name}: {ex.Message}";
            return false;
        }
    }

    private bool TryInvokeSetGuidBool(string name, int objectIndex, Guid collectionId, out string reason)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<int, Guid, bool, bool, bool>(name);
            if (!subscriber.HasFunction)
            {
                reason = $"{name}: no function";
                return false;
            }

            var result = subscriber.InvokeFunc(objectIndex, collectionId, true, true);
            reason = $"{name}: result={result}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{name}: {ex.Message}";
            return false;
        }
    }

    private bool TryInvokeSetNullableObject(string name, int objectIndex, Guid? collectionId, out string reason)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, object>(name);
            if (!subscriber.HasFunction)
            {
                reason = $"{name}: no function";
                return false;
            }

            var result = subscriber.InvokeFunc(objectIndex, collectionId, true, true);
            reason = $"{name}: result={result ?? "null"}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{name}: {ex.Message}";
            return false;
        }
    }

    private bool TryRedrawObject(int objectIndex, out string reason)
    {
        foreach (var name in new[] { "Penumbra.RedrawObject.V5", "Penumbra.Api.RedrawObject", "Penumbra.RedrawObject" })
        {
            try
            {
                var subscriber = this.pluginInterface.GetIpcSubscriber<int, int, object>(name);
                if (subscriber.HasFunction)
                {
                    var result = subscriber.InvokeFunc(objectIndex, 0);
                    reason = $"{name}: {result ?? "null"}";
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason = $"{name}: {ex.Message}";
            }

            try
            {
                var subscriber = this.pluginInterface.GetIpcSubscriber<int, bool>(name);
                if (subscriber.HasFunction)
                {
                    var result = subscriber.InvokeFunc(objectIndex);
                    reason = $"{name}: {result}";
                    return result;
                }
            }
            catch (Exception ex)
            {
                reason = $"{name}: {ex.Message}";
            }
        }

        reason = "No compatible RedrawObject IPC signature found.";
        return false;
    }

    private void MarkUnavailable(string reason)
    {
        this.IsAvailable = false;
        this.IsEnabled = false;
        this.LastError = reason;
        this.LastStatus = reason;
        if (this.wasAvailable)
            this.log.Warning("Penumbra IPC unavailable: {Reason}", reason);
        this.wasAvailable = false;
    }

    private bool Fail(RuntimeActorInstance actor, string message, out string reason)
    {
        reason = message;
        actor.LastPenumbraCollectionError = message;
        actor.LastPenumbraCollectionResult = message;
        return false;
    }

    private static bool TryReadObjectIndex(RuntimeActorInstance actor, out int objectIndex)
    {
        if (int.TryParse(actor.ObjectIndex, out objectIndex) && objectIndex >= 0)
            return true;

        objectIndex = -1;
        return false;
    }
}
