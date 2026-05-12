using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;
using System.Text;

namespace LocalQuestReborn.Services;

public sealed class ActorNameplateService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public ActorNameplateService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public bool TrySetActorName(RuntimeActorInstance actor, string name)
    {
        actor.DesiredDisplayName = name;
        if (actor.CharacterObject == null)
        {
            actor.NameSetResult = "失败：characterObject 为空。";
            return false;
        }

        if (TrySetStringMember(actor.CharacterObject, name, out var memberResult))
        {
            actor.NameSetResult = memberResult;
            actor.NativeNameReadback = this.TryReadActorName(actor);
            actor.NativeNameSet = true;
            return true;
        }

        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            actor.NameSetResult = $"失败：未找到公开 Name setter，且 UnsafeMode=false。{memberResult}";
            actor.NativeNameSet = false;
            return false;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            actor.NameSetResult = $"失败：无法读取 actor Address：{actor.Address}";
            actor.NativeNameSet = false;
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
                var bytes = Encoding.UTF8.GetBytes(name.Length > 20 ? name[..20] : name);
                var count = Math.Min(bytes.Length, 63);
                for (var i = 0; i < count; i++)
                    character->GameObject.Name[i] = bytes[i];
                character->GameObject.Name[count] = 0;
            }

            actor.NativeNameSet = true;
            actor.NameSetResult = "成功：已通过 native GameObject.Name 写入。";
            actor.NativeNameReadback = this.TryReadActorName(actor);
            return true;
        }
        catch (Exception ex)
        {
            actor.NativeNameSet = false;
            actor.NameSetResult = $"失败：native 名称写入异常：{ex.Message}";
            this.log.Warning(ex, "Failed to set actor native name. RuntimeId={RuntimeId}", actor.RuntimeId);
            return false;
        }
    }

    public string TryReadActorName(RuntimeActorInstance actor)
    {
        var value = actor.CharacterObject == null ? string.Empty : ReadStringMember(actor.CharacterObject, "Name", "ObjectName", "NamePlate");
        actor.NativeNameReadback = string.IsNullOrWhiteSpace(value) ? "不可用" : value;
        actor.CurrentNativeName = actor.NativeNameReadback;
        return actor.NativeNameReadback;
    }

    public bool TryRefreshNameplate(RuntimeActorInstance actor)
    {
        actor.NameSetResult = "NamePlateGui.RequestRedraw 尚未接入；已刷新本地 readback。";
        this.TryReadActorName(actor);
        return false;
    }

    private static bool TrySetStringMember(object source, string value, out string result)
    {
        foreach (var name in new[] { "Name", "ObjectName", "NamePlate" })
        {
            try
            {
                var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property is { CanWrite: true } && property.PropertyType == typeof(string))
                {
                    property.SetValue(source, value);
                    result = $"成功：已通过 {source.GetType().Name}.{name} setter 写入。";
                    return true;
                }
            }
            catch (Exception ex)
            {
                result = $"{name} setter 失败：{ex.Message}";
                return false;
            }
        }

        result = "未找到可写 Name/ObjectName/NamePlate 字符串属性。";
        return false;
    }

    private static string ReadStringMember(object source, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var type = source.GetType();
                var value = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)
                    ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
                var text = value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch
            {
            }
        }

        return string.Empty;
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
}
