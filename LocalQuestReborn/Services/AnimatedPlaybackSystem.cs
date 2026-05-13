using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace LocalQuestReborn.Services;

public sealed unsafe class AnimatedPlaybackSystem
{
    private readonly Dictionary<string, AnimationPlaybackMode> activePlayback = [];
    private readonly Dictionary<string, LocalAnimatedGroupInstance> groups = [];
    private int suppressUpdateFrames;

    public string LastStatus { get; private set; } = "AnimatedPlaybackSystem 尚未运行。";

    public int PlaybackCount => this.activePlayback.Count;

    public int GroupCount => this.groups.Count;

    public IReadOnlyList<LocalAnimatedGroupInstance> Groups => this.groups.Values.ToList();

    public void ConfigureTransformDelta(LocalLayoutObjectInstance instance, LayoutProbeInstance source)
    {
        if (!TryReadSourceTransform(source.Address, out var sourceTransform, out var sourceError))
        {
            this.DisablePlayback(instance, $"读取 source transform 失败：{sourceError}");
            return;
        }

        var localBase = new PlaybackTransform(
            instance.CurrentPosition,
            NormalizeRotation(instance.CurrentRotation),
            NormalizeScale(instance.CurrentScale));

        instance.AnimationSourceBgPart = source.Address;
        instance.AnimationSourceResourcePath = source.ResourcePath;
        instance.AnimationPlaybackMode = AnimationPlaybackMode.TransformDelta;
        instance.AnimationPlaybackEnabled = true;
        instance.AnimationSourceBasePosition = sourceTransform.Position;
        instance.AnimationSourceBaseRotation = sourceTransform.Rotation;
        instance.AnimationSourceBaseScale = sourceTransform.Scale;
        instance.LocalPlaybackBasePosition = localBase.Position;
        instance.LocalPlaybackBaseRotation = localBase.Rotation;
        instance.LocalPlaybackBaseScale = localBase.Scale;
        instance.AnimationSourceBaseTransform = FormatTransform(sourceTransform);
        instance.LocalPlaybackBaseTransform = FormatTransform(localBase);
        instance.PlaybackFrameCount = 0;
        instance.AnimationPlaybackFailedCount = 0;
        instance.AnimationPlaybackLastResult = "TransformDelta playback 已启用：source 只读采样，carrier 播放 delta。";
        this.activePlayback[instance.Id] = AnimationPlaybackMode.TransformDelta;
        this.LastStatus = instance.AnimationPlaybackLastResult;
    }

    public void ConfigureVisibilityCycling(LocalLayoutObjectInstance instance, LayoutProbeInstance sourceChild, string groupId, Vector3 groupBasePosition)
    {
        var localBase = new PlaybackTransform(
            groupBasePosition,
            NormalizeRotation(instance.CurrentRotation),
            NormalizeScale(instance.CurrentScale));

        instance.AnimationSourceBgPart = sourceChild.Address;
        instance.AnimationSourceResourcePath = sourceChild.ResourcePath;
        instance.AnimationGroupId = groupId;
        instance.AnimationGroupChildIndex = sourceChild.ChildIndex;
        instance.AnimationPlaybackMode = AnimationPlaybackMode.VisibilityCycling;
        instance.AnimationPlaybackEnabled = true;
        instance.LocalPlaybackBasePosition = localBase.Position;
        instance.LocalPlaybackBaseRotation = localBase.Rotation;
        instance.LocalPlaybackBaseScale = localBase.Scale;
        instance.LocalPlaybackBaseTransform = FormatTransform(localBase);
        instance.PlaybackFrameCount = 0;
        instance.AnimationPlaybackFailedCount = 0;
        instance.AnimationPlaybackLastResult = "VisibilityCycling playback 已启用：source child 只读采样，carrier 同步 visible。";
        this.activePlayback[instance.Id] = AnimationPlaybackMode.VisibilityCycling;
        this.LastStatus = instance.AnimationPlaybackLastResult;
    }

