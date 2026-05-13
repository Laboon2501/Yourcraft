using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed class ActorLookAtService
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    private readonly IObjectTable objectTable;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public ActorLookAtService(IObjectTable objectTable, BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public void Update(IEnumerable<RuntimeActorInstance> actors, QuestDatabase database)
    {
        var player = this.objectTable.LocalPlayer;
        if (player == null)
            return;

        foreach (var actor in actors)
        {
            var lookAtRadius = actor.LookAtRadius > 0.1f ? actor.LookAtRadius : 8f;
            if (!actor.LookAtPlayerEnabled || actor.LookAtMode == NpcLookAtMode.None || !actor.IsValid)
            {
                actor.IsLookingAtPlayer = false;
                continue;
            }

            var now = DateTime.UtcNow;
            if (now - actor.LastLookAtUpdateAt < UpdateInterval)
                continue;

            actor.LastLookAtUpdateAt = now;
            var distance = XzDistance(actor.LastKnownPosition, player.Position);
            if (distance > lookAtRadius)
            {
                actor.IsLookingAtPlayer = false;
                continue;
            }

            var direction = player.Position - actor.LastKnownPosition;
            var yaw = MathF.Atan2(direction.X, direction.Z);
            var success = actor.LookAtMode == NpcLookAtMode.NativeLookAt || actor.LookAtMode == NpcLookAtMode.HeadOnly
                ? this.TrySetNativeLookAt(actor, player, out var reason)
                : this.TrySetBodyYaw(actor, yaw, out reason);

            if (success)
            {
                actor.IsLookingAtPlayer = true;
                actor.LastLookAtError = string.Empty;
            }
            else
            {
                actor.IsLookingAtPlayer = false;
                actor.LastLookAtError = reason;
            }
        }
    }

    private bool TrySetNativeLookAt(RuntimeActorInstance actor, object player, out string reason)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false，native 注视目标写入已禁用。";
            return false;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            reason = $"无法读取 actor Address：{actor.Address}";
            return false;
        }

        if (!TryReadUIntMember(player, out var targetEntityId, "EntityId", "GameObjectId", "ObjectId"))
        {
            reason = "无法读取玩家 EntityId，已回退失败。";
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                character->TargetId.ObjectId = targetEntityId;
                character->TargetId.Type = 0;
            }

            reason = $"已设置 NativeLookAt 目标：{targetEntityId}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"设置 NativeLookAt 失败：{ex.Message}";
            this.log.Warning(ex, "Failed to set native look-at target. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    private bool TrySetBodyYaw(RuntimeActorInstance actor, float yaw, out string reason)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            reason = "UnsafeMode=false，native 旋转写入已禁用。";
            return false;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            reason = $"无法读取 actor Address：{actor.Address}";
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                character->GameObject.SetRotation(yaw);
            }

            reason = $"已设置 BodyYaw={yaw:F3}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"设置 BodyYaw 失败：{ex.Message}";
            this.log.Warning(ex, "Failed to rotate actor toward player. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    private static float XzDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static bool TryReadAddress(RuntimeActorInstance actor, out nint address)
    {
        address = 0;
        var raw = actor.Address?.Trim() ?? string.Empty;
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

    private static bool TryReadUIntMember(object source, out uint value, params string[] names)
    {
        value = 0;
        foreach (var name in names)
        {
            try
            {
                var type = source.GetType();
                var raw = type.GetProperty(name)?.GetValue(source) ?? type.GetField(name)?.GetValue(source);
                switch (raw)
                {
                    case uint u:
                        value = u;
                        return true;
                    case ulong ul when ul <= uint.MaxValue:
                        value = (uint)ul;
                        return true;
                    case int i when i >= 0:
                        value = (uint)i;
                        return true;
                    case string text when uint.TryParse(text, out var parsed):
                        value = parsed;
                        return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }
}
