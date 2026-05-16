using System.Numerics;

namespace Yourcraft.Services;

public sealed unsafe class VisualOnlyRotationMatrixBlockExperimentService
{
    private static readonly int[] AllowedMatrixOffsets = [0x03C, 0x040];
    private readonly BgPartVisualTransformProbeService probe;
    private nint lastGraphicsObjectAddress;
    private int lastMatrixOffset;
    private Matrix4x4? originalMatrix;

    public VisualOnlyRotationMatrixBlockExperimentService(BgPartVisualTransformProbeService probe)
    {
        this.probe = probe;
    }

    public int SelectedMatrixOffset { get; private set; } = 0x03C;

    public string LastResult { get; private set; } = "尚未执行 VisualOnly rotation matrix block 实验。";

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public IReadOnlyList<int> MatrixOffsets => AllowedMatrixOffsets;

    public void SelectMatrixOffset(int offset)
    {
        if (AllowedMatrixOffsets.Contains(offset))
            this.SelectedMatrixOffset = offset;
    }

    public void WriteYawPlusTen()
    {
        if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out var reason))
        {
            this.LastResult = $"拒绝写入：{reason}";
            return;
        }

        if (!AllowedMatrixOffsets.Contains(this.SelectedMatrixOffset))
        {
            this.LastResult = $"拒绝写入：matrix offset 只允许 +0x03C 或 +0x040，当前 +0x{this.SelectedMatrixOffset:X3}";
            return;
        }

        try
        {
            var before = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            if (!this.originalMatrix.HasValue || this.lastGraphicsObjectAddress != graphicsObjectAddress || this.lastMatrixOffset != this.SelectedMatrixOffset)
                this.originalMatrix = before;

            this.lastGraphicsObjectAddress = graphicsObjectAddress;
            this.lastMatrixOffset = this.SelectedMatrixOffset;

            var written = CreateYawAdjustedBlock(before, DegreesToRadians(10f));
            WriteMatrix(graphicsObjectAddress, this.SelectedMatrixOffset, written);
            var readback = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);

            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "VisualOnly rotation matrix block 写入完成。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                "writeMethod=写入一个 Matrix4x4 block；保留原 block translation 行；不写 layout transform",
                "forbidden=未写 collision；未写 layout transform；未 memcpy 整个 instance；未批量",
                "refreshDirty=暂未发现安全 refresh/dirty 入口，未调用。",
                "before:",
                FormatMatrix(before),
                "written:",
                FormatMatrix(written),
                "readback:",
                FormatMatrix(readback),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"VisualOnly rotation matrix block 写入失败：{ex.Message}";
        }
    }

    public void WriteYawPlusTenKeepTranslation()
    {
        if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out var reason))
        {
            this.LastResult = $"拒绝写入：{reason}";
            return;
        }

        if (!AllowedMatrixOffsets.Contains(this.SelectedMatrixOffset))
        {
            this.LastResult = $"拒绝写入：matrix offset 只允许 +0x03C 或 +0x040，当前 +0x{this.SelectedMatrixOffset:X3}";
            return;
        }

        try
        {
            var visualTranslationBefore = ReadVisualTranslation(graphicsObjectAddress);
            var before = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            if (!this.originalMatrix.HasValue || this.lastGraphicsObjectAddress != graphicsObjectAddress || this.lastMatrixOffset != this.SelectedMatrixOffset)
                this.originalMatrix = before;

            this.lastGraphicsObjectAddress = graphicsObjectAddress;
            this.lastMatrixOffset = this.SelectedMatrixOffset;

            var written = CreateYawAdjustedBlock(before, DegreesToRadians(10f));
            written.M41 = visualTranslationBefore.X;
            written.M42 = visualTranslationBefore.Y;
            written.M43 = visualTranslationBefore.Z;

            WriteMatrix(graphicsObjectAddress, this.SelectedMatrixOffset, written);
            WriteVisualTranslation(graphicsObjectAddress, visualTranslationBefore);

            var readback = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            var visualTranslationAfter = ReadVisualTranslation(graphicsObjectAddress);
            var translationDelta = Vector3.Distance(visualTranslationBefore, visualTranslationAfter);

            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "VisualOnly rotation pivot matrix block 写入完成。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                "writeMethod=写入一个候选 Matrix4x4 block，然后强制恢复 GraphicsObject +0x20 row translation。",
                "forbidden=未写 layout transform；未写 collision；未 memcpy 整个 instance；未批量",
                $"visualTranslationBefore={FormatVector(visualTranslationBefore)}",
                $"visualTranslationAfter={FormatVector(visualTranslationAfter)}",
                $"translationKept={(translationDelta <= 0.001f ? "true" : "false")} delta={translationDelta:F6}",
                "before:",
                FormatMatrix(before),
                "written:",
                FormatMatrix(written),
                "readback:",
                FormatMatrix(readback),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"VisualOnly rotation pivot matrix block 写入失败：{ex.Message}";
        }
    }

    public void Readback()
    {
        if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out var reason))
        {
            this.LastResult = $"无法 readback：{reason}";
            return;
        }

        try
        {
            var matrix = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            var visualTranslation = ReadVisualTranslation(graphicsObjectAddress);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "VisualOnly rotation matrix block readback。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                $"visualTranslation={FormatVector(visualTranslation)}",
                "matrix:",
                FormatMatrix(matrix),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"readback matrix block 失败：{ex.Message}";
        }
    }

    public void Restore()
    {
        if (!this.originalMatrix.HasValue || this.lastGraphicsObjectAddress == 0)
        {
            this.LastResult = "没有保存的原始 matrix block，无法恢复。";
            return;
        }

        try
        {
            WriteMatrix(this.lastGraphicsObjectAddress, this.lastMatrixOffset, this.originalMatrix.Value);
            var readback = ReadMatrix(this.lastGraphicsObjectAddress, this.lastMatrixOffset);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复 VisualOnly rotation matrix block 原值。",
                $"graphicsObjectAddress=0x{this.lastGraphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.lastMatrixOffset:X3}",
                "readback:",
                FormatMatrix(readback),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复 VisualOnly rotation matrix block 失败：{ex.Message}";
        }
    }

    public void TryRefreshDirty()
    {
        this.LastResult = string.Join(Environment.NewLine, new[]
        {
            "未执行 refresh/dirty。",
            "当前没有确认安全的 GraphicsObject refresh/dirty 入口。",
            "如果 matrix block 写入 readback 改变但画面无效，下一步需要继续取证 render update/dirty flag。",
        });
    }

    public void RecordManualResult(string result)
    {
        this.ManualVisualResult = result;
    }

    private static Matrix4x4 CreateYawAdjustedBlock(Matrix4x4 original, float yawRadians)
    {
        var yaw = Matrix4x4.CreateFromYawPitchRoll(yawRadians, 0f, 0f);
        var row1 = Vector3.TransformNormal(new Vector3(original.M11, original.M12, original.M13), yaw);
        var row2 = Vector3.TransformNormal(new Vector3(original.M21, original.M22, original.M23), yaw);
        var row3 = Vector3.TransformNormal(new Vector3(original.M31, original.M32, original.M33), yaw);

        var result = original;
        result.M11 = row1.X;
        result.M12 = row1.Y;
        result.M13 = row1.Z;
        result.M21 = row2.X;
        result.M22 = row2.Y;
        result.M23 = row2.Z;
        result.M31 = row3.X;
        result.M32 = row3.Y;
        result.M33 = row3.Z;
        return result;
    }

    private static Matrix4x4 ReadMatrix(nint graphicsObjectAddress, int matrixOffset)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset);

    private static void WriteMatrix(nint graphicsObjectAddress, int matrixOffset, Matrix4x4 matrix)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset) = matrix;

    private static Vector3 ReadVisualTranslation(nint graphicsObjectAddress)
    {
        var matrix = ReadMatrix(graphicsObjectAddress, 0x20);
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static void WriteVisualTranslation(nint graphicsObjectAddress, Vector3 translation)
    {
        var basePtr = (byte*)graphicsObjectAddress + 0x20;
        *(float*)(basePtr + 0x30) = translation.X;
        *(float*)(basePtr + 0x34) = translation.Y;
        *(float*)(basePtr + 0x38) = translation.Z;
    }

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static string FormatMatrix(Matrix4x4 matrix)
        => string.Join(Environment.NewLine, new[]
        {
            $"  [{matrix.M11:F4}, {matrix.M12:F4}, {matrix.M13:F4}, {matrix.M14:F4}]",
            $"  [{matrix.M21:F4}, {matrix.M22:F4}, {matrix.M23:F4}, {matrix.M24:F4}]",
            $"  [{matrix.M31:F4}, {matrix.M32:F4}, {matrix.M33:F4}, {matrix.M34:F4}]",
            $"  [{matrix.M41:F4}, {matrix.M42:F4}, {matrix.M43:F4}, {matrix.M44:F4}]",
        });

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";
}
