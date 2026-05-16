using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;

namespace Yourcraft.Services;

public sealed unsafe class LayerDumpService
{
    private readonly Dictionary<string, LayoutTransformSnapshot> reusableOriginals = new(StringComparer.Ordinal);

    public string LastStatus { get; private set; } = "尚未执行 Layer 容器取证。";

    public string LastDump { get; private set; } = "尚未 dump source layer。";

    public string LastFunctionScan { get; private set; } = "尚未扫描 Layout/Layer 函数。";

    public LayoutProbeInstance? ReusableCandidate { get; private set; }

    public string ReuseStatus { get; private set; } = "尚未执行复用候选实验。";

    public string SourceResourcePath { get; private set; } = "未选择";

    public string CandidateResourcePath { get; private set; } = "未选择";

    public string SourceTransformText { get; private set; } = "未读取";

    public string CandidateTransformText { get; private set; } = "未读取";

    public string ReadbackTransformText { get; private set; } = "未读取";

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public void DumpSourceLayer(LayoutProbeInstance? source, IReadOnlyList<LayoutProbeInstance> currentInstances)
    {
        if (source == null)
        {
            this.LastStatus = "请先在 Layout 列表里选择一个复制源。";
            return;
        }

        if (!TryParseAddress(source.LayerAddress, out var layerAddress) || layerAddress == 0)
        {
            this.LastStatus = $"source layer 地址解析失败：{source.LayerAddress}";
            return;
        }

        try
        {
            var layer = (LayerManager*)layerAddress;
            var entries = new List<LayerEntryDump>();
            foreach (var (instanceKey, instancePtr) in layer->Instances)
            {
                if (instancePtr == null || instancePtr.Value == null)
                    continue;

                var instance = instancePtr.Value;
                entries.Add(new LayerEntryDump(instanceKey.ToString(), $"0x{(nint)instance:X}", instance->Id.Type.ToString()));
            }

            var sourceIndex = entries.FindIndex(entry => string.Equals(entry.Address, source.Address, StringComparison.OrdinalIgnoreCase));
            var previous = sourceIndex > 0 ? entries[sourceIndex - 1] : null;
            var next = sourceIndex >= 0 && sourceIndex + 1 < entries.Count ? entries[sourceIndex + 1] : null;
            var knownInstancesInLayer = currentInstances
                .Where(instance => string.Equals(instance.LayerAddress, source.LayerAddress, StringComparison.OrdinalIgnoreCase))
                .Select(instance => $"{instance.Type}:{instance.Address}:{instance.ResourcePath}")
                .Take(20);

            this.LastDump = string.Join(Environment.NewLine, new[]
            {
                $"layerAddress={source.LayerAddress}",
                $"layerId={SafeRead(() => layer->Id.ToString(), "读取 layer->Id 失败")}",
                $"instanceCount={entries.Count}",
                "instanceContainerAddress=未公开；FFXIVClientStructs 只公开 layer->Instances 可枚举包装",
                "firstPointer=" + (entries.FirstOrDefault()?.Address ?? "无"),
                "lastPointer=" + (entries.LastOrDefault()?.Address ?? "无"),
                "capacity=未公开；不能从公开 wrapper 安全读取",
                $"selectedInstanceIndex={sourceIndex}",
                $"selectedInstance={source.Type}:{source.Key}:{source.Address}",
                $"previousInstance={(previous == null ? "无" : $"{previous.Type}:{previous.Key}:{previous.Address}")}",
                $"nextInstance={(next == null ? "无" : $"{next.Type}:{next.Key}:{next.Address}")}",
                "containerGuess=可 foreach 枚举的 native collection wrapper；从插入语义看更像由 LayoutResource/Layer load 流程持有，不应手写 first/last/capacity",
                "knownVisibleRowsInCurrentUi=",
                string.Join(Environment.NewLine, knownInstancesInLayer),
            });
            this.LastStatus = "已完成 source layer 只读 dump。";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"source layer dump 失败：{ex.Message}";
        }
    }

