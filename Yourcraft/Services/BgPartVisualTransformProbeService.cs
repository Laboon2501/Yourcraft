using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Terrain;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Yourcraft.Models;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Yourcraft.Services;

public sealed unsafe class BgPartVisualTransformProbeService
{
    private ProbeCandidate? currentCandidate;
    private int stableFollowCount;

    public string LastDump { get; private set; } = "尚未执行 BgPart 视觉 transform 取证。";

    public string LastVerification { get; private set; } = "尚未验证当前候选。";

    public bool VisualTransformWritable { get; private set; }

    public string LastRotationScaleProbe { get; private set; } = "尚未执行 rotation/scale 候选取证。";

    public bool ProbableVisualTransform { get; private set; }

    public int StableFollowCount => this.stableFollowCount;

    public string CurrentCandidateDescription => this.currentCandidate?.Describe() ?? "无";

    public bool TryGetCurrentGraphicsObject(out nint graphicsObjectAddress, out string reason)
    {
        graphicsObjectAddress = 0;
        if (this.currentCandidate == null)
        {
            reason = "没有当前 BgPart GraphicsObject 候选。请先对选中的 BgPart 执行 Dump。";
            return false;
        }

        if (this.currentCandidate.GraphicsObjectAddress == 0)
        {
            reason = "当前候选 GraphicsObject 地址无效。";
            return false;
        }

        graphicsObjectAddress = this.currentCandidate.GraphicsObjectAddress;
        reason = "OK";
        return true;
    }

    public bool TryGetVerifiedMatrixRowTranslationCandidate(out nint graphicsObjectAddress, out int matrixOffset, out string reason)
    {
        graphicsObjectAddress = 0;
        matrixOffset = 0;
        if (this.currentCandidate == null)
        {
            reason = "没有当前候选。";
            return false;
        }

        if (!this.ProbableVisualTransform || this.stableFollowCount < 2)
        {
            reason = $"候选尚未通过稳定性验证：probable={this.ProbableVisualTransform}, stableFollowCount={this.stableFollowCount}";
            return false;
        }

        if (this.currentCandidate.Kind != ProbeCandidateKind.MatrixRowTranslation)
        {
            reason = $"当前候选不是 MatrixRowTranslation：{this.currentCandidate.Kind}";
            return false;
        }

        if (this.currentCandidate.Offset != 0x20)
        {
            reason = $"当前候选 offset 不是 +0x20：+0x{this.currentCandidate.Offset:X}";
            return false;
        }

        graphicsObjectAddress = this.currentCandidate.GraphicsObjectAddress;
        matrixOffset = this.currentCandidate.Offset;
        reason = "OK";
        return true;
    }

    public void Dump(LayoutProbeInstance? instance)
    {
        this.VisualTransformWritable = false;
        this.ProbableVisualTransform = false;
        this.currentCandidate = null;
        this.stableFollowCount = 0;

        if (instance == null)
        {
            this.LastDump = "请先选择一个 BgPart。";
            return;
        }

        if (!string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastDump = $"当前选择不是 BgPart：{instance.Type}";
            return;
        }

        if (!TryParseAddress(instance.Address, out var address) || address == 0)
        {
            this.LastDump = $"地址解析失败：{instance.Address}";
            return;
        }

        try
        {
            var bgPart = (BgPartsLayoutInstance*)address;
            var layoutTransform = ((ILayoutInstance*)bgPart)->GetTransformImpl();
            var layoutPosition = layoutTransform == null ? (Vector3?)null : layoutTransform->Translation;
            var graphicsObject = bgPart->GraphicsObject;
            var modelHandle = ReadModelHandle(bgPart);
            var graphicsScan = ScanGraphicsObject(graphicsObject, layoutPosition, out var bestCandidate);

            if (bestCandidate != null)
            {
                bestCandidate.LayoutInstanceAddress = (nint)bgPart;
                this.currentCandidate = bestCandidate;
            }

            this.LastDump = string.Join(Environment.NewLine, new[]
            {
                $"layoutInstanceAddress={instance.Address}",
                $"graphicsObjectAddress=0x{(nint)graphicsObject:X}",
                "drawObjectAddress=未确认；BgPart GraphicsObject 不是普通 GameObject.DrawObject",
                $"modelResourceHandle={modelHandle}",
                $"layoutPosition={(layoutTransform == null ? "null" : FormatVector(layoutTransform->Translation))}",
                $"layoutRotation={(layoutTransform == null ? "null" : layoutTransform->Rotation.ToString())}",
                $"layoutScale={(layoutTransform == null ? "null" : FormatVector(layoutTransform->Scale))}",
                $"savedCandidate={(this.currentCandidate == null ? "无" : this.currentCandidate.Describe())}",
                "graphicsObject read-only scan:",
                graphicsScan,
                "visualTransformWritable=false",
                "结论：本版本仍不写 GraphicsObject 字段，只保存最接近 layoutPosition 的候选用于稳定性验证。",
            });
        }
        catch (Exception ex)
        {
            this.LastDump = $"BgPart 视觉 transform dump 失败：{ex.Message}";
        }
    }

