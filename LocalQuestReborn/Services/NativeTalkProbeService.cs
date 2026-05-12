using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class NativeTalkProbeService
{
    private readonly ITargetManager targetManager;

    public NativeTalkProbeService(ITargetManager targetManager)
    {
        this.targetManager = targetManager;
    }

    private readonly List<string> events = [];

    public IReadOnlyList<string> Events => this.events;

    public string LastEvent => this.events.LastOrDefault() ?? "尚未记录原生 Talk 事件。";

    public void RecordConfirmProbe()
    {
        var snapshot = NativeNpcProbeService.CaptureObject(this.targetManager.Target, "Confirm/Interact 当前目标");
        var dataId = snapshot.Fields.GetValueOrDefault("DataId")?.Value ?? "未读取";
        var objectIndex = snapshot.Fields.GetValueOrDefault("ObjectIndex")?.Value ?? snapshot.Fields.GetValueOrDefault("ObjectTableIndex")?.Value ?? "未读取";
        var name = snapshot.Fields.GetValueOrDefault("Name")?.Value ?? "未读取";
        this.Add($"Confirm/Interact Probe: target={name}, objectIndex={objectIndex}, dataId={dataId}");
    }

    public void RecordAddon(string addonName, string details)
        => this.Add($"Addon 打开：{addonName}；{details}");

    private void Add(string text)
    {
        this.events.Add($"{DateTime.Now:HH:mm:ss} {text}");
        if (this.events.Count > 80)
            this.events.RemoveAt(0);
    }
}
