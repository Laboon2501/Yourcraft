using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed unsafe class BgPartCollisionSourceResolver
{
    public BgPartCollisionSourceResolveResult Resolve(string targetMdlPath, IEnumerable<LayoutProbeInstance> bgParts)
    {
        targetMdlPath = (targetMdlPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetMdlPath))
            return BgPartCollisionSourceResolveResult.NotFound("target mdl path 为空。");

        var candidates = bgParts
            .Where(item => string.Equals(item.Type, "BgPart", StringComparison.Ordinal))
            .Where(item => string.Equals(item.ResourcePath, targetMdlPath, StringComparison.OrdinalIgnoreCase))
            .Select(ReadCandidate)
            .Where(item => item != null)
            .Cast<BgPartCollisionSourceResolveResult>()
            .OrderByDescending(item => item.CollisionType != "None")
            .ThenBy(item => item.DistanceToPlayer)
            .ToList();

        if (candidates.Count == 0)
            return BgPartCollisionSourceResolveResult.NotFound($"未在当前 BgPart Slot Pool 中找到 resourcePath == target mdl 的实例：{targetMdlPath}");

        var selected = candidates[0];
        selected.Message = selected.CollisionType == "None"
            ? $"找到 target mdl 对应 BgPart，但 source 无 collision：{selected.SourceSlotAddress}"
            : $"找到 target mdl 对应 collision source：{selected.SourceSlotAddress}，type={selected.CollisionType}";
        return selected;
    }

    private static BgPartCollisionSourceResolveResult? ReadCandidate(LayoutProbeInstance instance)
    {
        if (!TryGetBgPart(instance.Address, out var bgPart))
            return null;

        var mesh = bgPart->CollisionMeshPathCrc;
        var analytic = bgPart->AnalyticShapeDataCrc;
        var type = analytic != 0 ? "Analytic" : mesh != 0 ? "Mesh" : "None";
        var secondary = ReadSecondaryPath((ILayoutInstance*)bgPart);
        return new BgPartCollisionSourceResolveResult
        {
            Found = true,
            SourceInstance = instance,
            SourceSlotAddress = instance.Address,
            SourceResourcePath = instance.ResourcePath,
            CollisionType = type,
            CollisionMeshPathCrc = mesh,
            AnalyticShapeDataCrc = analytic,
            MaterialIdLow = bgPart->CollisionMaterialIdLow,
            MaterialMaskLow = bgPart->CollisionMaterialMaskLow,
            MaterialIdHigh = bgPart->CollisionMaterialIdHigh,
            MaterialMaskHigh = bgPart->CollisionMaterialMaskHigh,
            ColliderAddress = $"0x{(nint)bgPart->Collider:X}",
            SecondaryPath = secondary,
            DistanceToPlayer = instance.DistanceToPlayer,
        };
    }

    private static string ReadSecondaryPath(ILayoutInstance* layout)
    {
        try
        {
            var path = layout->GetSecondaryPath();
            return path.HasValue ? path.ToString() : "secondary path=null";
        }
        catch (Exception ex)
        {
            return $"secondary path 读取失败：{ex.Message}";
        }
    }

    private static bool TryGetBgPart(string? rawAddress, out BgPartsLayoutInstance* bgPart)
    {
        bgPart = null;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
            return false;

        bgPart = (BgPartsLayoutInstance*)address;
        return ((ILayoutInstance*)bgPart)->Id.Type == InstanceType.BgPart;
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
}

public sealed class BgPartCollisionSourceResolveResult
{
    public bool Found { get; set; }

    public LayoutProbeInstance? SourceInstance { get; set; }

    public string SourceSlotAddress { get; set; } = string.Empty;

    public string SourceResourcePath { get; set; } = string.Empty;

    public string CollisionType { get; set; } = "None";

    public uint CollisionMeshPathCrc { get; set; }

    public uint AnalyticShapeDataCrc { get; set; }

    public uint MaterialIdLow { get; set; }

    public uint MaterialMaskLow { get; set; }

    public uint MaterialIdHigh { get; set; }

    public uint MaterialMaskHigh { get; set; }

    public string ColliderAddress { get; set; } = string.Empty;

    public string SecondaryPath { get; set; } = string.Empty;

    public float DistanceToPlayer { get; set; }

    public string Message { get; set; } = string.Empty;

    public static BgPartCollisionSourceResolveResult NotFound(string message)
        => new()
        {
            Found = false,
            CollisionType = "None",
            Message = message,
        };
}
