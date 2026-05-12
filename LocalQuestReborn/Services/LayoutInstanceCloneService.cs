using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed unsafe class LayoutInstanceCloneService
{
    public LayoutProbeInstance? SourceInstance { get; private set; }

    public string LastStatus { get; private set; } = "尚未执行复制实验。";

    public string SourceAddress { get; private set; } = "无";

    public string SourceKey { get; private set; } = "无";

    public string SourceLayerAddress { get; private set; } = "无";

    public string CloneAddress { get; private set; } = "未创建";

    public string CloneKey { get; private set; } = "未创建";

    public bool InsertedSuccessfully { get; private set; }

    public bool CloneVisible { get; private set; }

    public Vector3? ReadbackPosition { get; private set; }

    public string LastDump { get; private set; } = "尚无复制体 dump。";

    public void SelectSource(LayoutProbeInstance instance)
    {
        this.SourceInstance = instance;
        this.SourceAddress = instance.Address;
        this.SourceKey = instance.Key;
        this.SourceLayerAddress = instance.LayerAddress;
        this.LastStatus = $"已选择复制源：{instance.Type} {instance.ResourcePath}";
    }

    public void CloneSelectedTo(Vector3 playerPosition)
    {
        this.InsertedSuccessfully = false;
        this.CloneVisible = false;
        this.CloneAddress = "未创建";
        this.CloneKey = "未创建";
        this.ReadbackPosition = null;

        if (!this.TryGetSourcePointer(out var source, out var instance))
            return;

        if (!string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastStatus = $"当前只允许复制 BgPart。已选类型={instance.Type}";
            return;
        }

        var transform = source->GetTransformImpl();
        var size = 0;
        try
        {
            size = source->GetSizeOf();
        }
        catch
        {
        }

        var layer = source->Layer;
        var hasLayer = layer != null;
        var path = ReadPrimaryPath(source);
        this.SourceLayerAddress = $"0x{(nint)layer:X}";

        this.LastStatus =
            "复制未执行：当前 FFXIVClientStructs 只公开 ILayoutInstance.GetSizeOf/SetLayer/CreatePrimary 等实例方法，" +
            "但 LayerManager 没有公开 AddInstance/Insert/Allocate 接口。为了避免盲目 memcpy + 手写容器导致崩溃，本按钮只完成前置取证。" +
            $" 源类型={source->Id.Type}; size={size}; layer={(hasLayer ? this.SourceLayerAddress : "null")}; path={path}; target={FormatVector(playerPosition)}; " +
            $"源transform={(transform == null ? "null" : FormatVector(transform->Translation))}";
    }

    public void DeleteClone()
    {
        if (this.CloneAddress == "未创建")
        {
            this.LastStatus = "当前没有复制体可删除。";
            return;
        }

        this.LastStatus = "删除复制体未执行：尚未成功创建/插入复制体。";
    }

    public void MoveCloneTo(Vector3 playerPosition)
    {
        if (this.CloneAddress == "未创建")
        {
            this.LastStatus = "当前没有复制体可移动。";
            return;
        }

        this.LastStatus = $"移动复制体未执行：尚未成功创建/插入复制体。目标={FormatVector(playerPosition)}";
    }

    public void DumpClone()
    {
        this.LastDump = string.Join(Environment.NewLine, new[]
        {
            $"sourceKey={this.SourceKey}",
            $"sourceAddress={this.SourceAddress}",
            $"sourceLayer={this.SourceLayerAddress}",
            $"cloneKey={this.CloneKey}",
            $"cloneAddress={this.CloneAddress}",
            $"inserted={this.InsertedSuccessfully}",
            $"visible={this.CloneVisible}",
            $"readbackPosition={FormatNullableVector(this.ReadbackPosition)}",
            $"lastStatus={this.LastStatus}",
        });
    }

    private bool TryGetSourcePointer(out ILayoutInstance* pointer, out LayoutProbeInstance instance)
    {
        pointer = null;
        instance = this.SourceInstance!;
        if (this.SourceInstance == null)
        {
            this.LastStatus = "请先选中一个 BgPart layout instance。";
            return false;
        }

        instance = this.SourceInstance;
        if (!TryParseAddress(instance.Address, out var address) || address == 0)
        {
            this.LastStatus = $"源地址解析失败：{instance.Address}";
            return false;
        }

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static string ReadPrimaryPath(ILayoutInstance* instance)
    {
        try
        {
            var path = instance->GetPrimaryPath();
            return path.HasValue ? path.ToString() : "无 primary path";
        }
        catch (Exception ex)
        {
            return $"读取 primary path 失败：{ex.Message}";
        }
    }

    private static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
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

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private static string FormatNullableVector(Vector3? vector)
        => vector.HasValue ? FormatVector(vector.Value) : "未读取";
}
