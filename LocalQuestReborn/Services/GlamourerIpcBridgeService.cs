using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LocalQuestReborn.Services;

public sealed class GlamourerIpcBridgeService
{
    private const string ApplyDesignIpcName = "Glamourer.ApplyDesign";
    private const uint DefaultKey = 0u;
    private const uint EquipmentAndCustomizationFlags = 6u;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly List<GlamourerIpcBindingDetail> details = [];

    public GlamourerIpcBridgeService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public IReadOnlyList<GlamourerIpcBindingDetail> Details => this.details;

    public GlamourerIpcBindingDetail? SelectedApplyDesign { get; private set; }

    public string LastMessage { get; private set; } = "尚未调用 Glamourer IPC。";

    public string LastError { get; private set; } = string.Empty;

    public string LastInvocationParameters { get; private set; } = string.Empty;

    public string LastReturnCode { get; private set; } = string.Empty;

    public bool IsApplyDesignRegistered => this.details.Any(detail => detail.IsRegistered);

    public bool IsTwoParameterApplyDesignBindable => false;

    public bool IsFourParameterApplyDesignBindable => this.details.Any(detail => detail.IsRegistered);

    public void Probe()
    {
        this.details.Clear();
        this.SelectedApplyDesign = null;

        this.ProbeApplyDesign(GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntObject);
        this.ProbeApplyDesign(GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntInt);
        this.ProbeApplyDesign(GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntUInt);

        this.SelectedApplyDesign = this.details.FirstOrDefault(detail => detail.IsRegistered);
        this.LastMessage = this.SelectedApplyDesign == null
            ? "未发现新版 Glamourer.ApplyDesign(Guid,int,uint,uint) IPC。"
            : $"已绑定 Glamourer.ApplyDesign：{this.SelectedApplyDesign.Signature}";
    }

    public bool ApplyDesignToObject(string designId, int objectIndex, out string reason)
    {
        if (string.IsNullOrWhiteSpace(designId))
        {
            reason = "Glamourer designId 为空。";
            this.LastError = reason;
            return false;
        }

        if (!Guid.TryParse(designId, out var guid))
        {
            reason = $"Glamourer designId 不是有效 GUID：{designId}";
            this.LastError = reason;
            return false;
        }

        var candidates = this.details.Where(detail => detail.IsRegistered).ToList();
        if (candidates.Count == 0)
        {
            reason = "未发现新版 Glamourer.ApplyDesign(Guid,int,uint,uint) IPC。请先点击“探测 Glamourer IPC”。";
            this.LastError = reason;
            this.LastMessage = reason;
            return false;
        }

        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            if (this.TryInvokeApplyDesign(candidate, guid, objectIndex, out reason))
            {
                this.SelectedApplyDesign = candidate;
                this.LastError = string.Empty;
                this.LastMessage = reason;
                return true;
            }

            errors.Add(reason);
        }

