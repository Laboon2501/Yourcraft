using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

public sealed unsafe class AnimatedBgPartControllerProbeService
{
    private const int SampleLimit = 60;
    private const int ColliderOffset = 0x38;
    private const int CachedMatricesOffset = 0xA0;
    private const int StainOrBgChangeDataOffset = 0xA8;
    private const int CachedTransformOffset = 0xB0;
    private const int AnimationDataOffset = 0xB8;

    private readonly List<SamplePair> samples = [];
    private LayoutProbeInstance? source;
    private LocalLayoutObjectInstance? localInstance;

    public string LastStatus { get; private set; } = "尚未执行 Animated / Dynamic Material BgPart controller 取证。";

    public string OneShotDump { get; private set; } = "尚未 dump。";

    public string SamplingSummary { get; private set; } = "尚未采样。";

    public string FrameSamplesDump { get; private set; } = "尚未采样。";

    public string UpdateMaterialsDiffDump { get; private set; } = "尚未执行 UpdateMaterials 前后对比。";

    public bool IsSampling { get; private set; }

    public int SamplesCollected => this.samples.Count;

    public void DumpOnce(LayoutProbeInstance? sourceBgPart, LocalLayoutObjectInstance? local)
    {
        var sourceSnapshot = this.ReadSnapshot("原地图 BgPart", sourceBgPart?.Address, sourceBgPart?.ResourcePath, sourceBgPart?.Key, sourceBgPart?.LayerAddress, sourceBgPart?.Source);
        var localSnapshot = this.ReadSnapshot("本地实例", local?.OccupiedSlotAddress, local?.CurrentResourcePath, local?.Id, string.Empty, local?.Notes);
        this.OneShotDump = FormatSnapshotPair(sourceSnapshot, localSnapshot);
        this.LastStatus = "已完成单帧只读 dump；未调用 CleanupRender，未复制指针，未写 controller。";
    }

    public void StartSampling(LayoutProbeInstance? sourceBgPart, LocalLayoutObjectInstance? local)
    {
        if (sourceBgPart == null)
        {
            this.LastStatus = "请先选择原地图中会动/动态材质正常的 BgPart。";
            return;
        }

        if (local == null)
        {
            this.LastStatus = "请先选择 recreate 后的本地实例。";
            return;
        }

        this.source = sourceBgPart;
        this.localInstance = local;
        this.samples.Clear();
        this.SamplingSummary = "正在采样 60 帧。";
        this.FrameSamplesDump = string.Empty;
        this.IsSampling = true;
        this.LastStatus = $"开始 60 帧采样：source={sourceBgPart.Address}; local={local.OccupiedSlotAddress}";
    }

    public void ProbeUpdateMaterialsDiff(LayoutProbeInstance? sourceBgPart, LocalLayoutObjectInstance? local)
    {
        var sourceBefore = this.ReadSnapshot("原地图 BgPart before UpdateMaterials", sourceBgPart?.Address, sourceBgPart?.ResourcePath, sourceBgPart?.Key, sourceBgPart?.LayerAddress, sourceBgPart?.Source);
        var localBefore = this.ReadSnapshot("本地实例 before UpdateMaterials", local?.OccupiedSlotAddress, local?.CurrentResourcePath, local?.Id, string.Empty, local?.Notes);
        var sourceCall = InvokeUpdateMaterials(sourceBgPart?.Address);
        var localCall = InvokeUpdateMaterials(local?.OccupiedSlotAddress);
        var sourceAfter = this.ReadSnapshot("原地图 BgPart after UpdateMaterials", sourceBgPart?.Address, sourceBgPart?.ResourcePath, sourceBgPart?.Key, sourceBgPart?.LayerAddress, sourceBgPart?.Source);
        var localAfter = this.ReadSnapshot("本地实例 after UpdateMaterials", local?.OccupiedSlotAddress, local?.CurrentResourcePath, local?.Id, string.Empty, local?.Notes);

        this.UpdateMaterialsDiffDump = string.Join(Environment.NewLine, [
            "UpdateMaterials 前后对比：未调用 CleanupRender，未复制/写入 controller，只调用 BgObject.UpdateMaterials()。",
            $"Source call: {sourceCall}",
            FormatBeforeAfter(sourceBefore, sourceAfter),
            string.Empty,
            $"Local call: {localCall}",
            FormatBeforeAfter(localBefore, localAfter),
        ]);
        this.LastStatus = "已完成 UpdateMaterials 前后字段对比。";
    }

