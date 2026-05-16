using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Yourcraft.Models;
using System.Text;

namespace Yourcraft.Services;

public sealed unsafe class BgPartCollisionSourceProbeService
{
    public string LastStatus { get; private set; } = "尚未执行 BgPart collision source 取证。";

    public string SelectedDump { get; private set; } = "尚未读取当前 BgPart。";

    public string ComparisonDump { get; private set; } = "尚未对比多个 BgPart。";

    public string TargetCandidateDump { get; private set; } = "尚未解析 target collision 候选。";

    public string PathTableDump { get; private set; } = "尚未读取 layout path table。";

    public void Probe(LayoutProbeInstance? selected, IEnumerable<LayoutProbeInstance> bgParts, string targetMdlPath)
    {
        if (selected == null)
        {
            this.LastStatus = "请先选择一个 BgPart。";
            return;
        }

        if (!TryGetBgPart(selected.Address, out var selectedBgPart, out var addressError))
        {
            this.LastStatus = addressError;
            return;
        }

        try
        {
            var layout = ((ILayoutInstance*)selectedBgPart)->Layout;
            var pathTable = ReadPathTable(layout);
            var selectedInfo = ReadCollisionInfo(selectedBgPart, selected);
            var target = BuildTargetCandidate(targetMdlPath, pathTable);
            var comparison = bgParts
                .Where(item => string.Equals(item.Type, "BgPart", StringComparison.Ordinal))
                .Take(120)
                .Select(item => TryGetBgPart(item.Address, out var bgPart, out _)
                    ? ReadCollisionInfo(bgPart, item)
                    : null)
                .Where(item => item != null)
                .Cast<CollisionInfo>()
                .Take(40)
                .ToList();

            this.SelectedDump = FormatSelected(selectedInfo);
            this.PathTableDump = FormatPathTable(pathTable);
            this.TargetCandidateDump = FormatTargetCandidate(target);
            this.ComparisonDump = FormatComparison(comparison);
            this.LastStatus = $"已完成只读取证：selected={selected.Address}，path table entries={pathTable.Count}，comparison rows={comparison.Count}。未调用 CreateSecondary/DestroySecondary，未写 CRC。";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"BgPart collision source 取证失败：{ex.Message}";
        }
    }

    private static CollisionInfo ReadCollisionInfo(BgPartsLayoutInstance* bgPart, LayoutProbeInstance source)
    {
        var layout = (ILayoutInstance*)bgPart;
        var primaryPath = ReadPrimaryPath(layout);
        var secondaryPath = ReadSecondaryPath(layout);
        var colliderType = bgPart->AnalyticShapeDataCrc != 0
            ? "Analytic"
            : bgPart->CollisionMeshPathCrc != 0
                ? "Mesh"
                : "None";

        return new CollisionInfo(
            source.Address,
            source.ResourcePath,
            primaryPath,
            secondaryPath,
            bgPart->CollisionMeshPathCrc,
            bgPart->AnalyticShapeDataCrc,
            colliderType,
            $"0x{(nint)bgPart->Collider:X}",
            $"0x{(nint)bgPart->CollisionUpdateListener:X}",
            bgPart->CollisionMaterialIdLow,
            bgPart->CollisionMaterialMaskLow,
            bgPart->CollisionMaterialIdHigh,
            bgPart->CollisionMaterialMaskHigh,
            source.DistanceToPlayer);
    }

    private static Dictionary<uint, string> ReadPathTable(LayoutManager* layout)
    {
        var result = new Dictionary<uint, string>();
        if (layout == null)
            return result;

        try
        {
            foreach (var (crc, pathPointer) in layout->CrcToPath)
            {
                if (pathPointer == null || pathPointer.Value == null)
                    continue;

                var path = ReadRefCountedString(pathPointer.Value);
                if (!string.IsNullOrWhiteSpace(path))
                    result[crc] = path;
            }
        }
        catch (Exception ex)
        {
            result[0] = $"读取 CrcToPath 失败：{ex.Message}";
        }

        return result;
    }

    private static TargetCollisionCandidate BuildTargetCandidate(string targetMdlPath, IReadOnlyDictionary<uint, string> pathTable)
    {
        targetMdlPath = (targetMdlPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetMdlPath))
        {
            return new TargetCollisionCandidate(
                string.Empty,
                string.Empty,
                false,
                0,
                false,
                0,
                "target mdl path 为空。");
        }

        var targetPcbPath = targetMdlPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            ? targetMdlPath[..^4] + ".pcb"
            : string.Empty;
        var targetMdl = FindPath(pathTable, targetMdlPath);
        var targetPcb = string.IsNullOrWhiteSpace(targetPcbPath) ? default : FindPath(pathTable, targetPcbPath);
        var note = string.IsNullOrWhiteSpace(targetPcbPath)
            ? "target path 不是 .mdl，无法推断同名 .pcb。"
            : targetPcb.Found
                ? "target 同名 .pcb 已存在于当前 LayoutManager.CrcToPath。"
                : "target 同名 .pcb 未在当前 LayoutManager.CrcToPath 中找到；不能直接调用 CreateSecondary 生成 target collider。";

