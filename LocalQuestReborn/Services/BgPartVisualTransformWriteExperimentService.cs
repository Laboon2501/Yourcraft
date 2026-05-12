using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed unsafe class BgPartVisualTransformWriteExperimentService
{
    private readonly BgPartVisualTransformProbeService probe;
    private nint lastGraphicsObjectAddress;
    private int lastMatrixOffset;
    private Vector3? originalTranslation;

    public BgPartVisualTransformWriteExperimentService(BgPartVisualTransformProbeService probe)
    {
        this.probe = probe;
    }

    public string LastResult { get; private set; } = "尚未执行 VisualOnly 单字段写入实验。";

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public bool HasOriginal => this.originalTranslation.HasValue;

    public void RunYPlusOne()
    {
        if (!this.probe.TryGetVerifiedMatrixRowTranslationCandidate(out var graphicsObjectAddress, out var matrixOffset, out var reason))
        {
            this.LastResult = $"拒绝写入：{reason}";
            return;
        }

        try
        {
            var translation = ReadMatrixRowTranslation(graphicsObjectAddress, matrixOffset);
            this.originalTranslation ??= translation;
            this.lastGraphicsObjectAddress = graphicsObjectAddress;
            this.lastMatrixOffset = matrixOffset;

            var written = translation + new Vector3(0f, 1f, 0f);
            WriteMatrixRowTranslationY(graphicsObjectAddress, matrixOffset, written.Y);
            var readback = ReadMatrixRowTranslation(graphicsObjectAddress, matrixOffset);

            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "VisualOnly 单字段 Y+1 实验完成。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{matrixOffset:X}",
                "writeMethod=只写 MatrixRowTranslation.Y 对应的单个 float；未写整个 Matrix4x4",
                "writeSize=4 bytes",
                $"original={FormatVector(translation)}",
                $"written={FormatVector(written)}",
                $"readback={FormatVector(readback)}",
                "禁止项确认：未写 layout transform，未调用 SetTransform，未写 collision，未 memcpy。",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"VisualOnly 单字段写入失败：{ex.Message}";
        }
    }

    public void Restore()
    {
        if (!this.originalTranslation.HasValue || this.lastGraphicsObjectAddress == 0)
        {
            this.LastResult = "没有保存的 original translation，无法恢复。";
            return;
        }

        try
        {
            WriteMatrixRowTranslationY(this.lastGraphicsObjectAddress, this.lastMatrixOffset, this.originalTranslation.Value.Y);
            var readback = ReadMatrixRowTranslation(this.lastGraphicsObjectAddress, this.lastMatrixOffset);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复 VisualOnly transform 单字段 Y。",
                $"graphicsObjectAddress=0x{this.lastGraphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.lastMatrixOffset:X}",
                $"original={FormatVector(this.originalTranslation.Value)}",
                $"readback={FormatVector(readback)}",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复 VisualOnly transform 失败：{ex.Message}";
        }
    }

    public void RecordManualResult(string result)
    {
        this.ManualVisualResult = result;
    }

    private static Vector3 ReadMatrixRowTranslation(nint graphicsObjectAddress, int matrixOffset)
    {
        var matrix = *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset);
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static void WriteMatrixRowTranslationY(nint graphicsObjectAddress, int matrixOffset, float y)
    {
        var yPtr = (float*)((byte*)graphicsObjectAddress + matrixOffset + 0x34);
        *yPtr = y;
    }

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";
}
