using System.Numerics;

namespace Yourcraft.Services;

public sealed unsafe class BgPartVisualRescueService
{
    private const int VisualMatrixOffset = 0x20;
    private readonly BgPartVisualTransformProbeService probe;
    private readonly Dictionary<nint, Vector3> originalTranslations = [];

    public BgPartVisualRescueService(BgPartVisualTransformProbeService probe)
    {
        this.probe = probe;
    }

    public string LastResult { get; private set; } = "尚未执行 BgPart visual translation 救援。";

    public bool HasOriginalForCurrent
    {
        get
        {
            if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out _))
                return false;

            return this.originalTranslations.ContainsKey(graphicsObjectAddress);
        }
    }

    public void MoveSelectedToPlayer(Vector3 playerPosition)
    {
        if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out var reason))
        {
            this.LastResult = $"无法移动：{reason}";
            return;
        }

        try
        {
            var before = ReadVisualTranslation(graphicsObjectAddress);
            this.originalTranslations.TryAdd(graphicsObjectAddress, before);
            WriteVisualTranslation(graphicsObjectAddress, playerPosition);
            var readback = ReadVisualTranslation(graphicsObjectAddress);
            var success = Vector3.Distance(playerPosition, readback) <= 0.01f;
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已把选中 BgPart 视觉模型移到玩家脚下。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                "writeMethod=只写 GraphicsObject +0x20 MatrixRowTranslation；不写 rotation/scale/layout/collision。",
                $"beforeVisualTranslation={FormatVector(before)}",
                $"targetPlayerPosition={FormatVector(playerPosition)}",
                $"readbackTranslation={FormatVector(readback)}",
                $"success={success}",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"移动选中 BgPart visual translation 失败：{ex.Message}";
        }
    }

    public void RestoreSelectedVisualTranslation()
    {
        if (!this.probe.TryGetCurrentGraphicsObject(out var graphicsObjectAddress, out var reason))
        {
            this.LastResult = $"无法恢复：{reason}";
            return;
        }

        if (!this.originalTranslations.TryGetValue(graphicsObjectAddress, out var original))
        {
            this.LastResult = $"没有保存的 originalVisualTranslation：0x{graphicsObjectAddress:X}";
            return;
        }

        try
        {
            var before = ReadVisualTranslation(graphicsObjectAddress);
            WriteVisualTranslation(graphicsObjectAddress, original);
            var readback = ReadVisualTranslation(graphicsObjectAddress);
            var success = Vector3.Distance(original, readback) <= 0.01f;
            this.LastResult = string.Join(Environment.NewLine, new[]
            {
                "已恢复选中 BgPart visual translation。",
                $"graphicsObjectAddress=0x{graphicsObjectAddress:X}",
                "writeMethod=只写 GraphicsObject +0x20 MatrixRowTranslation；不写 rotation/scale/layout/collision。",
                $"beforeVisualTranslation={FormatVector(before)}",
                $"originalVisualTranslation={FormatVector(original)}",
                $"readbackTranslation={FormatVector(readback)}",
                $"success={success}",
            });
        }
        catch (Exception ex)
        {
            this.LastResult = $"恢复选中 BgPart visual translation 失败：{ex.Message}";
        }
    }

    private static Vector3 ReadVisualTranslation(nint graphicsObjectAddress)
    {
        var matrix = *(Matrix4x4*)((byte*)graphicsObjectAddress + VisualMatrixOffset);
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static void WriteVisualTranslation(nint graphicsObjectAddress, Vector3 translation)
    {
        var basePtr = (byte*)graphicsObjectAddress + VisualMatrixOffset;
        *(float*)(basePtr + 0x30) = translation.X;
        *(float*)(basePtr + 0x34) = translation.Y;
        *(float*)(basePtr + 0x38) = translation.Z;
    }

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";
}
