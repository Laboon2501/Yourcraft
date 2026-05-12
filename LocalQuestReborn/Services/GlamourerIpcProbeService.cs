using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LocalQuestReborn.Services;

public sealed class GlamourerIpcProbeService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly List<GlamourerIpcProbeResult> results = [];

    public GlamourerIpcProbeService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.LastProbeMessage = "尚未手动探测 Glamourer/Penumbra IPC。";
    }

    public IReadOnlyList<GlamourerIpcProbeResult> Results => this.results;

    public string LastProbeMessage { get; private set; } = string.Empty;

    public string GlamourerVersion { get; private set; } = "未知";

    public string GlamourerVersionError { get; private set; } = string.Empty;

    public GlamourerIpcProbeResult? SelectedApplyDesign { get; private set; }

    public GlamourerIpcProbeResult? SelectedPenumbraRedraw { get; private set; }

    public string? SelectedApplyDesignIpc => this.SelectedApplyDesign?.Name;

    public string? SelectedPenumbraRedrawIpc => this.SelectedPenumbraRedraw?.Name;

    public void Probe()
    {
        this.results.Clear();
        this.SelectedApplyDesign = null;
        this.SelectedPenumbraRedraw = null;
        this.ProbeGlamourerVersion();

        foreach (var name in new[]
        {
            "Glamourer.Api.ApplyDesign",
            "Glamourer.ApplyDesign",
            "Glamourer.Design.Apply",
            "Glamourer.Api.Design.Apply",
            "Glamourer.State.ApplyDesign",
            "Glamourer.Designs.ApplyDesign",
        })
        {
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyGuidIntBool);
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyGuidUshortBool);
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyStringIntBool);
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyStringUshortBool);
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyGuidIntUintBool);
            this.ProbeApplyDesign(name, GlamourerIpcInvocationKind.ApplyStringIntUintBool);
        }

        foreach (var name in new[]
        {
            "Penumbra.Api.RedrawObject",
            "Penumbra.RedrawObject",
            "Penumbra.Api.Redraw",
            "Penumbra.Redraw",
        })
        {
            this.ProbeRedraw(name, GlamourerIpcInvocationKind.RedrawIntBool);
            this.ProbeRedraw(name, GlamourerIpcInvocationKind.RedrawUshortBool);
            this.ProbeRedraw(name, GlamourerIpcInvocationKind.RedrawIntObject);
            this.ProbeRedraw(name, GlamourerIpcInvocationKind.RedrawUshortObject);
        }

        this.SelectedApplyDesign = this.results.FirstOrDefault(result => result.IsRegistered && result.Kind == GlamourerIpcKind.ApplyDesign);
        this.SelectedPenumbraRedraw = this.results.FirstOrDefault(result => result.IsRegistered && result.Kind == GlamourerIpcKind.PenumbraRedraw);
        this.LastProbeMessage = $"Glamourer ApplyDesign：{Describe(this.SelectedApplyDesign)}；Penumbra Redraw：{Describe(this.SelectedPenumbraRedraw)}";
    }

    private void ProbeGlamourerVersion()
    {
        this.GlamourerVersion = "未知";
        this.GlamourerVersionError = string.Empty;

        foreach (var name in new[] { "Glamourer.ApiVersion", "Glamourer.Version" })
        {
            if (this.TryReadVersion<string>(name, value => value))
                return;

            if (this.TryReadVersion<int>(name, value => value.ToString()))
                return;

            if (this.TryReadVersion<(int Major, int Minor)>(name, value => $"{value.Major}.{value.Minor}"))
                return;
        }

        if (string.IsNullOrWhiteSpace(this.GlamourerVersionError))
            this.GlamourerVersionError = "未发现 Glamourer 版本 IPC。";
    }

    private bool TryReadVersion<T>(string name, Func<T, string> formatter)
    {
        try
        {
            var subscriber = this.pluginInterface.GetIpcSubscriber<T>(name);
            if (!subscriber.HasFunction)
                return false;

            this.GlamourerVersion = formatter(subscriber.InvokeFunc());
            return true;
        }
        catch (Exception ex)
        {
            this.GlamourerVersionError = $"{name}: {ex.Message}";
            return false;
        }
    }

    private void ProbeApplyDesign(string name, GlamourerIpcInvocationKind invocationKind)
    {
        var signature = DescribeSignature(invocationKind);
        try
        {
            var registered = invocationKind switch
            {
                GlamourerIpcInvocationKind.ApplyGuidIntBool => this.pluginInterface.GetIpcSubscriber<Guid, int, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.ApplyGuidUshortBool => this.pluginInterface.GetIpcSubscriber<Guid, ushort, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.ApplyStringIntBool => this.pluginInterface.GetIpcSubscriber<string, int, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.ApplyStringUshortBool => this.pluginInterface.GetIpcSubscriber<string, ushort, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.ApplyGuidIntUintBool => this.pluginInterface.GetIpcSubscriber<Guid, int, uint, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.ApplyStringIntUintBool => this.pluginInterface.GetIpcSubscriber<string, int, uint, bool>(name).HasFunction,
                _ => false,
            };
            this.results.Add(new GlamourerIpcProbeResult(name, signature, registered, string.Empty, GlamourerIpcKind.ApplyDesign, invocationKind));
        }
        catch (Exception ex)
        {
            this.results.Add(new GlamourerIpcProbeResult(name, signature, false, ex.Message, GlamourerIpcKind.ApplyDesign, invocationKind));
            this.log.Debug(ex, "Glamourer IPC probe failed. Name={Name}, Signature={Signature}", name, signature);
        }
    }

    private void ProbeRedraw(string name, GlamourerIpcInvocationKind invocationKind)
    {
        var signature = DescribeSignature(invocationKind);
        try
        {
            var registered = invocationKind switch
            {
                GlamourerIpcInvocationKind.RedrawIntBool => this.pluginInterface.GetIpcSubscriber<int, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.RedrawUshortBool => this.pluginInterface.GetIpcSubscriber<ushort, bool>(name).HasFunction,
                GlamourerIpcInvocationKind.RedrawIntObject => this.pluginInterface.GetIpcSubscriber<int, object>(name).HasFunction,
                GlamourerIpcInvocationKind.RedrawUshortObject => this.pluginInterface.GetIpcSubscriber<ushort, object>(name).HasFunction,
                _ => false,
            };
            this.results.Add(new GlamourerIpcProbeResult(name, signature, registered, string.Empty, GlamourerIpcKind.PenumbraRedraw, invocationKind));
        }
        catch (Exception ex)
        {
            this.results.Add(new GlamourerIpcProbeResult(name, signature, false, ex.Message, GlamourerIpcKind.PenumbraRedraw, invocationKind));
            this.log.Debug(ex, "Penumbra IPC probe failed. Name={Name}, Signature={Signature}", name, signature);
        }
    }

    private static string Describe(GlamourerIpcProbeResult? result)
        => result == null ? "未发现" : $"{result.Name} ({result.Signature})";

    private static string DescribeSignature(GlamourerIpcInvocationKind invocationKind)
        => invocationKind switch
        {
            GlamourerIpcInvocationKind.ApplyGuidIntBool => "Guid designId, int objectIndex -> bool",
            GlamourerIpcInvocationKind.ApplyGuidUshortBool => "Guid designId, ushort objectIndex -> bool",
            GlamourerIpcInvocationKind.ApplyStringIntBool => "string designId, int objectIndex -> bool",
            GlamourerIpcInvocationKind.ApplyStringUshortBool => "string designId, ushort objectIndex -> bool",
            GlamourerIpcInvocationKind.ApplyGuidIntUintBool => "Guid designId, int objectIndex, uint key -> bool",
            GlamourerIpcInvocationKind.ApplyStringIntUintBool => "string designId, int objectIndex, uint key -> bool",
            GlamourerIpcInvocationKind.RedrawIntBool => "int objectIndex -> bool",
            GlamourerIpcInvocationKind.RedrawUshortBool => "ushort objectIndex -> bool",
            GlamourerIpcInvocationKind.RedrawIntObject => "int objectIndex -> object",
            GlamourerIpcInvocationKind.RedrawUshortObject => "ushort objectIndex -> object",
            _ => "未知签名",
        };
}

public sealed record GlamourerIpcProbeResult(
    string Name,
    string Signature,
    bool IsRegistered,
    string ErrorMessage,
    GlamourerIpcKind Kind,
    GlamourerIpcInvocationKind InvocationKind);

public enum GlamourerIpcKind
{
    ApplyDesign,
    PenumbraRedraw,
}

public enum GlamourerIpcInvocationKind
{
    ApplyGuidIntBool,
    ApplyGuidUshortBool,
    ApplyStringIntBool,
    ApplyStringUshortBool,
    ApplyGuidIntUintBool,
    ApplyStringIntUintBool,
    RedrawIntBool,
    RedrawUshortBool,
    RedrawIntObject,
    RedrawUshortObject,
}