    public void CancelSampling()
    {
        this.IsSampling = false;
        this.LastStatus = "已取消 60 帧采样。";
    }

    public void Update()
    {
        if (!this.IsSampling)
            return;

        var frame = this.samples.Count + 1;
        var sourceSnapshot = this.ReadSnapshot("原地图 BgPart", this.source?.Address, this.source?.ResourcePath, this.source?.Key, this.source?.LayerAddress, this.source?.Source);
        var localSnapshot = this.ReadSnapshot("本地实例", this.localInstance?.OccupiedSlotAddress, this.localInstance?.CurrentResourcePath, this.localInstance?.Id, string.Empty, this.localInstance?.Notes);
        this.samples.Add(new SamplePair(frame, sourceSnapshot, localSnapshot));
        this.LastStatus = $"Animated BgPart controller 采样中：{this.samples.Count}/{SampleLimit}";

        if (this.samples.Count < SampleLimit)
            return;

        this.IsSampling = false;
        this.SamplingSummary = this.BuildSamplingSummary();
        this.FrameSamplesDump = this.BuildFrameSampleDump();
        this.LastStatus = "已完成 60 帧 Animated / Dynamic Material BgPart controller 取证。";
    }

    private BgPartSnapshot ReadSnapshot(string label, string? address, string? expectedPath, string? key, string? layerAddress, string? sourceText)
    {
        if (!TryGetBgPart(address, out var bgPart, out var error))
            return BgPartSnapshot.Error(label, address ?? string.Empty, expectedPath ?? string.Empty, error);

        var layout = (ILayoutInstance*)bgPart;
        var transform = SafeRead(() =>
        {
            var value = layout->GetTransformImpl();
            return value == null
                ? "layout transform=null"
                : $"layoutPos=({FormatVector(value->Translation)}); layoutRot={value->Rotation}; layoutScale=({FormatVector(value->Scale)})";
        });
        var primaryPath = ReadPrimaryPath(layout);
        var secondaryPath = ReadSecondaryPath(layout);
        var graphicsAddress = $"0x{(nint)bgPart->GraphicsObject:X}";
        var colliderAddress = SafeRead(() => $"0x{(nint)bgPart->Collider:X}");
        var listenerAddress = SafeRead(() => $"0x{(nint)bgPart->CollisionUpdateListener:X}");
        var instanceRaw = ReadRawPointers((byte*)bgPart, 0x00, 0x80, 0x08);
        var layer = SafeRead(() => $"layer=0x{(nint)bgPart->Layer:X}; layout=0x{(nint)layout->Layout:X}");
        var collision =
            $"collider={colliderAddress}; listener={listenerAddress}; meshCrc=0x{SafeRead(() => bgPart->CollisionMeshPathCrc.ToString("X8"))}; " +
            $"analyticCrc=0x{SafeRead(() => bgPart->AnalyticShapeDataCrc.ToString("X8"))}; " +
            $"matLow=0x{SafeRead(() => bgPart->CollisionMaterialIdLow.ToString("X8"))}/0x{SafeRead(() => bgPart->CollisionMaterialMaskLow.ToString("X8"))}; " +
            $"matHigh=0x{SafeRead(() => bgPart->CollisionMaterialIdHigh.ToString("X8"))}/0x{SafeRead(() => bgPart->CollisionMaterialMaskHigh.ToString("X8"))}";

        var bgObject = (SceneBgObject*)bgPart->GraphicsObject;
        var bgObjectDump = bgObject == null
            ? "GraphicsObject=null"
            : ReadBgObjectDump(bgObject);

        return new BgPartSnapshot(
            label,
            address ?? string.Empty,
            key ?? string.Empty,
            expectedPath ?? string.Empty,
            primaryPath,
            secondaryPath,
            graphicsAddress,
            colliderAddress,
            listenerAddress,
            layerAddress ?? string.Empty,
            layer,
            transform,
            collision,
            instanceRaw,
            bgObjectDump,
            sourceText ?? string.Empty,
            ExtractKey(bgObjectDump, "cachedMatrices"),
            ExtractKey(bgObjectDump, "stainOrBgChangeData"),
            ExtractKey(bgObjectDump, "cachedTransform"),
            ExtractKey(bgObjectDump, "animationData"),
            ExtractKey(bgObjectDump, "materialCandidates"),
            ExtractKey(bgObjectDump, "transform"),
            ExtractKey(bgObjectDump, "isTransformChanged"),
            ExtractKey(bgObjectDump, "visible"));
    }