    public void RegisterGroup(LocalAnimatedGroupInstance group)
    {
        group.PlaybackEnabled = true;
        group.IsRestoring = false;
        group.IsRestored = false;
        group.RestoreStatus = "播放中";
        this.groups[group.GroupId] = group;
    }

    public void DisablePlayback(LocalLayoutObjectInstance? instance, string reason)
    {
        if (instance == null)
            return;

        this.activePlayback.Remove(instance.Id);
        instance.AnimationPlaybackEnabled = false;
        instance.AnimationPlaybackMode = AnimationPlaybackMode.None;
        instance.AnimationPlaybackLastResult = reason;
        this.LastStatus = reason;
    }

    public void StopAllAndDetach(IEnumerable<LocalLayoutObjectInstance> instances, string reason = "已停止全部动画回放。")
    {
        var snapshot = instances.ToList();
        foreach (var group in this.groups.Values)
        {
            group.PlaybackEnabled = false;
            group.IsRestoring = true;
            group.RestoreStatus = reason;
        }

        foreach (var instance in snapshot)
        {
            instance.AnimationPlaybackEnabled = false;
            instance.AnimationPlaybackMode = AnimationPlaybackMode.None;
            instance.AnimationPlaybackLastResult = reason;
            instance.PlaybackFrameCount = 0;
        }

        this.activePlayback.Clear();
        this.groups.Clear();
        this.suppressUpdateFrames = Math.Max(this.suppressUpdateFrames, 1);
        this.LastStatus = $"{reason} 已清空 playback registry，并阻止下一帧 Update 写入。";
    }

    public void Update(IEnumerable<LocalLayoutObjectInstance> instances)
    {
        if (this.suppressUpdateFrames > 0)
        {
            this.suppressUpdateFrames--;
            return;
        }

        var instanceMap = instances.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var instance in instanceMap.Values)
        {
            if (instance.AnimationPlaybackEnabled && instance.AnimationPlaybackMode != AnimationPlaybackMode.None)
                this.activePlayback.TryAdd(instance.Id, instance.AnimationPlaybackMode);
        }

        foreach (var id in this.activePlayback.Keys.ToList())
        {
            if (!instanceMap.TryGetValue(id, out var instance))
            {
                this.activePlayback.Remove(id);
                continue;
            }

            if (!instance.AnimationPlaybackEnabled || instance.AnimationPlaybackMode == AnimationPlaybackMode.None)
            {
                this.activePlayback.Remove(id);
                continue;
            }

            if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate || instance.IsRenderInvalid || instance.IsRestoring)
            {
                this.DisablePlayback(instance, "实例已失效、已恢复、正在恢复或 render invalid，停止动画回放。");
                continue;
            }

