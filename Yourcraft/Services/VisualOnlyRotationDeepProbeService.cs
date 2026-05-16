using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Terrain;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;

namespace Yourcraft.Services;

public sealed unsafe class VisualOnlyRotationDeepProbeService
{
    private static readonly int[] MatrixOffsets = [0x20, 0x30, 0x3C, 0x40, 0x48];

    public string CandidateTable { get; private set; } = "尚未执行 VisualOnly rotation deep probe。";

    public string PivotAnalysis { get; private set; } = "尚未分析 pivot。";

    public string DirtyUpdateCandidates { get; private set; } = "尚未扫描 dirty/update 候选。";

    public string NextStepSuggestion { get; private set; } = "先执行 deep probe。";

    public void Probe(LayoutProbeInstance? instance)
    {
        if (instance == null)
        {
            this.SetError("请先选择一个 BgPart。");
            return;
        }

        if (!string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
        {
            this.SetError($"当前选择不是 BgPart：{instance.Type}");
            return;
        }

        if (!TryParseAddress(instance.Address, out var address) || address == 0)
        {
            this.SetError($"BgPart address 解析失败：{instance.Address}");
            return;
        }

        try
        {
            var bgPart = (BgPartsLayoutInstance*)address;
            var layout = (ILayoutInstance*)bgPart;
            var graphicsObject = (nint)bgPart->GraphicsObject;
            if (graphicsObject == 0)
            {
                this.SetError("GraphicsObject=null。");
                return;
            }

            var originalTransform = layout->GetTransformImpl();
            if (originalTransform == null)
            {
                this.SetError("读取 layout transform 失败。");
                return;
            }

            var original = new LayoutTransformSnapshot(originalTransform->Translation, originalTransform->Rotation, originalTransform->Scale);
            var baseline = Capture("baseline", graphicsObject, original);
            var captures = new List<RotationProbeCapture> { baseline };

            captures.Add(this.CaptureAfterTemporaryLayout(layout, graphicsObject, original, "layout yaw +10", yawDegrees: 10f, pitchDegrees: 0f, rollDegrees: 0f));
            captures.Add(this.CaptureAfterTemporaryLayout(layout, graphicsObject, original, "layout yaw +30", yawDegrees: 30f, pitchDegrees: 0f, rollDegrees: 0f));
            captures.Add(this.CaptureAfterTemporaryLayout(layout, graphicsObject, original, "layout pitch +10", yawDegrees: 0f, pitchDegrees: 10f, rollDegrees: 0f));
            captures.Add(this.CaptureAfterTemporaryLayout(layout, graphicsObject, original, "layout roll +10", yawDegrees: 0f, pitchDegrees: 0f, rollDegrees: 10f));

            RestoreLayout(layout, original);
            this.CandidateTable = BuildCandidateTable(graphicsObject, captures);
            this.PivotAnalysis = BuildPivotAnalysis(instance, captures);
            this.DirtyUpdateCandidates = BuildDirtyUpdateCandidateText();
            this.NextStepSuggestion = string.Join(Environment.NewLine, new[]
            {
                "建议下一步：",
                "1. 不再直接写 +0x03C/+0x040 matrix block。",
                "2. 先确认哪个 offset 的 basis 跟随 yaw/pitch/roll，且 translation 不被作为 pivot/culling 数据使用。",
                "3. 继续只读取证 dirty/update/refresh/bounding/culling 字段。",
                "4. 找到安全 refresh/dirty 入口前，rotation 仍保持暂停。",
            });
        }
        catch (Exception ex)
        {
            this.SetError($"VisualOnly rotation deep probe 失败：{ex.Message}");
        }
    }

    private RotationProbeCapture CaptureAfterTemporaryLayout(ILayoutInstance* layout, nint graphicsObject, LayoutTransformSnapshot original, string label, float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        try
        {
            var transform = new Transform
            {
                Translation = original.Position,
                Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(ToRadians(yawDegrees), ToRadians(pitchDegrees), ToRadians(rollDegrees)) * original.Rotation),
                Scale = original.Scale,
            };
            layout->SetTransform(&transform);
            return Capture(label, graphicsObject, new LayoutTransformSnapshot(transform.Translation, transform.Rotation, transform.Scale));
        }
        finally
        {
            RestoreLayout(layout, original);
        }
    }

