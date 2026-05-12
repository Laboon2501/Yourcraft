using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeGameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;

namespace LocalQuestReborn.Services;

public sealed unsafe class PlayerLookAtActorService
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);
    private const float MaxLookAtDistance = 45f;

    private readonly IObjectTable objectTable;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;
    private DateTime lastUpdateAt = DateTime.MinValue;

    public PlayerLookAtActorService(IObjectTable objectTable, BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public bool Enabled { get; set; }

    public bool IsLookingAtSelectedActor { get; private set; }

    public string LastError { get; private set; } = "尚未执行玩家头部注视。";

    public string LastResult { get; private set; } = "尚未执行。";

    public string CurrentTargetRuntimeId { get; private set; } = string.Empty;

    public void Update(string selectedRuntimeId, RuntimeActorInstance? actor, bool isGposing)
    {
        if (!this.Enabled)
        {
            if (this.IsLookingAtSelectedActor)
                this.Stop();
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedRuntimeId) || actor == null)
        {
            if (this.IsLookingAtSelectedActor)
                this.Stop();
            return;
        }

        if (isGposing)
        {
            this.StopWithReason("当前处于 GPose，已停止玩家头部注视。");
            return;
        }

        if (!actor.IsValid)
        {
            this.StopWithReason("选中 Actor 已失效，已停止玩家头部注视。");
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player == null)
        {
            this.StopWithReason("LocalPlayer 不可用。");
            return;
        }

        if (XzDistance(player.Position, actor.LastKnownPosition) > MaxLookAtDistance)
        {
            this.StopWithReason($"距离超过 {MaxLookAtDistance:F0}m，已停止玩家头部注视。");
            return;
        }

        var now = DateTime.UtcNow;
        if (now - this.lastUpdateAt < UpdateInterval)
            return;

        this.lastUpdateAt = now;
        this.LookAt(selectedRuntimeId, actor);
    }

    public bool LookAt(string selectedRuntimeId, RuntimeActorInstance actor)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            this.SetFailure("UnsafeMode=false，玩家 head-only native 注视写入已禁用。");
            return false;
        }

        var player = this.objectTable.LocalPlayer;
        if (player == null)
        {
            this.SetFailure("LocalPlayer 不可用。");
            return false;
        }

        if (!TryGetAddress(player, out var playerAddress) || playerAddress == 0)
        {
            this.SetFailure("无法读取 LocalPlayer.Address。");
            return false;
        }

        if (!TryParseAddress(actor.Address, out var actorAddress) || actorAddress == 0)
        {
            this.SetFailure($"无法读取 Actor Address：{actor.Address}");
            return false;
        }

        try
        {
            var actorObject = (NativeGameObject*)actorAddress;
            var targetId = actorObject->GetGameObjectId();
            if (targetId.ObjectId == 0)
            {
                this.SetFailure("Actor GameObjectId 为 0，无法设置注视目标。");
                return false;
            }

            var playerCharacter = (NativeCharacter*)playerAddress;
            playerCharacter->SetSoftTargetId(targetId);

            this.CurrentTargetRuntimeId = selectedRuntimeId;
            this.IsLookingAtSelectedActor = true;
            this.LastError = string.Empty;
            this.LastResult = $"玩家 head-only 注视已设置：runtimeId={ShortId(selectedRuntimeId)}, targetGameObjectId={targetId}, actorPos=X {actor.LastKnownPosition.X:F2}, Y {actor.LastKnownPosition.Y:F2}, Z {actor.LastKnownPosition.Z:F2}";
            return true;
        }
        catch (Exception ex)
        {
            this.SetFailure($"设置玩家头部注视失败：{ex.Message}");
            this.log.Warning(ex, "Failed to set player head look-at. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    public void Stop()
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            this.IsLookingAtSelectedActor = false;
            this.LastError = "UnsafeMode=false，无法写入停止 head look；已清理插件状态。";
            this.LastResult = this.LastError;
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player == null || !TryGetAddress(player, out var playerAddress) || playerAddress == 0)
        {
            this.IsLookingAtSelectedActor = false;
            this.LastError = "停止玩家注视失败：LocalPlayer Address 不可用。";
            this.LastResult = this.LastError;
            return;
        }

        try
        {
            var playerCharacter = (NativeCharacter*)playerAddress;
            playerCharacter->SetSoftTargetId(default(NativeGameObjectId));
            this.IsLookingAtSelectedActor = false;
            this.CurrentTargetRuntimeId = string.Empty;
            this.LastError = string.Empty;
            this.LastResult = "已停止玩家头部注视，并清空 soft target。";
        }
        catch (Exception ex)
        {
            this.IsLookingAtSelectedActor = false;
            this.LastError = $"停止玩家注视失败：{ex.Message}";
            this.LastResult = this.LastError;
            this.log.Warning(ex, "Failed to stop player head look-at.");
        }
    }

    private void StopWithReason(string reason)
    {
        if (this.IsLookingAtSelectedActor)
            this.Stop();

        this.IsLookingAtSelectedActor = false;
        this.LastError = reason;
        this.LastResult = reason;
    }

    private void SetFailure(string reason)
    {
        this.IsLookingAtSelectedActor = false;
        this.LastError = reason;
        this.LastResult = reason;
    }

    private static float XzDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static bool TryGetAddress(object source, out nint address)
    {
        address = 0;
        try
        {
            var raw = source.GetType().GetProperty("Address", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)
                      ?? source.GetType().GetField("Address", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
            return raw switch
            {
                nint native => (address = native) != 0,
                ulong value => (address = unchecked((nint)value)) != 0,
                long value => (address = (nint)value) != 0,
                string text => TryParseAddress(text, out address),
                _ => TryParseAddress(raw?.ToString(), out address),
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            address = unchecked((nint)hex);
            return address != 0;
        }

        if (!ulong.TryParse(text, out var value))
            return false;

        address = unchecked((nint)value);
        return address != 0;
    }

    private static string ShortId(string runtimeId)
        => runtimeId.Length <= 8 ? runtimeId : runtimeId[..8];
}