    private static string ReadBgObjectDump(SceneBgObject* bgObject)
    {
        var handleDump = "ModelResourceHandle=null";
        try
        {
            if (bgObject->ModelResourceHandle != null)
            {
                var handle = bgObject->ModelResourceHandle;
                handleDump =
                    $"handle=0x{(nint)handle:X}; fileName={handle->FileName}; loadState={handle->LoadState}; " +
                    $"category={handle->Type.Category} ({(ushort)handle->Type.Category}); fileType={handle->FileType}";
            }
        }
        catch (Exception ex)
        {
            handleDump = $"ModelResourceHandle read failed: {ex.Message}";
        }

        var baseAddress = (nint)bgObject;
        var cachedMatrices = ReadPointer(baseAddress, CachedMatricesOffset);
        var stainOrBgChangeData = ReadPointer(baseAddress, StainOrBgChangeDataOffset);
        var cachedTransform = ReadPointer(baseAddress, CachedTransformOffset);
        var animationData = ReadPointer(baseAddress, AnimationDataOffset);
        var materialCandidates = ReadRawPointers((byte*)bgObject, 0xC0, 0x80, 0x08);
        var objectRaw = ReadRawPointers((byte*)bgObject, 0x00, 0x80, 0x08);
        var transform = SafeRead(() => $"pos=({FormatVector(bgObject->Position)}), rot={bgObject->Rotation}, scale=({FormatVector(bgObject->Scale)})");
        var visible = SafeRead(() => bgObject->IsVisible.ToString());
        var changed = SafeRead(() => bgObject->IsTransformChanged.ToString());

        return string.Join("; ", [
            handleDump,
            $"visible={visible}",
            $"isTransformChanged={changed}",
            $"transform={transform}",
            $"cachedMatrices=0x{cachedMatrices:X}",
            $"stainOrBgChangeData=0x{stainOrBgChangeData:X}",
            $"cachedTransform=0x{cachedTransform:X}",
            $"animationData=0x{animationData:X}",
            $"materialCandidates={materialCandidates}",
            $"objectRaw={objectRaw}",
        ]);
    }

    private string BuildSamplingSummary()
    {
        if (this.samples.Count == 0)
            return "没有采样数据。";

        var builder = new StringBuilder();
        builder.AppendLine("60 帧采样结论（只读）：");
        builder.AppendLine("未调用 CleanupRender，未调用未知 controller 写入，未复制指针，未接入正式流程。");
        builder.AppendLine();
        builder.AppendLine("原地图 BgPart 字段变化：");
        AppendFieldChanges(builder, this.samples.Select(sample => sample.Source).ToList());
        builder.AppendLine();
        builder.AppendLine("本地实例字段变化：");
        AppendFieldChanges(builder, this.samples.Select(sample => sample.Local).ToList());
        builder.AppendLine();
        builder.AppendLine("初步判断：如果原对象的 cachedMatrices / cachedTransform / animationData / materialCandidates 在 60 帧中变化，而本地实例不变，说明本地实例缺少原 layout controller、shared group/event update 或 material animation 上下文。");
        builder.AppendLine("如果两边都不变但视觉原对象仍在动，需要继续向 render/material shader 内部资源或 layout controller 状态取证。");
        return builder.ToString();
    }