    public void VerifyCurrentCandidate()
    {
        this.ProbableVisualTransform = false;
        if (this.currentCandidate == null)
        {
            this.LastVerification = "没有可验证候选。请先执行 Dump BgPart 视觉 transform 取证。";
            return;
        }

        var candidate = this.currentCandidate;
        var layout = (ILayoutInstance*)candidate.LayoutInstanceAddress;
        if (layout == null)
        {
            this.LastVerification = "layout instance address 无效。";
            return;
        }

        var originalTransform = layout->GetTransformImpl();
        if (originalTransform == null)
        {
            this.LastVerification = "读取原始 layout transform 失败。";
            return;
        }

        var originalSnapshot = new LayoutTransformSnapshot(originalTransform->Translation, originalTransform->Rotation, originalTransform->Scale);
        var before = TryReadCandidateTranslation(candidate, out var beforeTranslation);
        if (!before)
        {
            this.LastVerification = "读取候选 before translation 失败。";
            return;
        }

        var movedTransform = new Transform
        {
            Translation = originalSnapshot.Position + new Vector3(0f, 1f, 0f),
            Rotation = originalSnapshot.Rotation,
            Scale = originalSnapshot.Scale,
        };

        var restored = false;
        try
        {
            layout->SetTransform(&movedTransform);
            TryReadCandidateTranslation(candidate, out var afterTranslation);
            var delta = afterTranslation - beforeTranslation;
            var expected = new Vector3(0f, 1f, 0f);
            var stable = Vector3.Distance(delta, expected) <= 0.05f;
            this.stableFollowCount = stable ? this.stableFollowCount + 1 : 0;
            this.ProbableVisualTransform = this.stableFollowCount >= 2;

            this.LastVerification = string.Join(Environment.NewLine, new[]
            {
                $"candidate={candidate.Describe()}",
                $"beforeTranslation={FormatVector(beforeTranslation)}",
                $"afterTranslation={FormatVector(afterTranslation)}",
                $"delta={FormatVector(delta)}",
                $"expectedLayoutDelta={FormatVector(expected)}",
                $"stableFollow={stable}",
                $"stableFollowCount={this.stableFollowCount}",
                $"probableVisualTransform={this.ProbableVisualTransform}",
                "注意：本验证只写 layout transform 作为临时探针，随后恢复；没有写 GraphicsObject 字段。",
            });
        }
        catch (Exception ex)
        {
            this.LastVerification = $"验证失败：{ex.Message}";
        }
        finally
        {
            try
            {
                var restoreTransform = new Transform
                {
                    Translation = originalSnapshot.Position,
                    Rotation = originalSnapshot.Rotation,
                    Scale = originalSnapshot.Scale,
                };
                layout->SetTransform(&restoreTransform);
                restored = true;
            }
            catch (Exception ex)
            {
                this.LastVerification += $"{Environment.NewLine}恢复 layout transform 失败：{ex.Message}";
            }

            if (restored)
                this.LastVerification += $"{Environment.NewLine}layout transform 已恢复。";
        }
    }

