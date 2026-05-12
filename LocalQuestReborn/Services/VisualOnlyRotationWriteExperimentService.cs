using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed unsafe class VisualOnlyRotationWriteExperimentService
{
    private static readonly int[] AllowedMatrixOffsets = [0x03C, 0x040];
    private static readonly int[] AllowedRotationFloatIndices = [1, 2, 4, 6, 8, 9];

    private readonly BgPartVisualTransformProbeService probe;
    private nint lastGraphicsObjectAddress;
    private int lastMatrixOffset;
    private int lastFloatIndex;
    private float? originalValue;

    public VisualOnlyRotationWriteExperimentService(BgPartVisualTransformProbeService probe)
    {
        this.probe = probe;
    }

    public int SelectedMatrixOffset { get; private set; } = 0x03C;

    public int SelectedFloatIndex { get; private set; } = 1;

    public string LastResult { get; private set; } = "当前结论：VisualOnly rotation 单分量写入没有视觉旋转效果，仅保留为失败记录。";

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public string SelectedDescription => $"+0x{this.SelectedMatrixOffset:X3} / float[{this.SelectedFloatIndex}]";

    public IReadOnlyList<int> MatrixOffsets => AllowedMatrixOffsets;

    public IReadOnlyList<int> RotationFloatIndices => AllowedRotationFloatIndices;

    public void SelectMatrixOffset(int offset)
    {
        if (AllowedMatrixOffsets.Contains(offset))
            this.SelectedMatrixOffset = offset;
    }

    public void SelectFloatIndex(int index)
    {
        if (AllowedRotationFloatIndices.Contains(index))
            this.SelectedFloatIndex = index;
    }

    public void Adjust(float delta)
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

        if (!AllowedRotationFloatIndices.Contains(this.SelectedFloatIndex))
        {
            this.LastResult = $"拒绝写入：float index {this.SelectedFloatIndex} 不是允许的 rotation 候选。允许值：{string.Join(", ", AllowedRotationFloatIndices)}";
            return;
        }

        try
        {
            var matrixBefore = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            var before = ReadMatrixFloat(graphicsObjectAddress, this.SelectedMatrixOffset, this.SelectedFloatIndex);
            this.originalValue ??= before;
            this.lastGraphicsObjectAddress = graphicsObjectAddress;
            this.lastMatrixOffset = this.SelectedMatrixOffset;
            this.lastFloatIndex = this.SelectedFloatIndex;

            var written = before + delta;
            WriteMatrixFloat(graphicsObjectAddress, this.SelectedMatrixOffset, this.SelectedFloatIndex, written);
            var readback = ReadMatrixFloat(graphicsObjectAddress, this.SelectedMatrixOffset, this.SelectedFloatIndex);
            var matrixAfter = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);

            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "VisualOnly rotation 单分量写入实验完成。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                $"floatIndex={this.SelectedFloatIndex}",
                $"absoluteOffset=+0x{this.SelectedMatrixOffset + this.SelectedFloatIndex * 4:X3}",
                "writeMethod=只写一个 4-byte float；不写整个 Matrix4x4",
                "forbidden=未写 translation；未写 scale；未写 layout transform；未 memcpy",
                $"before={before:F6}",
                $"delta={delta:+0.000;-0.000;0.000}",
                $"written={written:F6}",
                $"readback={readback:F6}",
                "matrixBefore:",
                FormatMatrix(matrixBefore),
                "matrixAfter:",
                FormatMatrix(matrixAfter),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"VisualOnly rotation 单分量写入失败：{ex.Message}";
        }
    }

    public void Restore()
    {
        if (!this.originalValue.HasValue || this.lastGraphicsObjectAddress == 0)
        {
            this.LastResult = "没有保存的 original float，无法恢复。";
            return;
        }

        try
        {
            WriteMatrixFloat(this.lastGraphicsObjectAddress, this.lastMatrixOffset, this.lastFloatIndex, this.originalValue.Value);
            var readback = ReadMatrixFloat(this.lastGraphicsObjectAddress, this.lastMatrixOffset, this.lastFloatIndex);
            var matrixAfter = ReadMatrix(this.lastGraphicsObjectAddress, this.lastMatrixOffset);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复 VisualOnly rotation 单分量原值。",
                $"graphicsObjectAddress=0x{this.lastGraphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.lastMatrixOffset:X3}",
                $"floatIndex={this.lastFloatIndex}",
                $"restored={this.originalValue.Value:F6}",
                $"readback={readback:F6}",
                "matrixAfterRestore:",
                FormatMatrix(matrixAfter),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复 VisualOnly rotation 单分量失败：{ex.Message}";
        }
    }

    public void RecordManualResult(string result)
    {
        this.ManualVisualResult = result;
    }

    private static Matrix4x4 ReadMatrix(nint graphicsObjectAddress, int matrixOffset)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset);

    private static float ReadMatrixFloat(nint graphicsObjectAddress, int matrixOffset, int floatIndex)
        => *(float*)((byte*)graphicsObjectAddress + matrixOffset + floatIndex * sizeof(float));

    private static void WriteMatrixFloat(nint graphicsObjectAddress, int matrixOffset, int floatIndex, float value)
        => *(float*)((byte*)graphicsObjectAddress + matrixOffset + floatIndex * sizeof(float)) = value;

    private static string FormatMatrix(Matrix4x4 matrix)
        => string.Join(Environment.NewLine, new[]
        {
            $"  [{matrix.M11:F4}, {matrix.M12:F4}, {matrix.M13:F4}, {matrix.M14:F4}]",
            $"  [{matrix.M21:F4}, {matrix.M22:F4}, {matrix.M23:F4}, {matrix.M24:F4}]",
            $"  [{matrix.M31:F4}, {matrix.M32:F4}, {matrix.M33:F4}, {matrix.M34:F4}]",
            $"  [{matrix.M41:F4}, {matrix.M42:F4}, {matrix.M43:F4}, {matrix.M44:F4}]",
        });
}