    public void ScanLayoutLayerFunctions()
    {
        try
        {
            var keywords = new[]
            {
                "CreatePrimary",
                "GetSizeOf",
                "SetLayer",
                "Init",
                "Initialize",
                "AddChild",
                "AddInstance",
                "DestroyInstance",
                "DestroyPrimary",
                "CreateInstance",
                "Insert",
                "Allocate",
                "Load",
                "Resource",
                "Callback",
                "SetProperties",
            };

            var assembly = typeof(ILayoutInstance).Assembly;
            var interestingTypes = assembly.GetTypes()
                .Where(type => type.FullName?.Contains("FFXIVClientStructs.FFXIV.Client.LayoutEngine", StringComparison.Ordinal) == true)
                .Where(type =>
                    type.Name.Contains("Layout", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("Layer", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("Instance", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("OBSet", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("Resource", StringComparison.OrdinalIgnoreCase))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            var lines = new List<string>
            {
                $"LayoutEngine type count={interestingTypes.Count}",
                "结论快照：LayerManager 公开 Instances 可枚举；未发现公开 AddInstance/CreateInstance/Insert/Allocate。",
                string.Empty,
            };

            foreach (var type in interestingTypes)
            {
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(member => keywords.Any(keyword => member.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    .Select(member => $"  {member.MemberType} {member.Name}")
                    .Distinct()
                    .Take(40)
                    .ToList();

                if (members.Count == 0)
                    continue;

                lines.Add(type.FullName ?? type.Name);
                lines.AddRange(members);
                lines.Add(string.Empty);
            }

            this.LastFunctionScan = string.Join(Environment.NewLine, lines);
            this.LastStatus = "已扫描 Layout/Layer 相关函数与成员。";
        }
        catch (Exception ex)
        {
            this.LastFunctionScan = $"扫描失败：{ex}";
            this.LastStatus = $"扫描 Layout/Layer 函数失败：{ex.Message}";
        }
    }

    public void FindReusableCandidate(LayoutProbeInstance? source, IReadOnlyList<LayoutProbeInstance> currentInstances)
    {
        if (source == null)
        {
            this.ReuseStatus = "请先选择一个源 BgPart。";
            return;
        }

        var candidates = currentInstances
            .Where(instance => string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
            .Where(instance => !string.Equals(instance.Address, source.Address, StringComparison.OrdinalIgnoreCase))
            .OrderBy(instance => instance.Visible ? 1 : 0)
            .ThenByDescending(instance => instance.DistanceToPlayer)
            .ToList();

        this.ReusableCandidate = candidates.FirstOrDefault();
        this.ManualVisualResult = "尚未人工确认。";
        this.ReuseStatus = this.ReusableCandidate == null
            ? "没有在当前 Layout 列表中找到可复用 BgPart。可以扩大距离过滤后重新 Dump。"
            : $"已选择候选：{this.ReusableCandidate.ResourcePath} @ {this.ReusableCandidate.Address}，距离 {this.ReusableCandidate.DistanceToPlayer:F1}y，可见={this.ReusableCandidate.Visible}";
    }

    public void SelectReusableCandidate(LayoutProbeInstance? candidate)
    {
        if (candidate == null)
        {
            this.ReuseStatus = "没有可选 BgPart 候选。";
            return;
        }

        if (!string.Equals(candidate.Type, "BgPart", StringComparison.Ordinal))
        {
            this.ReuseStatus = $"拒绝选择：当前对象不是 BgPart，而是 {candidate.Type}。";
            return;
        }

        this.ReusableCandidate = candidate;
        this.CandidateResourcePath = candidate.ResourcePath;
        this.CandidateTransformText = $"position={FormatVector(candidate.Position)}, rotation={candidate.Rotation}, scale={FormatVector(candidate.Scale)}";
        this.ManualVisualResult = "尚未人工确认。";
        this.ReuseStatus = $"已选择 BgPart 候选：{candidate.ResourcePath} @ {candidate.Address}，距离 {candidate.DistanceToPlayer:F1}y，可见={candidate.Visible}";
    }

    public void ReuseCandidateAsSource(LayoutProbeInstance? source, Vector3 playerPosition)
        => this.MoveCandidateToPlayer(source, playerPosition);

    public void CopySourceTransformToCandidate(LayoutProbeInstance? source)
    {
        if (source == null)
        {
            this.ReuseStatus = "请先选择源 BgPart。";
            return;
        }

        if (!TryGetPointer(source, out var sourcePointer))
        {
            this.ReuseStatus = $"源地址解析失败：{source.Address}";
            return;
        }

        var sourceTransform = ReadTransform(sourcePointer);
        if (sourceTransform == null)
        {
            this.ReuseStatus = "读取源 transform 失败，未写入。";
            return;
        }

        this.WriteCandidateTransform(source, sourceTransform.Value, "仅复制源 transform 到候选 BgPart");
    }

    public void MoveCandidateToPlayer(LayoutProbeInstance? source, Vector3 playerPosition)
    {
        if (source == null)
        {
            this.ReuseStatus = "请先选择源 BgPart。";
            return;
        }

        var candidateCurrent = this.ReadCandidateTransformOrRecord(source);
        if (candidateCurrent == null)
            return;

        this.WriteCandidateTransform(source, candidateCurrent.Value with { Position = playerPosition }, "候选移动到玩家位置");
    }

    public void MoveCandidateToSourcePosition(LayoutProbeInstance? source)
    {
        if (source == null)
        {
            this.ReuseStatus = "请先选择源 BgPart。";
            return;
        }

        if (!TryGetPointer(source, out var sourcePointer))
        {
            this.ReuseStatus = $"源地址解析失败：{source.Address}";
            return;
        }

        var sourceTransform = ReadTransform(sourcePointer);
        if (sourceTransform == null)
        {
            this.ReuseStatus = "读取源 transform 失败，未写入。";
            return;
        }

        var candidateCurrent = this.ReadCandidateTransformOrRecord(source);
        if (candidateCurrent == null)
            return;

        this.WriteCandidateTransform(source, candidateCurrent.Value with { Position = sourceTransform.Value.Position }, "候选移动到源 BgPart 位置");
    }

    public void RestoreReusableCandidate()
    {
        if (this.ReusableCandidate == null)
        {
            this.ReuseStatus = "没有候选可恢复。";
            return;
        }

        if (!this.reusableOriginals.TryGetValue(this.ReusableCandidate.Address, out var original))
        {
            this.ReuseStatus = "没有保存过该候选的原始 transform。";
            return;
        }

        if (!TryGetPointer(this.ReusableCandidate, out var pointer))
        {
            this.ReuseStatus = $"候选地址解析失败：{this.ReusableCandidate.Address}";
            return;
        }

        var written = WriteTransform(pointer, original);
        var after = ReadTransform(pointer);
        this.ReadbackTransformText = FormatSnapshot(after);
        this.ReuseStatus = written
            ? $"已恢复候选原始 transform。readback={FormatNullableVector(after?.Position)}"
            : "恢复候选原始 transform 失败。";
    }

    public void RecordManualVisualResult(bool moved)
    {
        this.ManualVisualResult = moved
            ? "我看见候选移动了。复用已有 BgPart slot + transform override 可行。"
            : "候选没动。readback 若已变化，可能还需要 layout refresh/update dirty flag。";
    }

    private LayoutTransformSnapshot? ReadCandidateTransformOrRecord(LayoutProbeInstance source)
    {
        if (this.ReusableCandidate == null)
        {
            this.ReuseStatus = "请先查找候选可复用 BgPart。";
            return null;
        }

        if (!TryGetPointer(this.ReusableCandidate, out var candidatePointer))
        {
            this.ReuseStatus = $"候选地址解析失败：{this.ReusableCandidate.Address}";
            return null;
        }

        var before = ReadTransform(candidatePointer);
        if (before == null)
        {
            this.ReuseStatus = "读取候选 transform 失败，未写入。";
            return null;
        }

        this.SourceResourcePath = source.ResourcePath;
        this.CandidateResourcePath = this.ReusableCandidate.ResourcePath;
        this.CandidateTransformText = FormatSnapshot(before);
        if (!this.reusableOriginals.ContainsKey(this.ReusableCandidate.Address))
            this.reusableOriginals[this.ReusableCandidate.Address] = before.Value;

        return before.Value;
    }

    private void WriteCandidateTransform(LayoutProbeInstance source, LayoutTransformSnapshot target, string action)
    {
        if (this.ReusableCandidate == null)
        {
            this.ReuseStatus = "请先查找候选可复用 BgPart。";
            return;
        }

        if (!TryGetPointer(source, out var sourcePointer) || !TryGetPointer(this.ReusableCandidate, out var candidatePointer))
        {
            this.ReuseStatus = "源或候选地址解析失败。";
            return;
        }

        var sourceTransform = ReadTransform(sourcePointer);
        var candidateBefore = ReadTransform(candidatePointer);
        if (sourceTransform == null || candidateBefore == null)
        {
            this.ReuseStatus = "读取源或候选 transform 失败，未写入。";
            return;
        }

        if (!this.reusableOriginals.ContainsKey(this.ReusableCandidate.Address))
            this.reusableOriginals[this.ReusableCandidate.Address] = candidateBefore.Value;

        this.SourceResourcePath = source.ResourcePath;
        this.CandidateResourcePath = this.ReusableCandidate.ResourcePath;
        this.SourceTransformText = FormatSnapshot(sourceTransform);
        this.CandidateTransformText = FormatSnapshot(candidateBefore);

        var written = WriteTransform(candidatePointer, target);
        var after = ReadTransform(candidatePointer);
        this.ReadbackTransformText = FormatSnapshot(after);
        var changed = after != null && Vector3.Distance(candidateBefore.Value.Position, after.Value.Position) > 0.001f;
        var hitTarget = after != null && Vector3.Distance(target.Position, after.Value.Position) <= 0.01f;

        this.ReuseStatus = written
            ? $"{action} 完成。未写 resourcePath，未调用 SetProperties，未 memcpy。readbackChanged={changed}，hitTarget={hitTarget}。"
            : $"{action} 失败：SetTransform 未成功。";
    }

    private static bool TryGetPointer(LayoutProbeInstance instance, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(instance.Address, out var address) || address == 0)
            return false;

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static LayoutTransformSnapshot? ReadTransform(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return null;

        try
        {
            var transform = pointer->GetTransformImpl();
            return transform == null
                ? null
                : new LayoutTransformSnapshot(transform->Translation, transform->Rotation, transform->Scale);
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteTransform(ILayoutInstance* pointer, LayoutTransformSnapshot snapshot)
    {
        if (pointer == null)
            return false;

        try
        {
            var transform = new Transform
            {
                Translation = snapshot.Position,
                Rotation = snapshot.Rotation,
                Scale = snapshot.Scale,
            };
            pointer->SetTransform(&transform);
            return true;
        }
        catch
        {
            return false;
        }
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

    private static string SafeRead(Func<string> read, string fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"{fallback}: {ex.Message}";
        }
    }

    private static string FormatNullableVector(Vector3? vector)
        => vector.HasValue ? FormatVector(vector.Value) : "未读取";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private static string FormatSnapshot(LayoutTransformSnapshot? snapshot)
        => snapshot == null
            ? "未读取"
            : $"position=({FormatVector(snapshot.Value.Position)}), rotation={snapshot.Value.Rotation}, scale=({FormatVector(snapshot.Value.Scale)})";

    private sealed record LayerEntryDump(string Key, string Address, string Type);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
