using System.Numerics;

namespace Yourcraft.Services;

public sealed unsafe class RotationMatrixExperimentService
{
    private static readonly int[] AllowedMatrixOffsets = [0x03C, 0x040];

    private readonly BgPartVisualTransformProbeService probe;
    private nint lastGraphicsObjectAddress;
    private int lastMatrixOffset;
    private Matrix4x4? originalMatrix;

    public RotationMatrixExperimentService(BgPartVisualTransformProbeService probe)
    {
        this.probe = probe;
    }

    public int SelectedMatrixOffset { get; private set; } = 0x03C;

    public IReadOnlyList<int> MatrixOffsets => AllowedMatrixOffsets;

    public string LastResult { get; private set; } = "Rotation matrix 写入会导致模型消失，已暂停。";

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public void SelectMatrixOffset(int offset)
    {
        if (AllowedMatrixOffsets.Contains(offset))
            this.SelectedMatrixOffset = offset;
    }

    public void YawPlusTen() => this.ApplyYawDelta(10f);

    public void YawMinusTen() => this.ApplyYawDelta(-10f);

    public void Restore()
    {
        if (!this.originalMatrix.HasValue || this.lastGraphicsObjectAddress == 0)
        {
            this.LastResult = "没有保存的原始 matrix，无法恢复。";
            return;
        }

        try
        {
            WriteMatrix(this.lastGraphicsObjectAddress, this.lastMatrixOffset, this.originalMatrix.Value);
            var readback = ReadMatrix(this.lastGraphicsObjectAddress, this.lastMatrixOffset);
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复原始 rotation matrix。",
                $"graphicsObjectAddress=0x{this.lastGraphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.lastMatrixOffset:X3}",
                $"readbackTranslation={FormatVector(GetTranslation(readback))}",
                "readback:",
                FormatMatrix(readback),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复原始 rotation matrix 失败：{ex.Message}";
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
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "Rotation matrix readback。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                $"translation={FormatVector(GetTranslation(matrix))}",
                "matrix:",
                FormatMatrix(matrix),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"readback rotation matrix 失败：{ex.Message}";
        }
    }

    public void RecordManualResult(string result)
    {
        this.ManualVisualResult = result;
    }

    private void ApplyYawDelta(float degrees)
    {
        this.LastResult = "Rotation matrix 写入会导致模型消失，已暂停。请使用只读 probe 和 visual translation 找回按钮。";
        return;

#pragma warning disable CS0162
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
            var original = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            if (!this.originalMatrix.HasValue || this.lastGraphicsObjectAddress != graphicsObjectAddress || this.lastMatrixOffset != this.SelectedMatrixOffset)
                this.originalMatrix = original;

            this.lastGraphicsObjectAddress = graphicsObjectAddress;
            this.lastMatrixOffset = this.SelectedMatrixOffset;

            var originalTranslation = GetTranslation(original);
            if (!Matrix4x4.Decompose(original, out var originalScale, out var originalRotation, out var decomposedTranslation))
            {
                this.LastResult = string.Join(Environment.NewLine, new[]
                {
                    "Matrix4x4.Decompose 失败，未写入。",
                    $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                    $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                    $"rawTranslation={FormatVector(originalTranslation)}",
                    "original:",
                    FormatMatrix(original),
                });
                return;
            }

            var yawDelta = Quaternion.CreateFromYawPitchRoll(DegreesToRadians(degrees), 0f, 0f);
            var newRotation = Quaternion.Normalize(originalRotation * yawDelta);
            var newMatrix =
                Matrix4x4.CreateScale(originalScale) *
                Matrix4x4.CreateFromQuaternion(newRotation) *
                Matrix4x4.CreateTranslation(originalTranslation);

            WriteMatrix(graphicsObjectAddress, this.SelectedMatrixOffset, newMatrix);
            var readback = ReadMatrix(graphicsObjectAddress, this.SelectedMatrixOffset);
            var readbackTranslation = GetTranslation(readback);
            var translationDelta = Vector3.Distance(originalTranslation, readbackTranslation);

            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                $"完整 Rotation Matrix yaw {(degrees >= 0 ? "+" : string.Empty)}{degrees:F1}° 写入完成。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                $"matrixOffset=+0x{this.SelectedMatrixOffset:X3}",
                "writeMethod=Decompose original matrix -> Scale * Rotation(newYaw) * Translation(originalTranslation)",
                "forbidden=未写 layout transform；未写 collision；未 memcpy 整个 instance；未批量",
                $"originalTranslationRaw={FormatVector(originalTranslation)}",
                $"decomposedTranslation={FormatVector(decomposedTranslation)}",
                $"originalScale={FormatVector(originalScale)}",
                $"readbackTranslation={FormatVector(readbackTranslation)}",
                $"translationUnchanged={(translationDelta <= 0.001f ? "true" : "false")} delta={translationDelta:F6}",
                "originalMatrix:",
                FormatMatrix(original),
                "newMatrix:",
                FormatMatrix(newMatrix),
                "readbackMatrix:",
                FormatMatrix(readback),
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"完整 Rotation Matrix 写入失败：{ex.Message}";
        }
#pragma warning restore CS0162
    }

    private static Matrix4x4 ReadMatrix(nint graphicsObjectAddress, int matrixOffset)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset);

    private static void WriteMatrix(nint graphicsObjectAddress, int matrixOffset, Matrix4x4 matrix)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset) = matrix;

    private static Vector3 GetTranslation(Matrix4x4 matrix)
        => new(matrix.M41, matrix.M42, matrix.M43);

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";

    private static string FormatMatrix(Matrix4x4 matrix)
        => string.Join(Environment.NewLine, new[]
        {
            $"  [{matrix.M11:F4}, {matrix.M12:F4}, {matrix.M13:F4}, {matrix.M14:F4}]",
            $"  [{matrix.M21:F4}, {matrix.M22:F4}, {matrix.M23:F4}, {matrix.M24:F4}]",
            $"  [{matrix.M31:F4}, {matrix.M32:F4}, {matrix.M33:F4}, {matrix.M34:F4}]",
            $"  [{matrix.M41:F4}, {matrix.M42:F4}, {matrix.M43:F4}, {matrix.M44:F4}]",
        });
}