        reason = string.Join(" | ", errors);
        this.LastError = reason;
        this.LastMessage = $"Glamourer ApplyDesign 调用失败：{reason}";
        return false;
    }

    private void ProbeApplyDesign(GlamourerBridgeInvocationKind invocationKind)
    {
        var detail = GlamourerIpcBindingDetail.Create(ApplyDesignIpcName, invocationKind);
        try
        {
            detail.IsRegistered = invocationKind switch
            {
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntObject => this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, object>(ApplyDesignIpcName).HasFunction,
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntInt => this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, int>(ApplyDesignIpcName).HasFunction,
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntUInt => this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, uint>(ApplyDesignIpcName).HasFunction,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            detail.IsRegistered = false;
            detail.BindingError = ex.Message;
        }

        this.details.Add(detail);
    }

    private bool TryInvokeApplyDesign(GlamourerIpcBindingDetail detail, Guid designId, int objectIndex, out string reason)
    {
        reason = string.Empty;
        this.LastInvocationParameters = $"designId={designId}, objectIndex={objectIndex}, key={DefaultKey}, flags={EquipmentAndCustomizationFlags}";
        this.LastReturnCode = string.Empty;

        try
        {
            object? result = detail.InvocationKind switch
            {
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntObject =>
                    this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, object>(detail.Name).InvokeFunc(designId, objectIndex, DefaultKey, EquipmentAndCustomizationFlags),
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntInt =>
                    this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, int>(detail.Name).InvokeFunc(designId, objectIndex, DefaultKey, EquipmentAndCustomizationFlags),
                GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntUInt =>
                    this.pluginInterface.GetIpcSubscriber<Guid, int, uint, uint, uint>(detail.Name).InvokeFunc(designId, objectIndex, DefaultKey, EquipmentAndCustomizationFlags),
                _ => null,
            };

            var code = ConvertReturnCode(result);
            this.LastReturnCode = code?.ToString() ?? result?.ToString() ?? "null";
            if (code == 0)
            {
                reason = $"Glamourer ApplyDesign 成功。签名=Guid,int,uint,uint -> {detail.ReturnType}，key=0，flags=6，返回码={this.LastReturnCode}，参数={this.LastInvocationParameters}";
                return true;
            }

            reason = $"Glamourer ApplyDesign 返回非 0。签名=Guid,int,uint,uint -> {detail.ReturnType}，key=0，flags=6，返回码={this.LastReturnCode}，参数={this.LastInvocationParameters}";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"Glamourer ApplyDesign 调用失败。签名=Guid,int,uint,uint -> {detail.ReturnType}，key=0，flags=6，参数={this.LastInvocationParameters}，异常={ex.Message}";
            detail.LastError = ex.Message;
            this.log.Warning(ex, "Glamourer ApplyDesign invocation failed. Signature={Signature}", detail.Signature);
            return false;
        }
    }

    private static long? ConvertReturnCode(object? result)
    {
        if (result == null)
            return 0;

        try
        {
            if (result.GetType().IsEnum)
                return Convert.ToInt64(result);

            return result switch
            {
                int value => value,
                uint value => value,
                long value => value,
                ulong value when value <= long.MaxValue => (long)value,
                short value => value,
                ushort value => value,
                byte value => value,
                sbyte value => value,
                bool value => value ? 0 : 1,
                _ => long.TryParse(result.ToString(), out var parsed) ? parsed : null,
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GlamourerIpcBindingDetail
{
    public string Name { get; init; } = string.Empty;
    public string ReturnType { get; init; } = string.Empty;
    public int ParameterCount { get; init; }
    public string ParameterTypes { get; init; } = "Guid, int, uint, uint";
    public string ParameterMeaning { get; init; } = "designId, objectIndex, key, flags";
    public string Signature { get; init; } = string.Empty;
    public bool IsRegistered { get; set; }
    public string BindingError { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public GlamourerBridgeIpcKind Kind { get; init; } = GlamourerBridgeIpcKind.ApplyDesign;
    public GlamourerBridgeInvocationKind InvocationKind { get; init; }

    public static GlamourerIpcBindingDetail Create(string name, GlamourerBridgeInvocationKind invocationKind)
    {
        var returnType = invocationKind switch
        {
            GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntObject => "object",
            GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntInt => "int",
            GlamourerBridgeInvocationKind.ApplyGuidIntUIntUIntUInt => "uint",
            _ => "unknown",
        };

        return new GlamourerIpcBindingDetail
        {
            Name = name,
            ReturnType = returnType,
            ParameterCount = 4,
            Signature = $"Guid, int, uint, uint -> {returnType}",
            InvocationKind = invocationKind,
        };
    }
}

public enum GlamourerBridgeIpcKind
{
    ApplyDesign,
}

public enum GlamourerBridgeInvocationKind
{
    ApplyGuidIntUIntUIntObject,
    ApplyGuidIntUIntUIntInt,
    ApplyGuidIntUIntUIntUInt,
}