    private string BuildFrameSampleDump()
    {
        var builder = new StringBuilder();
        foreach (var sample in this.samples)
        {
            builder.AppendLine($"Frame {sample.Frame}");
            builder.AppendLine($"  Source: cachedMatrices={sample.Source.CachedMatrices}; cachedTransform={sample.Source.CachedTransform}; animationData={sample.Source.AnimationData}; stainOrBgChangeData={sample.Source.StainOrBgChangeData}; material={sample.Source.MaterialCandidates}; transform={sample.Source.Transform}; changed={sample.Source.IsTransformChanged}; visible={sample.Source.Visible}");
            builder.AppendLine($"  Local : cachedMatrices={sample.Local.CachedMatrices}; cachedTransform={sample.Local.CachedTransform}; animationData={sample.Local.AnimationData}; stainOrBgChangeData={sample.Local.StainOrBgChangeData}; material={sample.Local.MaterialCandidates}; transform={sample.Local.Transform}; changed={sample.Local.IsTransformChanged}; visible={sample.Local.Visible}");
        }

        return builder.ToString();
    }

    private static void AppendFieldChanges(StringBuilder builder, IReadOnlyList<BgPartSnapshot> snapshots)
    {
        AppendDistinct(builder, "transform", snapshots.Select(snapshot => snapshot.Transform));
        AppendDistinct(builder, "cachedMatrices", snapshots.Select(snapshot => snapshot.CachedMatrices));
        AppendDistinct(builder, "cachedTransform", snapshots.Select(snapshot => snapshot.CachedTransform));
        AppendDistinct(builder, "animationData", snapshots.Select(snapshot => snapshot.AnimationData));
        AppendDistinct(builder, "stainOrBgChangeData", snapshots.Select(snapshot => snapshot.StainOrBgChangeData));
        AppendDistinct(builder, "materialCandidates", snapshots.Select(snapshot => snapshot.MaterialCandidates));
        AppendDistinct(builder, "isTransformChanged", snapshots.Select(snapshot => snapshot.IsTransformChanged));
        AppendDistinct(builder, "visible", snapshots.Select(snapshot => snapshot.Visible));
    }

