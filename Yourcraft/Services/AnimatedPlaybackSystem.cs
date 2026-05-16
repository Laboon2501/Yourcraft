using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class AnimatedPlaybackSystem
{
    private readonly Dictionary<string, AnimationPlaybackMode> activePlayback = [];
    private readonly Dictionary<string, LocalAnimatedGroupInstance> groups = [];

    public string LastStatus { get; private set; } = "AnimatedPlaybackSystem 已暂停；当前版本只支持静态本地场景物体。";

    public int PlaybackCount => this.activePlayback.Count;

    public int GroupCount => this.groups.Count;

    public IReadOnlyList<LocalAnimatedGroupInstance> Groups => this.groups.Values.ToList();

    public void ConfigureTransformDelta(LocalLayoutObjectInstance instance, LayoutProbeInstance source)
        => this.DisablePlayback(instance, "v9.8 静态稳定版已禁用 TransformDelta 动画回放。");

    public void ConfigureVisibilityCycling(LocalLayoutObjectInstance instance, LayoutProbeInstance sourceChild, string groupId, Vector3 groupBasePosition)
        => this.DisablePlayback(instance, "v9.8 静态稳定版已禁用 VisibilityCycling 动画回放。");

    public void RegisterGroup(LocalAnimatedGroupInstance group)
    {
        group.PlaybackEnabled = false;
        group.IsRestoring = false;
        group.IsRestored = true;
        group.RestoreStatus = "v9.8 静态稳定版已禁用 animated group。";
        this.LastStatus = group.RestoreStatus;
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
        foreach (var group in this.groups.Values)
        {
            group.PlaybackEnabled = false;
            group.IsRestoring = false;
            group.IsRestored = true;
            group.RestoreStatus = reason;
        }

        foreach (var instance in instances)
        {
            instance.AnimationPlaybackEnabled = false;
            instance.AnimationPlaybackMode = AnimationPlaybackMode.None;
            instance.AnimationPlaybackLastResult = reason;
            instance.PlaybackFrameCount = 0;
        }

        this.activePlayback.Clear();
        this.groups.Clear();
        this.LastStatus = $"{reason} 已清空 playback registry；当前版本不会执行动态写入。";
    }

    public void Update(IEnumerable<LocalLayoutObjectInstance> instances)
    {
        if (this.activePlayback.Count == 0 && this.groups.Count == 0)
            return;

        this.StopAllAndDetach(instances, "v9.8 静态稳定版：清理残留动画回放。");
    }
}