    public void ProbeRotationScaleCandidates()
    {
        if (this.currentCandidate == null)
        {
            this.LastRotationScaleProbe = "没有当前候选。请先执行 Dump BgPart 视觉 transform 取证。";
            return;
        }

        var candidate = this.currentCandidate;
        var layout = (ILayoutInstance*)candidate.LayoutInstanceAddress;
        if (layout == null)
        {
            this.LastRotationScaleProbe = "layout instance address 无效。";
            return;
        }

        var originalTransform = layout->GetTransformImpl();
        if (originalTransform == null)
        {
            this.LastRotationScaleProbe = "读取原始 layout transform 失败。";
            return;
        }

        var originalSnapshot = new LayoutTransformSnapshot(originalTransform->Translation, originalTransform->Rotation, originalTransform->Scale);
        var before = SnapshotMatrices(candidate.GraphicsObjectAddress);
        var yawLines = new List<string>();
        var scaleLines = new List<string>();

        try
        {
            var yawTransform = new Transform
            {
                Translation = originalSnapshot.Position,
                Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(DegreesToRadians(10f), 0f, 0f) * originalSnapshot.Rotation),
                Scale = originalSnapshot.Scale,
            };
            layout->SetTransform(&yawTransform);
            yawLines = CompareMatrixSnapshots(before, SnapshotMatrices(candidate.GraphicsObjectAddress), "yaw+10deg");

            RestoreLayout(layout, originalSnapshot);

            var scaleTransform = new Transform
            {
                Translation = originalSnapshot.Position,
                Rotation = originalSnapshot.Rotation,
                Scale = originalSnapshot.Scale * 1.1f,
            };
            layout->SetTransform(&scaleTransform);
            scaleLines = CompareMatrixSnapshots(before, SnapshotMatrices(candidate.GraphicsObjectAddress), "scale*1.1");
        }
        catch (Exception ex)
        {
            this.LastRotationScaleProbe = $"rotation/scale probe 失败：{ex.Message}";
        }
        finally
        {
            try
            {
                RestoreLayout(layout, originalSnapshot);
            }
            catch (Exception ex)
            {
                this.LastRotationScaleProbe += $"{Environment.NewLine}恢复 layout transform 失败：{ex.Message}";
            }
        }