    private static void AppendDistinct(StringBuilder builder, string name, IEnumerable<string> values)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(5).ToList();
        builder.AppendLine($"- {name}: distinct={distinct.Count}; samples={string.Join(" | ", distinct)}");
    }

    private static string FormatSnapshotPair(BgPartSnapshot source, BgPartSnapshot local)
        => string.Join(Environment.NewLine, [
            "只读单帧对比：",
            "A. 原地图中会动/动态材质正常的 BgPart",
            FormatSnapshot(source),
            string.Empty,
            "B. recreate 后静态或异常的本地实例",
            FormatSnapshot(local),
        ]);

    private static string FormatBeforeAfter(BgPartSnapshot before, BgPartSnapshot after)
        => string.Join(Environment.NewLine, [
            $"{before.Label} -> {after.Label}",
            $"model: {before.PrimaryPath} -> {after.PrimaryPath}",
            $"cachedMatrices: {before.CachedMatrices} -> {after.CachedMatrices}",
            $"cachedTransform: {before.CachedTransform} -> {after.CachedTransform}",
            $"animationData: {before.AnimationData} -> {after.AnimationData}",
            $"stainOrBgChangeData: {before.StainOrBgChangeData} -> {after.StainOrBgChangeData}",
            $"materialCandidates: {before.MaterialCandidates} -> {after.MaterialCandidates}",
            $"isTransformChanged: {before.IsTransformChanged} -> {after.IsTransformChanged}",
            $"visible: {before.Visible} -> {after.Visible}",
        ]);

    private static string InvokeUpdateMaterials(string? address)
    {
        if (!TryGetBgPart(address, out var bgPart, out var error))
            return error;

        try
        {
            if (bgPart->GraphicsObject == null)
                return "GraphicsObject=null，跳过 UpdateMaterials。";

            var bgObject = (SceneBgObject*)bgPart->GraphicsObject;
            bgObject->UpdateMaterials();
            return "UpdateMaterials() 调用成功。";
        }
        catch (Exception ex)
        {
            return $"UpdateMaterials() 调用失败：{ex.Message}";
        }
    }

    private static string FormatSnapshot(BgPartSnapshot snapshot)
        => string.Join(Environment.NewLine, [
            $"{snapshot.Label}: address={snapshot.Address}; key={snapshot.Key}; expectedPath={snapshot.ExpectedPath}",
            $"primaryPath={snapshot.PrimaryPath}; secondaryPath={snapshot.SecondaryPath}",
            $"GraphicsObject={snapshot.GraphicsObject}; Collider={snapshot.Collider}; CollisionUpdateListener={snapshot.CollisionUpdateListener}",
            $"layerAddress={snapshot.LayerAddress}; layer={snapshot.LayerInfo}; source={snapshot.Source}",
            $"layoutTransform={snapshot.LayoutTransform}",
            $"collision={snapshot.Collision}",
            $"layout raw={snapshot.InstanceRaw}",
            $"BgObject={snapshot.BgObjectDump}",
        ]);

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

    private static string ReadRawPointers(byte* basePtr, int offset, int length, int step)
    {
        var values = new List<string>();
        try
        {
            for (var current = offset; current < offset + length; current += step)
            {
                var value = *(nint*)(basePtr + current);
                values.Add($"+0x{current:X}=0x{value:X}");
            }
        }
        catch (Exception ex)
        {
            values.Add($"read failed: {ex.Message}");
        }

        return string.Join(", ", values);
    }

    private static nint ReadPointer(nint baseAddress, int offset)
    {
        try
        {
            return *(nint*)(baseAddress + offset);
        }
        catch
        {
            return 0;
        }
    }

    private static string ExtractKey(string source, string key)
    {
        var token = key + "=";
        var start = source.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        start += token.Length;
        var end = source.IndexOf(';', start);
        return end < 0 ? source[start..].Trim() : source[start..end].Trim();
    }

    private static bool TryGetBgPart(string? rawAddress, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
        {
            error = $"BgPart 地址无效：{rawAddress}";
            return false;
        }

        bgPart = (BgPartsLayoutInstance*)address;
        try
        {
            if (((ILayoutInstance*)bgPart)->Id.Type != InstanceType.BgPart)
            {
                error = $"当前地址不是 BgPart：{((ILayoutInstance*)bgPart)->Id.Type}";
                bgPart = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"读取 BgPart 类型失败：{ex.Message}";
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
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var hex)
            ? (address = (nint)hex) != 0
            : ulong.TryParse(raw, out var value) && (address = (nint)value) != 0;
    }

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private sealed record SamplePair(int Frame, BgPartSnapshot Source, BgPartSnapshot Local);

    private sealed record BgPartSnapshot(
        string Label,
        string Address,
        string Key,
        string ExpectedPath,
        string PrimaryPath,
        string SecondaryPath,
        string GraphicsObject,
        string Collider,
        string CollisionUpdateListener,
        string LayerAddress,
        string LayerInfo,
        string LayoutTransform,
        string Collision,
        string InstanceRaw,
        string BgObjectDump,
        string Source,
        string CachedMatrices,
        string StainOrBgChangeData,
        string CachedTransform,
        string AnimationData,
        string MaterialCandidates,
        string Transform,
        string IsTransformChanged,
        string Visible)
    {
        public static BgPartSnapshot Error(string label, string address, string expectedPath, string error)
            => new(label, address, string.Empty, expectedPath, error, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, error, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }
}