            switch (instance.AnimationPlaybackMode)
            {
                case AnimationPlaybackMode.TransformDelta:
                    this.UpdateTransformDelta(instance);
                    break;
                case AnimationPlaybackMode.VisibilityCycling:
                    this.UpdateVisibilityCycling(instance);
                    break;
            }
        }
    }

    private void UpdateTransformDelta(LocalLayoutObjectInstance instance)
    {
        if (!TryReadSourceTransform(instance.AnimationSourceBgPart, out var sourceCurrent, out var sourceError))
        {
            this.FailPlayback(instance, $"读取 source transform 失败：{sourceError}");
            return;
        }

        var sourceBase = new PlaybackTransform(
            instance.AnimationSourceBasePosition,
            NormalizeRotation(instance.AnimationSourceBaseRotation),
            NormalizeScale(instance.AnimationSourceBaseScale));
        var localBase = new PlaybackTransform(
            instance.LocalPlaybackBasePosition,
            NormalizeRotation(instance.LocalPlaybackBaseRotation),
            NormalizeScale(instance.LocalPlaybackBaseScale));
        var positionDelta = sourceCurrent.Position - sourceBase.Position;
        var rotationDelta = NormalizeRotation(sourceCurrent.Rotation * Quaternion.Inverse(sourceBase.Rotation));
        var scaleDelta = new Vector3(
            SafeRatio(sourceCurrent.Scale.X, sourceBase.Scale.X),
            SafeRatio(sourceCurrent.Scale.Y, sourceBase.Scale.Y),
            SafeRatio(sourceCurrent.Scale.Z, sourceBase.Scale.Z));
        var target = new PlaybackTransform(
            localBase.Position + positionDelta,
            NormalizeRotation(rotationDelta * localBase.Rotation),
            new Vector3(
                localBase.Scale.X * scaleDelta.X,
                localBase.Scale.Y * scaleDelta.Y,
                localBase.Scale.Z * scaleDelta.Z));

        if (!WriteCarrierTransform(instance, target, out var writeResult))
        {
            this.FailPlayback(instance, writeResult);
            return;
        }

        if (TryReadCarrierTransform(instance, out var readback, out var readbackError))
        {
            var targetDistance = Vector3.Distance(readback.Position, target.Position);
            var sourceDistance = Vector3.Distance(readback.Position, sourceCurrent.Position);
            if (targetDistance > 2f && sourceDistance < 1f)
            {
                this.DisablePlayback(instance, $"carrier readback 回到 source 位置，疑似写错对象或 carrier 被 controller 控制。targetDistance={targetDistance:F2}; sourceDistance={sourceDistance:F2}");
                return;
            }

            instance.AnimationReadbackRotation = readback.Rotation.ToString();
        }
        else
        {
            instance.AnimationReadbackRotation = $"readback failed: {readbackError}";
        }

        instance.CurrentPosition = target.Position;
        instance.CurrentRotation = target.Rotation;
        instance.CurrentScale = target.Scale;
        instance.CurrentDelta = $"posDelta=({FormatVector(positionDelta)}); rotDelta={rotationDelta}; scaleDelta=({FormatVector(scaleDelta)})";
        instance.AnimationSourceRotation = sourceCurrent.Rotation.ToString();
        instance.AnimationRotationDelta = rotationDelta.ToString();
        instance.AnimationLocalTargetRotation = target.Rotation.ToString();
        instance.LastSampleTime = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        instance.PlaybackFrameCount++;
        instance.AnimationPlaybackFailedCount = 0;
        instance.AnimationPlaybackLastResult = $"TransformDelta frame={instance.PlaybackFrameCount}; {writeResult}";
        this.LastStatus = instance.AnimationPlaybackLastResult;
    }

    private void UpdateVisibilityCycling(LocalLayoutObjectInstance instance)
    {
        if (!TryReadSourceVisible(instance.AnimationSourceBgPart, out var visible, out var sourceError))
        {
            this.FailPlayback(instance, $"读取 source visible 失败：{sourceError}");
            return;
        }

        if (!SetCarrierVisible(instance, visible, out var writeResult))
        {
            this.FailPlayback(instance, writeResult);
            return;
        }

        instance.LastSampleTime = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        instance.PlaybackFrameCount++;
        instance.AnimationPlaybackFailedCount = 0;
        instance.AnimationPlaybackLastResult = $"VisibilityCycling frame={instance.PlaybackFrameCount}; visible={visible}; {writeResult}";
        this.LastStatus = instance.AnimationPlaybackLastResult;
    }

    private void FailPlayback(LocalLayoutObjectInstance instance, string message)
    {
        instance.AnimationPlaybackFailedCount++;
        instance.AnimationPlaybackLastResult = $"{message}（失败 {instance.AnimationPlaybackFailedCount}/30）";
        this.LastStatus = instance.AnimationPlaybackLastResult;
        if (instance.AnimationPlaybackFailedCount < 30)
            return;

        this.DisablePlayback(instance, $"连续 30 次失败，已暂停动画回放：{message}");
    }

    private static bool TryReadSourceTransform(string? address, out PlaybackTransform transform, out string error)
    {
        transform = default;
        error = string.Empty;
        if (!TryGetBgPart(address, out var bgPart, out error))
            return false;

        try
        {
            var layout = (ILayoutInstance*)bgPart;
            var layoutTransform = layout->GetTransformImpl();
            if (layoutTransform != null)
            {
                transform = new PlaybackTransform(layoutTransform->Translation, NormalizeRotation(layoutTransform->Rotation), NormalizeScale(layoutTransform->Scale));
                if (IsTransformNormal(transform))
                    return true;
            }

            if (bgPart->GraphicsObject != null)
            {
                var obj = (SceneObject*)bgPart->GraphicsObject;
                transform = new PlaybackTransform(obj->Position, NormalizeRotation(obj->Rotation), NormalizeScale(obj->Scale));
                return IsTransformNormal(transform);
            }

            error = "source layout transform=null 且 GraphicsObject=null";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadSourceVisible(string? address, out bool visible, out string error)
    {
        visible = false;
        error = string.Empty;
        if (!TryGetBgPart(address, out var bgPart, out error))
            return false;

        try
        {
            if (bgPart->GraphicsObject == null)
            {
                error = "source GraphicsObject=null";
                return false;
            }

            visible = ((SceneBgObject*)bgPart->GraphicsObject)->IsVisible;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool WriteCarrierTransform(LocalLayoutObjectInstance instance, PlaybackTransform transform, out string result)
    {
        result = string.Empty;
        if (!IsTransformNormal(transform))
        {
            result = $"target transform 异常：{FormatTransform(transform)}";
            return false;
        }

        return instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? WriteCarrierVisualTransform(instance, transform, out result)
            : WriteCarrierLayoutTransform(instance, transform, out result);
    }

    private static bool TryReadCarrierTransform(LocalLayoutObjectInstance instance, out PlaybackTransform transform, out string error)
    {
        transform = default;
        error = string.Empty;
        try
        {
            if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            {
                if (!TryRefreshCarrierGraphicsObject(instance, out var graphicsAddress, out error))
                    return false;

                var obj = (SceneObject*)graphicsAddress;
                transform = new PlaybackTransform(obj->Position, NormalizeRotation(obj->Rotation), NormalizeScale(obj->Scale));
                return IsTransformNormal(transform);
            }

            if (!TryGetLayoutPointer(instance.OccupiedSlotAddress, out var pointer))
            {
                error = $"carrier slot 地址无效：{instance.OccupiedSlotAddress}";
                return false;
            }

            var layoutTransform = pointer->GetTransformImpl();
            if (layoutTransform == null)
            {
                error = "carrier layout transform=null";
                return false;
            }

            transform = new PlaybackTransform(layoutTransform->Translation, NormalizeRotation(layoutTransform->Rotation), NormalizeScale(layoutTransform->Scale));
            return IsTransformNormal(transform);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool WriteCarrierVisualTransform(LocalLayoutObjectInstance instance, PlaybackTransform transform, out string result)
    {
        result = string.Empty;
        if (!TryRefreshCarrierGraphicsObject(instance, out var graphicsAddress, out result))
            return false;

        try
        {
            var obj = (SceneObject*)graphicsAddress;
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null || bg->ModelResourceHandle->LoadState != 7)
            {
                result = "carrier ModelResourceHandle 不可用或未加载完成。";
                return false;
            }

            if (!bg->IsVisible)
                bg->IsVisible = true;

            obj->Position = transform.Position;
            obj->Rotation = transform.Rotation;
            obj->Scale = transform.Scale;
            bg->IsTransformChanged = true;
            bg->NotifyTransformChanged();
            bg->UpdateTransforms(true);
            bg->UpdateRender();
            result = $"写 Graphics.Scene.Object transform：{FormatTransform(transform)}";
            return true;
        }
        catch (Exception ex)
        {
            result = $"写 VisualOnly carrier transform 失败：{ex.Message}";
            return false;
        }
    }

    private static bool WriteCarrierLayoutTransform(LocalLayoutObjectInstance instance, PlaybackTransform transform, out string result)
    {
        result = string.Empty;
        if (!TryGetLayoutPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            result = $"carrier slot 地址无效：{instance.OccupiedSlotAddress}";
            return false;
        }

        try
        {
            if (pointer->Id.Type != InstanceType.BgPart)
            {
                result = $"carrier slot 不是 BgPart：{pointer->Id.Type}";
                return false;
            }

            var layoutTransform = new Transform
            {
                Translation = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale,
            };
            pointer->SetTransform(&layoutTransform);
            result = $"写 LayoutInstance transform：{FormatTransform(transform)}";
            return true;
        }
        catch (Exception ex)
        {
            result = $"写 FullLayout carrier transform 失败：{ex.Message}";
            return false;
        }
    }

    private static bool SetCarrierVisible(LocalLayoutObjectInstance instance, bool visible, out string result)
    {
        result = string.Empty;
        if (!TryRefreshCarrierGraphicsObject(instance, out var graphicsAddress, out result))
            return false;

        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null || bg->ModelResourceHandle->LoadState != 7)
            {
                result = "carrier ModelResourceHandle 不可用或未加载完成。";
                return false;
            }

            bg->IsVisible = visible;
            bg->UpdateRender();
            result = $"同步 carrier visible={visible}";
            return true;
        }
        catch (Exception ex)
        {
            result = $"同步 carrier visible 失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryRefreshCarrierGraphicsObject(LocalLayoutObjectInstance instance, out nint graphicsAddress, out string error)
    {
        graphicsAddress = 0;
        if (!TryGetBgPart(instance.OccupiedSlotAddress, out var bgPart, out error))
            return false;

        try
        {
            if (bgPart->GraphicsObject == null)
            {
                error = "carrier GraphicsObject=null";
                return false;
            }

            graphicsAddress = (nint)bgPart->GraphicsObject;
            instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetBgPart(string? rawAddress, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
        {
            error = $"地址无效：{rawAddress}";
            return false;
        }

        bgPart = (BgPartsLayoutInstance*)address;
        try
        {
            if (((ILayoutInstance*)bgPart)->Id.Type != InstanceType.BgPart)
            {
                error = $"地址不是 BgPart：{((ILayoutInstance*)bgPart)->Id.Type}";
                bgPart = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            bgPart = null;
            return false;
        }
    }

    private static bool TryGetLayoutPointer(string? rawAddress, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
            return false;

        pointer = (ILayoutInstance*)address;
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

    private static Quaternion NormalizeRotation(Quaternion rotation)
        => rotation.LengthSquared() < 0.0001f ? Quaternion.Identity : Quaternion.Normalize(rotation);

    private static Vector3 NormalizeScale(Vector3 scale)
        => scale.LengthSquared() < 0.0001f ? Vector3.One : scale;

    private static float SafeRatio(float value, float baseline)
        => Math.Abs(baseline) < 0.0001f ? 1f : value / baseline;

    private static bool IsTransformNormal(PlaybackTransform transform)
        => IsVectorNormal(transform.Position)
            && IsQuaternionNormal(transform.Rotation)
            && IsVectorNormal(transform.Scale)
            && Math.Abs(transform.Scale.X) > 0.0001f
            && Math.Abs(transform.Scale.Y) > 0.0001f
            && Math.Abs(transform.Scale.Z) > 0.0001f;

    private static bool IsVectorNormal(Vector3 value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && Math.Abs(value.X) < 1_000_000f
            && Math.Abs(value.Y) < 1_000_000f
            && Math.Abs(value.Z) < 1_000_000f;

    private static bool IsQuaternionNormal(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() is > 0.0001f and < 10f;

    private static string FormatTransform(PlaybackTransform transform)
        => $"position=({FormatVector(transform.Position)}), rotation={transform.Rotation}, scale=({FormatVector(transform.Scale)})";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private readonly record struct PlaybackTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
