using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace LocalQuestReborn.Services;

public sealed unsafe class StandaloneRenderListProbeService
{
    private const int MaxLayoutScanCount = 20000;
    private const double MaxLayoutScanMs = 80;
    private const int MaxChildScanCount = 2048;
    private const double MaxChildScanMs = 60;

    public string LastStatus { get; private set; } = "尚未执行 Standalone render/list 只读取证。";

    public string LastDump { get; private set; } = "尚未 dump Standalone render/list 对比。";

    public void Compare(StandaloneObjectInstance? standalone, LayoutProbeInstance? realBgPart)
    {
        if (standalone == null)
        {
            this.LastStatus = "请先选择一个 Standalone 对象。";
            return;
        }

        if (!TryParseAddress(standalone.ObjectAddress, out var standaloneAddress) || standaloneAddress == 0)
        {
            this.LastStatus = $"Standalone 地址无效：{standalone.ObjectAddress}";
            return;
        }

        try
        {
            var standaloneObject = (SceneObject*)standaloneAddress;
            nint realLayoutAddress = 0;
            nint realGraphicsAddress = 0;

            if (realBgPart != null && TryParseAddress(realBgPart.Address, out var parsedRealLayout) && parsedRealLayout != 0)
            {
                realLayoutAddress = parsedRealLayout;
                var realLayout = (ILayoutInstance*)parsedRealLayout;
                if (realLayout->Id.Type == InstanceType.BgPart)
                    realGraphicsAddress = (nint)((BgPartsLayoutInstance*)realLayout)->GraphicsObject;
            }

            var layoutScan = ScanLayoutBgParts(standaloneAddress, realLayoutAddress, realGraphicsAddress);
            var instancesByTypeScan = ScanInstancesByType(standaloneAddress, realLayoutAddress, realGraphicsAddress);
            var standaloneParentScan = ScanParentChildList(standaloneObject->ParentObject, standaloneObject);
            var realParentScan = realGraphicsAddress == 0
                ? ListScanResult.Empty("NoRealBgPart")
                : ScanParentChildList(((SceneObject*)realGraphicsAddress)->ParentObject, (SceneObject*)realGraphicsAddress);
            var standaloneRootScan = ScanParentChildList(GetRootObject(standaloneObject), standaloneObject);
            var realRootScan = realGraphicsAddress == 0
                ? ListScanResult.Empty("NoRealBgPart")
                : ScanParentChildList(GetRootObject((SceneObject*)realGraphicsAddress), (SceneObject*)realGraphicsAddress);

            this.LastDump = string.Join(Environment.NewLine, new[]
            {
                "Standalone render / active list read-only probe",
                $"standalone=0x{standaloneAddress:X}; realLayout={(realLayoutAddress == 0 ? "none" : $"0x{realLayoutAddress:X}")}; realGraphics={(realGraphicsAddress == 0 ? "none" : $"0x{realGraphicsAddress:X}")}",
                string.Empty,
                "[LayoutWorld BgPart instance scan]",
                layoutScan.ToMultiline(),
                string.Empty,
                "[LayoutManager.InstancesByType BgPart scan]",
                instancesByTypeScan.ToMultiline(),
                string.Empty,
                "[Parent child ring]",
                $"real parent contains real: {realParentScan.ToSummary()}",
                $"standalone parent contains standalone: {standaloneParentScan.ToSummary()}",
                string.Empty,
                "[Root child ring]",
                $"real root scan: {realRootScan.ToSummary()}",
                $"standalone root scan: {standaloneRootScan.ToSummary()}",
                string.Empty,
                "[Standalone fields]",
                BuildObjectStateDump(standaloneAddress, "Standalone"),
                string.Empty,
                "[Real BgPart fields]",
                realGraphicsAddress == 0 ? "没有可对比的真实 BgPart GraphicsObject。" : BuildObjectStateDump(realGraphicsAddress, realBgPart?.ResourcePath ?? "Real BgPart"),
                string.Empty,
                "[SetActive related bytes]",
                realLayoutAddress == 0 ? "real layout: none" : BuildLayoutActiveStateDump(realLayoutAddress, realGraphicsAddress, "Real BgPart"),
                BuildLayoutActiveStateDump(0, standaloneAddress, "Standalone"),
                string.Empty,
                "[Interpretation]",
                layoutScan.RealGraphicsFound
                    ? "- 真实 BgPart 的 GraphicsObject 能被 LayoutWorld/Layer/BgPart 实例扫描命中。"
                    : "- 真实 BgPart 未被本次 LayoutWorld/Layer/BgPart 扫描命中；可能 UI 选择不是 BgPart，或扫描被限时截断。",
                layoutScan.StandaloneGraphicsFound
                    ? "- Standalone 也出现在 LayoutWorld/Layer/BgPart 扫描中：这不符合当前预期，需要继续追 owner。"
                    : "- Standalone 没有出现在 LayoutWorld/Layer/BgPart 扫描中，说明它缺少 LayoutInstance owner / active instance 链。",
                instancesByTypeScan.StandaloneGraphicsFound
                    ? "- Standalone 出现在 LayoutManager.InstancesByType 中：需要确认是否被某个 LayoutInstance 误持有。"
                    : "- Standalone 未出现在 LayoutManager.InstancesByType 中，进一步支持它没有真实 LayoutInstance owner。",
                realParentScan.Hit && !standaloneParentScan.Hit
                    ? "- parent child ring 差异成立：真实 BgPart 在 parent child list 中，Standalone 不在。"
                    : "- parent child ring 差异不完整；需要检查选择对象和 scan 截断状态。",
                "- SetActive 相关字节用于确认 active 状态：LayoutInstance +0x2B bit4 是 active flag，GraphicsObject +0x88 bit0 会被 SetActive 同步，+0xD7 bit0 会被 0x140456100 置位并入队。",
                "- 当前还没有定位到独立的 culling/render submit list 指针；下一步应继续从 SetActive 函数体和 UpdateCulling callsite 追 manager/list。",
            });

            this.LastStatus = $"已完成 Standalone render/list 只读取证：layerScan={layoutScan.Scanned}; byTypeScan={instancesByTypeScan.Scanned}; realHit={layoutScan.RealGraphicsFound || instancesByTypeScan.RealGraphicsFound}; standaloneHit={layoutScan.StandaloneGraphicsFound || instancesByTypeScan.StandaloneGraphicsFound}; truncated={layoutScan.Truncated || instancesByTypeScan.Truncated}; elapsed={(layoutScan.ElapsedMs + instancesByTypeScan.ElapsedMs):F2}ms";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Standalone render/list 只读取证失败：{ex.Message}";
            this.LastDump = ex.ToString();
        }
    }