    private static RotationProbeCapture Capture(string label, nint graphicsObject, LayoutTransformSnapshot layout)
    {
        var matrices = MatrixOffsets.ToDictionary(offset => offset, offset => ReadMatrix(graphicsObject, offset));
        var vectors = new Dictionary<int, Vector3>();
        for (var offset = 0x50; offset <= 0x90; offset += 4)
            vectors[offset] = ReadVector3(graphicsObject, offset);

        return new RotationProbeCapture(label, layout, matrices, vectors);
    }

    private static string BuildCandidateTable(nint graphicsObject, IReadOnlyList<RotationProbeCapture> captures)
    {
        var baseline = captures[0];
        var lines = new List<string>
        {
            $"graphicsObjectAddress=0x{graphicsObject:X}",
            "Matrix offsets: +0x20 / +0x30 / +0x3C / +0x40 / +0x48",
            string.Empty,
            "Baseline:",
            DumpCapture(baseline),
            string.Empty,
            "Comparisons:",
        };

        foreach (var capture in captures.Skip(1))
        {
            lines.Add($"[{capture.Label}] layoutPosition={FormatVector(capture.Layout.Position)} layoutScale={FormatVector(capture.Layout.Scale)} layoutRotation={capture.Layout.Rotation}");
            foreach (var offset in MatrixOffsets)
            {
                var before = baseline.Matrices[offset];
                var after = capture.Matrices[offset];
                lines.Add($"  +0x{offset:X2}: {CompareMatrix(before, after)}");
            }

            lines.Add("  Vector3 +0x50..+0x90 changes:");
            foreach (var (offset, beforeVector) in baseline.Vectors)
            {
                var afterVector = capture.Vectors[offset];
                var delta = Vector3.Distance(beforeVector, afterVector);
                if (delta > 0.001f)
                    lines.Add($"    +0x{offset:X2}: {FormatVector(beforeVector)} -> {FormatVector(afterVector)} delta={delta:F4}");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPivotAnalysis(LayoutProbeInstance instance, IReadOnlyList<RotationProbeCapture> captures)
    {
        var baseline = captures[0];
        var layoutPosition = baseline.Layout.Position;
        var visualTranslation = GetTranslation(baseline.Matrices[0x20]);
        var lines = new List<string>
        {
            $"resourcePath={instance.ResourcePath}",
            $"layoutPosition={FormatVector(layoutPosition)}",
            $"visualTranslation(+0x20)={FormatVector(visualTranslation)}",
            $"layoutToVisualDelta={FormatVector(visualTranslation - layoutPosition)} distance={Vector3.Distance(layoutPosition, visualTranslation):F4}",
            "matrix translation / pivot candidates:",
        };

        foreach (var offset in MatrixOffsets)
        {
            var translation = GetTranslation(baseline.Matrices[offset]);
            lines.Add($"  +0x{offset:X2}: matrixTranslation={FormatVector(translation)} deltaToLayout={FormatVector(translation - layoutPosition)} deltaToVisual={FormatVector(translation - visualTranslation)}");
        }

        lines.Add("推断：+0x040 写入后绕圈，说明该 block 的 translation/basis 可能包含 pivot、culling 或中间空间数据，而不是最终可直接覆盖的 world visual rotation。");
        lines.Add("要原地旋转，需要找到真正参与渲染提交的 local/world rotation 字段，或找到写入后应调用的 dirty/refresh/update 入口。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDirtyUpdateCandidateText()
    {
        var keywords = new[] { "Update", "Refresh", "Dirty", "Transform", "Calculate", "Bounding", "Bounds", "Culling", "Cull", "Draw", "Model", "Resource", "Matrix" };
        var assemblies = new[] { typeof(BgPartsLayoutInstance).Assembly };
        var lines = new List<string>
        {
            "只读反射扫描 FFXIVClientStructs LayoutEngine / Graphics 相关类型：",
        };

        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes())
                     .Where(type => type.FullName?.Contains("FFXIVClientStructs.FFXIV.Client", StringComparison.Ordinal) == true)
                     .Where(type => type.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                                    type.Name.Contains("BgPart", StringComparison.OrdinalIgnoreCase) ||
                                    type.Name.Contains("Layout", StringComparison.OrdinalIgnoreCase) ||
                                    type.Name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
                                    type.Name.Contains("Draw", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(type => type.FullName)
                     .Take(120))
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(member => keywords.Any(keyword => member.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .Select(member => $"  {member.MemberType} {member.Name}")
                .Distinct()
                .Take(20)
                .ToList();

            if (members.Count == 0)
                continue;

            lines.Add(type.FullName ?? type.Name);
            lines.AddRange(members);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DumpCapture(RotationProbeCapture capture)
    {
        var lines = new List<string>
        {
            $"layoutPosition={FormatVector(capture.Layout.Position)}",
            $"layoutScale={FormatVector(capture.Layout.Scale)}",
            $"layoutRotation={capture.Layout.Rotation}",
            $"visualTranslation(+0x20)={FormatVector(GetTranslation(capture.Matrices[0x20]))}",
        };

        foreach (var offset in MatrixOffsets)
            lines.Add($"  Matrix +0x{offset:X2}: T={FormatVector(GetTranslation(capture.Matrices[offset]))}; basis={FormatBasis(capture.Matrices[offset])}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string CompareMatrix(Matrix4x4 before, Matrix4x4 after)
    {
        var translationDelta = Vector3.Distance(GetTranslation(before), GetTranslation(after));
        var basisDelta = BasisDelta(before, after);
        var scaleBefore = ExtractScale(before);
        var scaleAfter = ExtractScale(after);
        var scaleDelta = Vector3.Distance(scaleBefore, scaleAfter);
        var changed = new List<string>();
        AddChanged(changed, "M11", before.M11, after.M11);
        AddChanged(changed, "M12", before.M12, after.M12);
        AddChanged(changed, "M13", before.M13, after.M13);
        AddChanged(changed, "M21", before.M21, after.M21);
        AddChanged(changed, "M22", before.M22, after.M22);
        AddChanged(changed, "M23", before.M23, after.M23);
        AddChanged(changed, "M31", before.M31, after.M31);
        AddChanged(changed, "M32", before.M32, after.M32);
        AddChanged(changed, "M33", before.M33, after.M33);
        AddChanged(changed, "T", translationDelta, 0f);

        var category = translationDelta > 0.001f
            ? "rotation+translation/pivot"
            : basisDelta > 0.001f
                ? "rotation-only candidate"
                : scaleDelta > 0.001f
                    ? "scale candidate"
                    : "unchanged";

        return $"{category}; basisDelta={basisDelta:F4}; translationDelta={translationDelta:F4}; scaleDelta={scaleDelta:F4}; changed={string.Join(", ", changed.Take(16))}";
    }

    private static void AddChanged(List<string> output, string name, float before, float after)
    {
        if (MathF.Abs(before - after) > 0.001f)
            output.Add($"{name}:{before:F3}->{after:F3}");
    }

    private static Vector3 ExtractScale(Matrix4x4 matrix)
        => new(
            new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
            new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(),
            new Vector3(matrix.M31, matrix.M32, matrix.M33).Length());

    private static float BasisDelta(Matrix4x4 a, Matrix4x4 b)
        => MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13)
         + MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23)
         + MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33);

    private static string FormatBasis(Matrix4x4 matrix)
        => $"[{matrix.M11:F3},{matrix.M12:F3},{matrix.M13:F3}] [{matrix.M21:F3},{matrix.M22:F3},{matrix.M23:F3}] [{matrix.M31:F3},{matrix.M32:F3},{matrix.M33:F3}]";

    private static Matrix4x4 ReadMatrix(nint graphicsObjectAddress, int offset)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + offset);

    private static Vector3 ReadVector3(nint graphicsObjectAddress, int offset)
        => *(Vector3*)((byte*)graphicsObjectAddress + offset);

    private static Vector3 GetTranslation(Matrix4x4 matrix)
        => new(matrix.M41, matrix.M42, matrix.M43);

    private static void RestoreLayout(ILayoutInstance* layout, LayoutTransformSnapshot snapshot)
    {
        var transform = new Transform
        {
            Translation = snapshot.Position,
            Rotation = snapshot.Rotation,
            Scale = snapshot.Scale,
        };
        layout->SetTransform(&transform);
    }

    private static bool TryParseAddress(string raw, out nint address)
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

    private static float ToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";

    private void SetError(string message)
    {
        this.CandidateTable = message;
        this.PivotAnalysis = message;
        this.DirtyUpdateCandidates = message;
        this.NextStepSuggestion = message;
    }

    private sealed record RotationProbeCapture(
        string Label,
        LayoutTransformSnapshot Layout,
        IReadOnlyDictionary<int, Matrix4x4> Matrices,
        IReadOnlyDictionary<int, Vector3> Vectors);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
