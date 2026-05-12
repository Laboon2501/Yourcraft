using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using LocalQuestReborn.Models;
using System.Numerics;

namespace LocalQuestReborn.Services;

public sealed unsafe class LayoutInstanceTransformService
{
    private readonly Dictionary<string, LayoutTransformSnapshot> originals = new(StringComparer.Ordinal);

    public LayoutProbeInstance? SelectedInstance { get; private set; }

    public string LastStatus { get; private set; } = "尚未选择 Layout instance。";

    public Vector3? BeforePosition { get; private set; }

    public Vector3? TargetPosition { get; private set; }

    public Vector3? AfterPosition { get; private set; }

    public bool LastReadbackChanged { get; private set; }

    public string ManualVisualResult { get; private set; } = "尚未人工确认。";

    public void Select(LayoutProbeInstance instance)
    {
        this.SelectedInstance = instance;
        this.ManualVisualResult = "尚未人工确认。";
        this.Refresh();
    }

    public void MoveY(float delta)
    {
        this.ApplyOffset(new Vector3(0f, delta, 0f));
    }

    public void MoveTo(Vector3 position)
    {
        this.ApplyTargetPosition(position);
    }

    public void RestoreOriginal()
    {
        if (!this.TryGetSelectedPointer(out var instance, out var pointer))
            return;

        if (!this.originals.TryGetValue(instance.Address, out var original))
        {
            this.LastStatus = "没有保存的原始 transform，无法恢复。请先执行一次写入操作。";
            return;
        }

        this.WriteTransform(pointer, original.Position, original.Rotation, original.Scale, "恢复原始位置");
    }

    public void Refresh()
    {
        if (!this.TryGetSelectedPointer(out var instance, out var pointer))
            return;

        var snapshot = ReadTransform(pointer);
        if (snapshot == null)
        {
            this.LastStatus = "重新读取 transform 失败。";
            return;
        }

        this.BeforePosition = snapshot.Value.Position;
        this.TargetPosition = null;
        this.AfterPosition = snapshot.Value.Position;
        this.LastReadbackChanged = false;
        this.LastStatus = $"已重新读取 {instance.Type} transform：{FormatVector(snapshot.Value.Position)}";
    }

    public void RecordManualVisualResult(string result)
    {
        this.ManualVisualResult = result;
    }

    private void ApplyOffset(Vector3 offset)
    {
        if (!this.TryGetSelectedPointer(out _, out var pointer))
            return;

        var current = ReadTransform(pointer);
        if (current == null)
        {
            this.LastStatus = "读取当前 transform 失败，未写入。";
            return;
        }

        this.ApplyTargetPosition(current.Value.Position + offset);
    }

    private void ApplyTargetPosition(Vector3 target)
    {
        if (!this.TryGetSelectedPointer(out _, out var pointer))
            return;

        var current = ReadTransform(pointer);
        if (current == null)
        {
            this.LastStatus = "读取当前 transform 失败，未写入。";
            return;
        }

        this.WriteTransform(pointer, target, current.Value.Rotation, current.Value.Scale, "写入目标位置");
    }

    private void WriteTransform(ILayoutInstance* pointer, Vector3 targetPosition, Quaternion rotation, Vector3 scale, string action)
    {
        if (this.SelectedInstance == null)
            return;

        var before = ReadTransform(pointer);
        if (before == null)
        {
            this.LastStatus = $"{action}失败：写入前读取 transform 失败。";
            return;
        }

        if (!this.originals.ContainsKey(this.SelectedInstance.Address))
            this.originals[this.SelectedInstance.Address] = before.Value;

        this.BeforePosition = before.Value.Position;
        this.TargetPosition = targetPosition;

        try
        {
            var transform = new Transform
            {
                Translation = targetPosition,
                Rotation = rotation,
                Scale = scale,
            };

            pointer->SetTransform(&transform);
        }
        catch (Exception ex)
        {
            this.LastStatus = $"{action}失败：SetTransform 抛出异常：{ex.Message}";
            return;
        }

        var after = ReadTransform(pointer);
        if (after == null)
        {
            this.LastStatus = $"{action}后 readback 失败。";
            return;
        }

        this.AfterPosition = after.Value.Position;
        this.LastReadbackChanged = Vector3.Distance(before.Value.Position, after.Value.Position) > 0.001f;
        var hitTarget = Vector3.Distance(targetPosition, after.Value.Position) <= 0.01f;
        this.LastStatus = $"{action}完成：readback={(this.LastReadbackChanged ? "已变化" : "未变化")}，是否到达目标={hitTarget}。如果 readback 变了但画面没动，可能需要 refresh/update dirty flag。";
    }

    private bool TryGetSelectedPointer(out LayoutProbeInstance instance, out ILayoutInstance* pointer)
    {
        instance = this.SelectedInstance!;
        pointer = null;
        if (this.SelectedInstance == null)
        {
            this.LastStatus = "请先从 Layout 列表选中一个 instance。";
            return false;
        }

        instance = this.SelectedInstance;
        if (!TryParseAddress(instance.Address, out var address) || address == 0)
        {
            this.LastStatus = $"地址解析失败：{instance.Address}";
            return false;
        }

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static LayoutTransformSnapshot? ReadTransform(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return null;

        var transform = pointer->GetTransformImpl();
        if (transform == null)
            return null;

        return new LayoutTransformSnapshot(transform->Translation, transform->Rotation, transform->Scale);
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

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
