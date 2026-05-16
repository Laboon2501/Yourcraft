using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class EventNpcHostService
{
    private readonly ITargetManager targetManager;
    private readonly IClientState clientState;
    private readonly QuestDatabase database;

    public EventNpcHostService(ITargetManager targetManager, IClientState clientState, QuestDatabase database)
    {
        this.targetManager = targetManager;
        this.clientState = clientState;
        this.database = database;
    }

    public string MatchedHostNpcId { get; private set; } = string.Empty;

    public string LastHostMatchDebug { get; private set; } = "尚未匹配 Host。";

    public DateTime? LastHostInteractionAt { get; private set; }

    public string LastNativeAddonName { get; private set; } = "无";

    public bool LastHostIntercepted { get; private set; }

    public string LastHostInterceptSource { get; private set; } = "无";

    public string LastHostInterceptResult { get; private set; } = "尚未拦截 Host 交互。";

    public string LastNativeAddonCloseResult { get; private set; } = "尚未尝试关闭原生 addon。";

    public CustomNpc? TryGetHostByTarget()
    {
        this.MatchedHostNpcId = string.Empty;
        var target = this.targetManager.Target;
        if (target == null)
        {
            this.LastHostMatchDebug = "当前 Target 为空。";
            return null;
        }

        foreach (var npc in this.database.Npcs)
        {
            if (this.IsTargetMatchingHost(npc, target))
            {
                this.MatchedHostNpcId = npc.Id;
                this.LastHostMatchDebug = $"匹配 Host：{npc.Name} ({npc.Id})，模式={npc.InterceptMode}";
                return npc;
            }
        }

        this.LastHostMatchDebug = "当前 Target 未匹配任何 ExistingEventNpcHost。";
        return null;
    }

    public bool IsTargetMatchingHost(CustomNpc npc, object? target)
    {
        if (target == null)
            return false;

        if (npc.HostMode != CustomNpcHostMode.ExistingEventNpcHost && npc.NativeHostMode != NativeHostMode.ExistingEventNpcHost)
            return false;

        if (npc.HostTerritoryType != 0 && npc.HostTerritoryType != this.clientState.TerritoryType)
            return false;

        var objectKind = ReadMember(target, "ObjectKind");
        if (!objectKind.Contains("EventNpc", StringComparison.OrdinalIgnoreCase))
            return false;

        var dataId = ParseUInt(ReadMember(target, "DataId"));
        if (npc.HostDataId != 0 && dataId == npc.HostDataId)
            return true;

        var targetName = ReadMember(target, "Name");
        if (!string.IsNullOrWhiteSpace(npc.HostName) &&
            !string.IsNullOrWhiteSpace(targetName) &&
            string.Equals(npc.HostName, targetName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public bool IsCurrentTargetMatchingHost(CustomNpc npc)
        => this.IsTargetMatchingHost(npc, this.targetManager.Target);

    public bool SetCurrentTargetAsHost(CustomNpc npc, out string message)
    {
        var target = this.targetManager.Target;
        if (target == null)
        {
            message = "当前 Target 为空，无法设置 Host。";
            return false;
        }

        var objectKind = ReadMember(target, "ObjectKind");
        if (!objectKind.Contains("EventNpc", StringComparison.OrdinalIgnoreCase))
        {
            message = $"当前 Target 不是 EventNpc：ObjectKind={objectKind}";
            return false;
        }

        npc.HostMode = CustomNpcHostMode.ExistingEventNpcHost;
        npc.NativeHostMode = NativeHostMode.ExistingEventNpcHost;
        npc.HostDataId = ParseUInt(ReadMember(target, "DataId"));
        npc.HostObjectIndex = ReadMember(target, "ObjectIndex");
        if (string.IsNullOrWhiteSpace(npc.HostObjectIndex) || npc.HostObjectIndex == "未知")
            npc.HostObjectIndex = ReadMember(target, "ObjectTableIndex");
        npc.HostTerritoryType = (ushort)Math.Clamp((int)this.clientState.TerritoryType, 0, ushort.MaxValue);
        npc.HostName = ReadMember(target, "Name");
        npc.InterceptMode = HostInterceptMode.ManualCommand;
        npc.OverrideDialogueEnabled = true;
        npc.UseLocalDialogueOnInteract = true;
        this.database.Save();
        message = $"已设置 Host：{npc.HostName} DataId={npc.HostDataId} Territory={npc.HostTerritoryType}";
        return true;
    }

    public void ClearHost(CustomNpc npc)
    {
        npc.HostMode = CustomNpcHostMode.VirtualActor;
        npc.NativeHostMode = NativeHostMode.None;
        npc.HostDataId = 0;
        npc.HostObjectIndex = string.Empty;
        npc.HostTerritoryType = 0;
        npc.HostName = string.Empty;
        npc.OverrideNativeName = false;
        npc.InterceptNativeTalk = false;
        npc.UseLocalDialogueOnInteract = false;
        npc.InterceptMode = HostInterceptMode.ManualCommand;
        npc.OverrideDialogueEnabled = false;
        this.database.Save();
        this.LastHostMatchDebug = $"已清除 Host：{npc.Id}";
    }

    public bool TestHost(CustomNpc npc, out string message)
    {
        var matched = this.IsTargetMatchingHost(npc, this.targetManager.Target);
        message = matched
            ? $"当前 Target 匹配 Host：{npc.Id}"
            : $"当前 Target 不匹配 Host：{npc.Id}";
        this.LastHostMatchDebug = message;
        return matched;
    }

    public void MarkHostInteraction(CustomNpc npc, string source)
    {
        this.LastHostInteractionAt = DateTime.Now;
        this.MatchedHostNpcId = npc.Id;
        this.LastHostIntercepted = true;
        this.LastHostInterceptSource = source;
        this.LastHostInterceptResult = $"已打开本地对话：npcId={npc.Id}，来源={source}";
        this.LastHostMatchDebug = $"Host 交互：{npc.Id}，来源={source}";
    }

    public void RecordNativeAddon(string addonName, bool intercepted = false, string closeResult = "")
    {
        this.LastNativeAddonName = addonName;
        this.LastHostInteractionAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(closeResult))
            this.LastNativeAddonCloseResult = closeResult;
        if (intercepted)
        {
            this.LastHostIntercepted = true;
            this.LastHostInterceptSource = $"NativeTalkAddon:{addonName}";
            this.LastHostInterceptResult = $"已拦截原生 addon：{addonName}；{this.LastNativeAddonCloseResult}";
        }
    }

    private static uint ParseUInt(string raw)
        => uint.TryParse(raw, out var value) ? value : 0;

    private static string ReadMember(object source, string name)
    {
        try
        {
            var type = source.GetType();
            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)?.ToString()
                   ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)?.ToString()
                   ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }
}