        this.LastRotationScaleProbe = string.Join(Environment.NewLine, new[]
        {
            $"graphicsObjectAddress=0x{candidate.GraphicsObjectAddress:X}",
            $"matrixScanCount={before.Count}",
            "Yaw +10° changed matrix candidates:",
            yawLines.Count == 0 ? "  未发现明显跟随 yaw 变化的 matrix 候选。" : string.Join(Environment.NewLine, yawLines.Take(30)),
            "Scale x1.1 changed matrix candidates:",
            scaleLines.Count == 0 ? "  未发现明显跟随 scale 变化的 matrix 候选。" : string.Join(Environment.NewLine, scaleLines.Take(30)),
            "注意：本探针只临时写 layout rotation/scale，随后恢复；不写 GraphicsObject 字段。",
        });
    }

    private static string ScanGraphicsObject(void* graphicsObject, Vector3? layoutPosition, out ProbeCandidate? bestCandidate)
    {
        bestCandidate = null;
        if (graphicsObject == null)
            return "GraphicsObject=null，无法扫描。";

        var lines = new List<string>
        {
            "说明：只读扫描 0x00-0x300，按 4 字节对齐读取 Vector3 和 Matrix4x4 候选；不会写入。",
        };

        try
        {
            var basePtr = (byte*)graphicsObject;
            var vectorHits = new List<(int Offset, Vector3 Value, float Distance)>();
            for (var offset = 0; offset <= 0x300 - sizeof(float) * 3; offset += 4)
            {
                var value = *(Vector3*)(basePtr + offset);
                if (!IsReasonableVector(value))
                    continue;

                var distance = layoutPosition.HasValue ? Vector3.Distance(value, layoutPosition.Value) : float.NaN;
                if (!layoutPosition.HasValue || distance <= 50f)
                    vectorHits.Add((offset, value, distance));
            }

            var matrixHits = new List<(int Offset, bool UseRow, Vector3 Translation, float Distance, Matrix4x4 Matrix)>();
            for (var offset = 0; offset <= 0x300 - sizeof(float) * 16; offset += 4)
            {
                var matrix = *(Matrix4x4*)(basePtr + offset);
                if (!IsReasonableMatrix(matrix))
                    continue;

                var rowTranslation = new Vector3(matrix.M41, matrix.M42, matrix.M43);
                var columnTranslation = new Vector3(matrix.M14, matrix.M24, matrix.M34);
                var rowDistance = layoutPosition.HasValue ? Vector3.Distance(rowTranslation, layoutPosition.Value) : float.NaN;
                var columnDistance = layoutPosition.HasValue ? Vector3.Distance(columnTranslation, layoutPosition.Value) : float.NaN;

                if (!layoutPosition.HasValue || rowDistance <= 50f)
                    matrixHits.Add((offset, true, rowTranslation, rowDistance, matrix));
                if (!layoutPosition.HasValue || columnDistance <= 50f)
                    matrixHits.Add((offset, false, columnTranslation, columnDistance, matrix));
            }

            if (matrixHits.Count > 0)
            {
                var best = matrixHits.OrderBy(hit => float.IsNaN(hit.Distance) ? float.MaxValue : hit.Distance).First();
                bestCandidate = new ProbeCandidate((nint)graphicsObject, best.Offset, best.UseRow ? ProbeCandidateKind.MatrixRowTranslation : ProbeCandidateKind.MatrixColumnTranslation, best.Translation, best.Distance);
            }
            else if (vectorHits.Count > 0)
            {
                var best = vectorHits.OrderBy(hit => float.IsNaN(hit.Distance) ? float.MaxValue : hit.Distance).First();
                bestCandidate = new ProbeCandidate((nint)graphicsObject, best.Offset, ProbeCandidateKind.Vector3, best.Value, best.Distance);
            }

            lines.Add("Vector3 candidates near layout position:");
            lines.Add(vectorHits.Count == 0
                ? "  无接近 layout position 的 Vector3 候选。"
                : string.Join(Environment.NewLine, vectorHits.Take(40).Select(hit => $"  Vector3 @ +0x{hit.Offset:X3}: {FormatVector(hit.Value)}; distanceToLayout={hit.Distance:F3}")));
            lines.Add("Matrix4x4 candidates near layout position:");
            lines.Add(matrixHits.Count == 0
                ? "  无接近 layout position 的 Matrix4x4 候选。"
                : string.Join(Environment.NewLine, matrixHits.Take(24).Select(hit => $"  Matrix4x4 @ +0x{hit.Offset:X3}: {(hit.UseRow ? "rowT" : "columnT")}={FormatVector(hit.Translation)} dist={hit.Distance:F3}; M44={hit.Matrix.M44:F3}")));
            lines.Add($"vectorCandidateCount={vectorHits.Count}; matrixCandidateCount={matrixHits.Count}");
            lines.Add($"selectedBestCandidate={(bestCandidate == null ? "无" : bestCandidate.Describe())}");
        }
        catch (Exception ex)
        {
            lines.Add($"扫描失败：{ex.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryReadCandidateTranslation(ProbeCandidate candidate, out Vector3 translation)
    {
        translation = Vector3.Zero;
        try
        {
            var ptr = (byte*)candidate.GraphicsObjectAddress + candidate.Offset;
            switch (candidate.Kind)
            {
                case ProbeCandidateKind.Vector3:
                    translation = *(Vector3*)ptr;
                    return true;
                case ProbeCandidateKind.MatrixRowTranslation:
                    var rowMatrix = *(Matrix4x4*)ptr;
                    translation = new Vector3(rowMatrix.M41, rowMatrix.M42, rowMatrix.M43);
                    return true;
                case ProbeCandidateKind.MatrixColumnTranslation:
                    var columnMatrix = *(Matrix4x4*)ptr;
                    translation = new Vector3(columnMatrix.M14, columnMatrix.M24, columnMatrix.M34);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<int, Matrix4x4> SnapshotMatrices(nint graphicsObjectAddress)
    {
        var result = new Dictionary<int, Matrix4x4>();
        var basePtr = (byte*)graphicsObjectAddress;
        for (var offset = 0; offset <= 0x300 - sizeof(float) * 16; offset += 4)
        {
            var matrix = *(Matrix4x4*)(basePtr + offset);
            if (IsReasonableMatrix(matrix))
                result[offset] = matrix;
        }

        return result;
    }

    private static List<string> CompareMatrixSnapshots(Dictionary<int, Matrix4x4> before, Dictionary<int, Matrix4x4> after, string label)
    {
        var lines = new List<(float Delta, string Text)>();
        foreach (var (offset, beforeMatrix) in before)
        {
            if (!after.TryGetValue(offset, out var afterMatrix))
                continue;

            var delta = MatrixDelta(beforeMatrix, afterMatrix);
            if (delta <= 0.01f)
                continue;

            var beforeT = new Vector3(beforeMatrix.M41, beforeMatrix.M42, beforeMatrix.M43);
            var afterT = new Vector3(afterMatrix.M41, afterMatrix.M42, afterMatrix.M43);
            lines.Add((delta, $"  {label} Matrix @ +0x{offset:X3}: delta={delta:F4}; rowT {FormatVector(beforeT)} -> {FormatVector(afterT)}; diag {beforeMatrix.M11:F3}/{beforeMatrix.M22:F3}/{beforeMatrix.M33:F3} -> {afterMatrix.M11:F3}/{afterMatrix.M22:F3}/{afterMatrix.M33:F3}"));
        }

        return lines.OrderByDescending(item => item.Delta).Select(item => item.Text).ToList();
    }

    private static float MatrixDelta(Matrix4x4 a, Matrix4x4 b)
    {
        return
            MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M14 - b.M14) +
            MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24) +
            MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34) +
            MathF.Abs(a.M41 - b.M41) + MathF.Abs(a.M42 - b.M42) + MathF.Abs(a.M43 - b.M43) + MathF.Abs(a.M44 - b.M44);
    }

    private static void RestoreLayout(ILayoutInstance* layout, LayoutTransformSnapshot snapshot)
    {
        var restoreTransform = new Transform
        {
            Translation = snapshot.Position,
            Rotation = snapshot.Rotation,
            Scale = snapshot.Scale,
        };
        layout->SetTransform(&restoreTransform);
    }

    private static float DegreesToRadians(float degrees)
        => degrees * (MathF.PI / 180f);

    private static string ReadModelHandle(BgPartsLayoutInstance* bgPart)
    {
        try
        {
            if (bgPart->GraphicsObject == null)
                return "GraphicsObject=null";

            var graphics = (MeddleBgObject*)bgPart->GraphicsObject;
            if (graphics->ModelResourceHandle == null)
                return "ModelResourceHandle=null";

            return $"0x{(nint)graphics->ModelResourceHandle:X}; FileName={graphics->ModelResourceHandle->FileName}; LoadState={graphics->ModelResourceHandle->LoadState}";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
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

    private static bool IsReasonableVector(Vector3 value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z) &&
               Math.Abs(value.X) < 100000f &&
               Math.Abs(value.Y) < 100000f &&
               Math.Abs(value.Z) < 100000f &&
               value.LengthSquared() > 0.0001f;
    }

    private static bool IsReasonableMatrix(Matrix4x4 matrix)
    {
        return IsFinite(matrix.M11) && IsFinite(matrix.M12) && IsFinite(matrix.M13) && IsFinite(matrix.M14) &&
               IsFinite(matrix.M21) && IsFinite(matrix.M22) && IsFinite(matrix.M23) && IsFinite(matrix.M24) &&
               IsFinite(matrix.M31) && IsFinite(matrix.M32) && IsFinite(matrix.M33) && IsFinite(matrix.M34) &&
               IsFinite(matrix.M41) && IsFinite(matrix.M42) && IsFinite(matrix.M43) && IsFinite(matrix.M44) &&
               Math.Abs(matrix.M44) < 10f;
    }

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private sealed record ProbeCandidate(nint GraphicsObjectAddress, int Offset, ProbeCandidateKind Kind, Vector3 CandidateTranslation, float DistanceToLayout)
    {
        public nint LayoutInstanceAddress { get; set; }

        public string Describe()
            => $"graphics=0x{this.GraphicsObjectAddress:X}; layout=0x{this.LayoutInstanceAddress:X}; offset=+0x{this.Offset:X3}; kind={this.Kind}; translation={FormatVector(this.CandidateTranslation)}; distanceToLayout={this.DistanceToLayout:F3}";
    }

    private enum ProbeCandidateKind
    {
        Vector3,
        MatrixRowTranslation,
        MatrixColumnTranslation,
    }

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    [StructLayout(LayoutKind.Explicit, Size = 0xD0)]
    private unsafe struct MeddleBgObject
    {
        [FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
    }
}