        return new TargetCollisionCandidate(
            targetMdlPath,
            targetPcbPath,
            targetMdl.Found,
            targetMdl.Crc,
            targetPcb.Found,
            targetPcb.Crc,
            note);
    }

    private static (bool Found, uint Crc) FindPath(IReadOnlyDictionary<uint, string> pathTable, string path)
    {
        foreach (var (crc, value) in pathTable)
        {
            if (string.Equals(value, path, StringComparison.OrdinalIgnoreCase))
                return (true, crc);
        }

        return (false, 0);
    }

    private static string FormatSelected(CollisionInfo info)
        => string.Join(Environment.NewLine, new[]
        {
            $"address={info.Address}",
            $"PathMdl/runtime primary={info.PrimaryPath}",
            $"PathPcb/runtime secondary={info.SecondaryPath}",
            $"layout resourcePath={info.LayoutResourcePath}",
            $"CollisionMeshPathCrc=0x{info.CollisionMeshPathCrc:X8} ({info.CollisionMeshPathCrc})",
            $"AnalyticShapeDataCrc=0x{info.AnalyticShapeDataCrc:X8} ({info.AnalyticShapeDataCrc})",
            $"ColliderType(inferred)={info.ColliderType}",
            $"Collider pointer={info.ColliderPointer}",
            $"CollisionUpdateListener={info.CollisionUpdateListener}",
            $"MaterialIdLow/MaskLow=0x{info.MaterialIdLow:X8}/0x{info.MaterialMaskLow:X8}",
            $"MaterialIdHigh/MaskHigh=0x{info.MaterialIdHigh:X8}/0x{info.MaterialMaskHigh:X8}",
        });

    private static string FormatPathTable(IReadOnlyDictionary<uint, string> pathTable)
    {
        var pcbCount = pathTable.Values.Count(path => path.EndsWith(".pcb", StringComparison.OrdinalIgnoreCase));
        var mdlCount = pathTable.Values.Count(path => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        var preview = pathTable
            .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(pair => pair.Value.EndsWith(".pcb", StringComparison.OrdinalIgnoreCase) || pair.Value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .Take(80)
            .Select(pair => $"0x{pair.Key:X8} -> {pair.Value}");

        return $"entries={pathTable.Count}; mdl={mdlCount}; pcb={pcbCount}{Environment.NewLine}{string.Join(Environment.NewLine, preview)}";
    }

    private static string FormatTargetCandidate(TargetCollisionCandidate target)
        => string.Join(Environment.NewLine, new[]
        {
            $"targetMdlPath={target.TargetMdlPath}",
            $"targetPcbPath={target.TargetPcbPath}",
            $"target mdl in path table={target.TargetMdlInPathTable} crc=0x{target.TargetMdlCrc:X8}",
            $"target pcb in path table={target.TargetPcbInPathTable} crc=0x{target.TargetPcbCrc:X8}",
            $"note={target.Note}",
        });

    private static string FormatComparison(IReadOnlyList<CollisionInfo> infos)
    {
        if (infos.Count == 0)
            return "没有可对比的 BgPart。";

        var lines = new List<string>
        {
            "distance | colliderType | CollisionMeshPathCrc | AnalyticShapeDataCrc | mdl | pcb"
        };
        lines.AddRange(infos.Select(info =>
            $"{info.DistanceToPlayer:F1} | {info.ColliderType} | 0x{info.CollisionMeshPathCrc:X8} | 0x{info.AnalyticShapeDataCrc:X8} | {info.PrimaryPath} | {info.SecondaryPath}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReadPrimaryPath(ILayoutInstance* layout)
    {
        try
        {
            var pointer = layout->GetPrimaryPath();
            return pointer.HasValue ? pointer.ToString() : "primary path=null";
        }
        catch (Exception ex)
        {
            return $"primary path 读取失败：{ex.Message}";
        }
    }

    private static string ReadSecondaryPath(ILayoutInstance* layout)
    {
        try
        {
            var pointer = layout->GetSecondaryPath();
            return pointer.HasValue ? pointer.ToString() : "secondary path=null";
        }
        catch (Exception ex)
        {
            return $"secondary path 读取失败：{ex.Message}";
        }
    }

    private static string ReadRefCountedString(RefCountedString* value)
    {
        if (value == null)
            return string.Empty;

        var data = (byte*)value + 4;
        var length = 0;
        while (length < 260 && data[length] != 0)
            length++;

        return length <= 0 ? string.Empty : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data, length));
    }

    private static bool TryGetBgPart(string? rawAddress, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
        {
            error = $"BgPart 地址解析失败：{rawAddress}";
            return false;
        }

        bgPart = (BgPartsLayoutInstance*)address;
        if (((ILayoutInstance*)bgPart)->Id.Type != InstanceType.BgPart)
        {
            error = $"当前地址不是 BgPart：{((ILayoutInstance*)bgPart)->Id.Type}";
            bgPart = null;
            return false;
        }

        return true;
    }

    private static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
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

    private sealed record CollisionInfo(
        string Address,
        string LayoutResourcePath,
        string PrimaryPath,
        string SecondaryPath,
        uint CollisionMeshPathCrc,
        uint AnalyticShapeDataCrc,
        string ColliderType,
        string ColliderPointer,
        string CollisionUpdateListener,
        uint MaterialIdLow,
        uint MaterialMaskLow,
        uint MaterialIdHigh,
        uint MaterialMaskHigh,
        float DistanceToPlayer);

    private sealed record TargetCollisionCandidate(
        string TargetMdlPath,
        string TargetPcbPath,
        bool TargetMdlInPathTable,
        uint TargetMdlCrc,
        bool TargetPcbInPathTable,
        uint TargetPcbCrc,
        string Note);
}