    private static LayoutScanResult ScanLayoutBgParts(nint standaloneGraphics, nint realLayoutAddress, nint realGraphicsAddress)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LayoutScanResult();

        try
        {
            var world = LayoutWorld.Instance();
            if (world == null)
                return result with { EndReason = "LayoutWorldNull", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };

            foreach (var (_, layoutPtr) in world->LoadedLayouts)
            {
                ScanLayout(layoutPtr.Value, standaloneGraphics, realLayoutAddress, realGraphicsAddress, stopwatch, ref result);
                if (result.Truncated)
                    return result;
            }

            ScanLayout(world->GlobalLayout, standaloneGraphics, realLayoutAddress, realGraphicsAddress, stopwatch, ref result);
            return result with { EndReason = result.EndReason.Length == 0 ? "Completed" : result.EndReason, ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            return result with { EndReason = $"Exception:{ex.Message}", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
        }
    }

    private static LayoutScanResult ScanInstancesByType(nint standaloneGraphics, nint realLayoutAddress, nint realGraphicsAddress)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LayoutScanResult();

        try
        {
            var world = LayoutWorld.Instance();
            if (world == null)
                return result with { EndReason = "LayoutWorldNull", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };

            foreach (var (_, layoutPtr) in world->LoadedLayouts)
            {
                ScanInstancesByTypeLayout(layoutPtr.Value, standaloneGraphics, realLayoutAddress, realGraphicsAddress, stopwatch, ref result);
                if (result.Truncated)
                    return result;
            }

            ScanInstancesByTypeLayout(world->GlobalLayout, standaloneGraphics, realLayoutAddress, realGraphicsAddress, stopwatch, ref result);
            return result with { EndReason = result.EndReason.Length == 0 ? "Completed" : result.EndReason, ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            return result with { EndReason = $"Exception:{ex.Message}", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
        }
    }

    private static void ScanInstancesByTypeLayout(LayoutManager* layout, nint standaloneGraphics, nint realLayoutAddress, nint realGraphicsAddress, Stopwatch stopwatch, ref LayoutScanResult result)
    {
        if (layout == null)
            return;

        foreach (var (instanceType, mapPtr) in layout->InstancesByType)
        {
            if (instanceType != InstanceType.BgPart || mapPtr == null || mapPtr.Value == null)
                continue;

            foreach (var (_, instancePtr) in *mapPtr.Value)
            {
                if (instancePtr == null || instancePtr.Value == null)
                    continue;

                if (result.Scanned >= MaxLayoutScanCount)
                {
                    result = result with { Truncated = true, EndReason = "CountLimit", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
                    return;
                }

                if (stopwatch.Elapsed.TotalMilliseconds > MaxLayoutScanMs)
                {
                    result = result with { Truncated = true, EndReason = "TimeLimit", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
                    return;
                }

                var instance = instancePtr.Value;
                var layoutAddress = (nint)instance;
                var graphicsAddress = instance->Id.Type == InstanceType.BgPart
                    ? (nint)((BgPartsLayoutInstance*)instance)->GraphicsObject
                    : 0;
                result = result with { Scanned = result.Scanned + 1 };

                if (!result.RealLayoutFound && realLayoutAddress != 0 && layoutAddress == realLayoutAddress)
                    result = result with { RealLayoutFound = true, RealLayoutHitIndex = result.Scanned - 1 };
                if (!result.RealGraphicsFound && realGraphicsAddress != 0 && graphicsAddress == realGraphicsAddress)
                    result = result with { RealGraphicsFound = true, RealGraphicsHitIndex = result.Scanned - 1 };
                if (!result.StandaloneGraphicsFound && graphicsAddress == standaloneGraphics)
                    result = result with { StandaloneGraphicsFound = true, StandaloneGraphicsHitIndex = result.Scanned - 1 };
            }
        }
    }

    private static void ScanLayout(LayoutManager* layout, nint standaloneGraphics, nint realLayoutAddress, nint realGraphicsAddress, Stopwatch stopwatch, ref LayoutScanResult result)
    {
        if (layout == null)
            return;

        foreach (var (_, layerPtr) in layout->Layers)
        {
            if (layerPtr == null || layerPtr.Value == null)
                continue;

            foreach (var (_, instancePtr) in layerPtr.Value->Instances)
            {
                if (instancePtr == null || instancePtr.Value == null)
                    continue;

                if (result.Scanned >= MaxLayoutScanCount)
                {
                    result = result with { Truncated = true, EndReason = "CountLimit", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
                    return;
                }

                if (stopwatch.Elapsed.TotalMilliseconds > MaxLayoutScanMs)
                {
                    result = result with { Truncated = true, EndReason = "TimeLimit", ElapsedMs = stopwatch.Elapsed.TotalMilliseconds };
                    return;
                }

                var instance = instancePtr.Value;
                if (instance->Id.Type != InstanceType.BgPart)
                    continue;

                var layoutAddress = (nint)instance;
                var graphicsAddress = (nint)((BgPartsLayoutInstance*)instance)->GraphicsObject;
                result = result with { Scanned = result.Scanned + 1 };

                if (!result.RealLayoutFound && realLayoutAddress != 0 && layoutAddress == realLayoutAddress)
                    result = result with { RealLayoutFound = true, RealLayoutHitIndex = result.Scanned - 1 };
                if (!result.RealGraphicsFound && realGraphicsAddress != 0 && graphicsAddress == realGraphicsAddress)
                    result = result with { RealGraphicsFound = true, RealGraphicsHitIndex = result.Scanned - 1 };
                if (!result.StandaloneGraphicsFound && graphicsAddress == standaloneGraphics)
                    result = result with { StandaloneGraphicsFound = true, StandaloneGraphicsHitIndex = result.Scanned - 1 };
            }
        }
    }

    private static ListScanResult ScanParentChildList(SceneObject* parent, SceneObject* target)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parent == null)
            return ListScanResult.Empty("ParentNull");
        if (target == null)
            return ListScanResult.Empty("TargetNull");

        try
        {
            var first = parent->ChildObject;
            if (first == null)
                return ListScanResult.Empty("EmptyChildList");

            var visited = new HashSet<nint>();
            var current = first;
            for (var i = 0; current != null; i++)
            {
                if (i >= MaxChildScanCount)
                    return new ListScanResult(false, -1, i, true, "CountLimit", stopwatch.Elapsed.TotalMilliseconds);
                if (stopwatch.Elapsed.TotalMilliseconds > MaxChildScanMs)
                    return new ListScanResult(false, -1, i, true, "TimeLimit", stopwatch.Elapsed.TotalMilliseconds);

                var currentAddress = (nint)current;
                if (!visited.Add(currentAddress))
                    return new ListScanResult(false, -1, i, false, $"Cycle:0x{currentAddress:X}", stopwatch.Elapsed.TotalMilliseconds);

                if (current == target)
                    return new ListScanResult(true, i, i + 1, false, "Hit", stopwatch.Elapsed.TotalMilliseconds);

                var next = current->NextSiblingObject;
                if (next == null)
                    return new ListScanResult(false, -1, i + 1, false, "EndedByNull", stopwatch.Elapsed.TotalMilliseconds);
                if (next == first)
                    return new ListScanResult(false, -1, i + 1, false, "ReturnedToStart", stopwatch.Elapsed.TotalMilliseconds);

                current = next;
            }

            return new ListScanResult(false, -1, visited.Count, false, "EndedByNull", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new ListScanResult(false, -1, 0, false, $"Exception:{ex.Message}", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static SceneObject* GetRootObject(SceneObject* obj)
    {
        var current = obj;
        for (var i = 0; i < 32 && current != null; i++)
        {
            var parent = current->ParentObject;
            if (parent == null)
                return current;
            current = parent;
        }

        return current;
    }

    private static string BuildObjectStateDump(nint address, string label)
    {
        try
        {
            var obj = (SceneObject*)address;
            var bg = (SceneBgObject*)address;
            FFXIVClientStructs.FFXIV.Common.Math.SphereBounds bounds = default;
            var boundsText = "not computed";

            if (bg->ModelResourceHandle != null && bg->ModelResourceHandle->LoadState >= 7)
            {
                bg->ComputeSphereBounds(&bounds);
                boundsText = $"center=({bounds.CenterPoint.X:F2}, {bounds.CenterPoint.Y:F2}, {bounds.CenterPoint.Z:F2}); radius={bounds.Radius:F2}";
            }

            return string.Join(Environment.NewLine, new[]
            {
                $"{label}: object=0x{address:X}",
                $"parent=0x{(nint)obj->ParentObject:X}; child=0x{(nint)obj->ChildObject:X}; prev=0x{(nint)obj->PreviousSiblingObject:X}; next=0x{(nint)obj->NextSiblingObject:X}",
                $"objectFlags=0x{obj->ObjectFlags:X16}; high=0x{(obj->ObjectFlags & ~0xFUL):X16}; low=0x{obj->ObjectFlags & 0xF:X}",
                $"drawFlags=0x{bg->Flags:X2}; outlineFlags=0x{bg->OutlineFlags:X2}; visible={bg->IsVisible}; transformChanged={bg->IsTransformChanged}",
                $"modelHandle={(bg->ModelResourceHandle == null ? "0x0" : $"0x{(nint)bg->ModelResourceHandle:X}")}; loadState={(bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->LoadState.ToString())}; path={SafeReadPath(bg)}",
                $"position=({obj->Position.X:F2}, {obj->Position.Y:F2}, {obj->Position.Z:F2}); scale=({obj->Scale.X:F2}, {obj->Scale.Y:F2}, {obj->Scale.Z:F2})",
                $"cachedMatrices={ReadPointer(address, 0xA0)}; stain={ReadPointer(address, 0xA8)}; cachedTransform={ReadPointer(address, 0xB0)}; animationData={ReadPointer(address, 0xB8)}",
                $"bounds={boundsText}",
            });
        }
        catch (Exception ex)
        {
            return $"{label}: read failed: {ex.Message}";
        }
    }

    private static string BuildLayoutActiveStateDump(nint layoutAddress, nint graphicsAddress, string label)
    {
        try
        {
            var lines = new List<string> { $"{label}:" };
            if (layoutAddress != 0)
            {
                lines.Add($"  layout=0x{layoutAddress:X}");
                lines.Add($"  layout+0x2A=0x{ReadByte(layoutAddress, 0x2A):X2}; layout+0x2B=0x{ReadByte(layoutAddress, 0x2B):X2}");
                lines.Add($"  layout active bit(+0x2B bit4)={((ReadByte(layoutAddress, 0x2B) & 0x10) != 0)}; wantActive bit(+0x2B bit5)={((ReadByte(layoutAddress, 0x2B) & 0x20) != 0)}");
                lines.Add($"  layout graphics ptr(+0x30)=0x{ReadNint(layoutAddress, 0x30):X}; collider ptr(+0x38)=0x{ReadNint(layoutAddress, 0x38):X}");
            }
            else
            {
                lines.Add("  layout=none; Standalone has no BgPartsLayoutInstance owner.");
            }

            if (graphicsAddress != 0)
            {
                lines.Add($"  graphics=0x{graphicsAddress:X}");
                lines.Add($"  graphics+0x88=0x{ReadByte(graphicsAddress, 0x88):X2}; +0x89=0x{ReadByte(graphicsAddress, 0x89):X2}; +0xD6=0x{ReadByte(graphicsAddress, 0xD6):X2}; +0xD7=0x{ReadByte(graphicsAddress, 0xD7):X2}");
                lines.Add($"  graphics active bit(+0x88 bit0)={((ReadByte(graphicsAddress, 0x88) & 1) != 0)}; queued bit(+0xD7 bit0)={((ReadByte(graphicsAddress, 0xD7) & 1) != 0)}; outline/load low nibble(+0x89)={ReadByte(graphicsAddress, 0x89) & 0xF}");
            }
            else
            {
                lines.Add("  graphics=none");
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"{label}: active state read failed: {ex.Message}";
        }
    }

    private static string SafeReadPath(SceneBgObject* bg)
    {
        try
        {
            return bg->ModelResourceHandle == null ? string.Empty : bg->ModelResourceHandle->FileName.ToString();
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static string ReadPointer(nint address, int offset)
    {
        try
        {
            return $"0x{*(nint*)(address + offset):X}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static byte ReadByte(nint address, int offset)
        => *(byte*)(address + offset);

    private static nint ReadNint(nint address, int offset)
        => *(nint*)(address + offset);

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

    private readonly record struct LayoutScanResult(
        int Scanned = 0,
        bool Truncated = false,
        string EndReason = "",
        double ElapsedMs = 0,
        bool RealLayoutFound = false,
        int RealLayoutHitIndex = -1,
        bool RealGraphicsFound = false,
        int RealGraphicsHitIndex = -1,
        bool StandaloneGraphicsFound = false,
        int StandaloneGraphicsHitIndex = -1)
    {
        public string ToMultiline()
            => string.Join(Environment.NewLine, new[]
            {
                $"scannedBgParts={this.Scanned}; truncated={this.Truncated}; endedBy={(string.IsNullOrWhiteSpace(this.EndReason) ? "Completed" : this.EndReason)}; elapsedMs={this.ElapsedMs:F2}",
                $"realLayoutFound={this.RealLayoutFound}; hitIndex={this.RealLayoutHitIndex}",
                $"realGraphicsFound={this.RealGraphicsFound}; hitIndex={this.RealGraphicsHitIndex}",
                $"standaloneGraphicsFound={this.StandaloneGraphicsFound}; hitIndex={this.StandaloneGraphicsHitIndex}",
            });
    }

    private readonly record struct ListScanResult(bool Hit, int HitIndex, int ScanCount, bool Truncated, string EndReason, double ElapsedMs)
    {
        public static ListScanResult Empty(string reason)
            => new(false, -1, 0, false, reason, 0);

        public string ToSummary()
            => $"hit={this.Hit}; hitIndex={this.HitIndex}; scanCount={this.ScanCount}; truncated={this.Truncated}; endedBy={this.EndReason}; elapsedMs={this.ElapsedMs:F2}";
    }
}
