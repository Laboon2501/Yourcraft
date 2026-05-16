using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class ActorValidityMonitorService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GposeExitDelay = TimeSpan.FromSeconds(2);

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private DateTime lastCheckAt = DateTime.MinValue;
    private bool wasGposing;
    private DateTime? gposeExitedAt;

    public ActorValidityMonitorService(IClientState clientState, IObjectTable objectTable)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
    }

    public string LastStatus { get; private set; } = "尚未检查。";
    public bool CurrentIsGposing { get; private set; }
    public bool PreviousFrameIsGposing { get; private set; }
    public DateTime? LastGposeExitedAt { get; private set; }
    public bool IsRebuildScheduled => this.gposeExitedAt != null;
    public TimeSpan GposeExitWaitRemaining
    {
        get
        {
            if (this.gposeExitedAt == null)
                return TimeSpan.Zero;

            var remaining = GposeExitDelay - (DateTime.UtcNow - this.gposeExitedAt.Value);
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public void Update(IEnumerable<RuntimeActorInstance> actors)
    {
        this.UpdateGposeState();
        var now = DateTime.UtcNow;
        if (now - this.lastCheckAt < CheckInterval)
            return;

        this.lastCheckAt = now;
        var checkedCount = 0;
        foreach (var actor in actors)
        {
            checkedCount++;
            if (IsPendingSpawnShell(actor))
                continue;

            if (!this.IsStillValid(actor, out var position, out var reason))
            {
                actor.IsValid = false;
                actor.LastError = reason;
            }
            else
            {
                actor.IsValid = true;
                actor.LastKnownPosition = position;
                if (actor.LastError.Contains("GPose", StringComparison.OrdinalIgnoreCase))
                    actor.LastError = string.Empty;
            }
        }

        this.LastStatus = $"已检查 {checkedCount} 个 Actor。";
    }

    private static bool IsPendingSpawnShell(RuntimeActorInstance actor)
    {
        if (actor.LifecycleState != ActorLifecycleState.Ready && actor.CharacterObject == null)
            return true;

        if (actor.CharacterObject != null || actor.IsReady)
            return false;

        if (string.Equals(actor.ObjectIndex, "Pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actor.Address, "Pending", StringComparison.OrdinalIgnoreCase))
            return true;

        var state = actor.PostSpawnPipelineState ?? string.Empty;
        return state is "Pending" or "PendingRestore" or "WaitingPrewarm" or "WaitingSceneStable" or "WaitingPostSpawn" or "Creating" or "WaitingBrioResult" or "BindingRuntime" or "ApplyingTransform";
    }

    public bool ConsumeGposeExitReady()
    {
        if (this.gposeExitedAt == null)
            return false;

        if (DateTime.UtcNow - this.gposeExitedAt.Value < GposeExitDelay)
            return false;

        this.gposeExitedAt = null;
        return true;
    }

    private void UpdateGposeState()
    {
        var isGposing = this.ReadIsGposing();
        this.PreviousFrameIsGposing = this.wasGposing;
        this.CurrentIsGposing = isGposing;
        if (this.wasGposing && !isGposing)
        {
            this.gposeExitedAt = DateTime.UtcNow;
            this.LastGposeExitedAt = this.gposeExitedAt;
            this.LastStatus = "检测到刚退出 GPose，已安排延迟重建。";
        }

        this.wasGposing = isGposing;
    }

    private bool IsStillValid(RuntimeActorInstance actor, out Vector3 position, out string reason)
    {
        position = actor.LastKnownPosition;
        reason = string.Empty;

        var actorAddress = NormalizeAddress(actor.Address);
        if (!string.IsNullOrWhiteSpace(actorAddress))
        {
            foreach (var obj in this.objectTable)
            {
                if (obj == null)
                    continue;

                var address = NormalizeAddress(ReadStringMember(obj, "Address"));
                if (string.IsNullOrWhiteSpace(address) || !string.Equals(actorAddress, address, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ReadVector3Member(obj, "Position", out position))
                    return true;

                reason = "Actor invalid: matched by address, but Position is unavailable.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(actor.ObjectIndex) || !int.TryParse(actor.ObjectIndex, out var objectIndex))
        {
            reason = "Actor 已失效，ObjectIndex 不可用。";
            return false;
        }

        if (objectIndex == 0 && !string.IsNullOrWhiteSpace(actorAddress))
        {
            reason = "Actor validity kept by spawned address; objectIndex=0 is not used as a LocalPlayer fallback.";
            return true;
        }

        var found = false;
        foreach (var obj in this.objectTable)
        {
            if (obj == null)
                continue;

            var index = ReadIntMember(obj, "ObjectIndex", "ObjectTableIndex", "Index");
            if (index != objectIndex)
                continue;

            found = true;
            var address = ReadStringMember(obj, "Address");
            if (!string.IsNullOrWhiteSpace(actorAddress) && !string.IsNullOrWhiteSpace(address) &&
                !string.Equals(actorAddress, NormalizeAddress(address), StringComparison.OrdinalIgnoreCase))
            {
                reason = "Actor 已失效，可能由 GPose 切换清理：Address 不再匹配。";
                return false;
            }

            if (ReadVector3Member(obj, "Position", out position))
                return true;

            reason = "Actor 已失效，Position 不可读。";
            return false;
        }

        if (!found)
        {
            reason = "Actor 已失效，可能由 GPose 切换清理：ObjectTable 中找不到 ObjectIndex。";
            return false;
        }

        return true;
    }

    private bool ReadIsGposing()
    {
        try
        {
            var property = this.clientState.GetType().GetProperty("IsGPosing", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(this.clientState) is bool value)
                return value;
        }
        catch
        {
        }

        return false;
    }

    private static int? ReadIntMember(object source, params string[] names)
    {
        foreach (var name in names)
        {
            var raw = ReadStringMember(source, name);
            if (int.TryParse(raw, out var value))
                return value;
        }

        return null;
    }

    private static string ReadStringMember(object source, string name)
    {
        try
        {
            var type = source.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var propertyValue = property?.GetValue(source)?.ToString();
            if (!string.IsNullOrWhiteSpace(propertyValue))
                return propertyValue;

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(source)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ReadVector3Member(object source, string name, out Vector3 value)
    {
        value = Vector3.Zero;
        try
        {
            var raw = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
            if (raw is Vector3 vector)
            {
                value = vector;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string NormalizeAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("不可", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var normalized = raw.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('0');
        return normalized.Any(Uri.IsHexDigit) ? normalized : string.Empty;
    }
}
