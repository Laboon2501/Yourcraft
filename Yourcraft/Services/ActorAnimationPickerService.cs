using Dalamud.Plugin.Services;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class ActorAnimationPickerService
{
    private readonly SceneDataStore database;
    private readonly RuntimeActorRegistry actorRegistry;
    private readonly ActorAnimationCatalogService catalog;
    private readonly IPluginLog log;

    public ActorAnimationPickerService(
        SceneDataStore database,
        RuntimeActorRegistry actorRegistry,
        ActorAnimationCatalogService catalog,
        IPluginLog log)
    {
        this.database = database;
        this.actorRegistry = actorRegistry;
        this.catalog = catalog;
        this.log = log;
    }

    public ActorAnimationPickerRequest? CurrentRequest { get; private set; }

    public string SearchText { get; set; } = string.Empty;

    public string LastResult { get; private set; } = "尚未选择。";

    public void Open(ActorAnimationPickerRequest request)
    {
        this.CurrentRequest = request;
        this.SearchText = string.Empty;
        this.LastResult = $"正在选择：{request.Title}";
    }

    public void Close()
    {
        this.CurrentRequest = null;
        this.SearchText = string.Empty;
    }

    public IReadOnlyList<ActorAnimationCatalogEntry> Search()
    {
        var request = this.CurrentRequest;
        if (request == null)
            return [];

        return this.catalog.Search(request.PickerMode, this.SearchText, 600).ToList();
    }

    public void Refresh()
    {
        this.catalog.Refresh();
        this.LastResult = "已刷新 ActionTimeline/Emote 列表。";
    }

    public bool Apply(ushort actionTimelineId)
    {
        var request = this.CurrentRequest;
        if (request == null)
        {
            this.LastResult = "没有活动的选择目标。";
            return false;
        }

        try
        {
            switch (request.TargetKind)
            {
                case ActorAnimationPickerTargetKind.NpcDefaultAction:
                    return this.ApplyNpcDefault(request, actionTimelineId);
                case ActorAnimationPickerTargetKind.ActorCurrentAction:
                    return this.ApplyActorCurrent(request, actionTimelineId);
                case ActorAnimationPickerTargetKind.ActorExpression:
                    return this.ApplyActorExpression(request, actionTimelineId);
                case ActorAnimationPickerTargetKind.StepAnimation:
                    return this.ApplyStep(request, actionTimelineId, expression: false);
                case ActorAnimationPickerTargetKind.StepExpression:
                    return this.ApplyStep(request, actionTimelineId, expression: true);
                default:
                    this.LastResult = $"未知选择目标：{request.TargetKind}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to apply selected ActionTimeline. Target={TargetKind}, Id={ActionTimelineId}", request.TargetKind, actionTimelineId);
            this.LastResult = $"写入选择失败：{ex.Message}";
            return false;
        }
    }

    private bool ApplyNpcDefault(ActorAnimationPickerRequest request, ushort actionTimelineId)
    {
        if (string.IsNullOrWhiteSpace(request.NpcId))
        {
            this.LastResult = "NPC 目标为空。";
            return false;
        }

        var npc = this.database.GetNpcById(request.NpcId);
        if (npc == null)
        {
            this.LastResult = $"找不到 NPC 模板：{request.NpcId}";
            return false;
        }

        npc.DefaultAnimationId = actionTimelineId;
        this.database.Save();
        this.LastResult = $"已写入 NPC 默认动画：{npc.Name} -> {actionTimelineId}";
        return true;
    }

    private bool ApplyActorCurrent(ActorAnimationPickerRequest request, ushort actionTimelineId)
    {
        if (string.IsNullOrWhiteSpace(request.ActorRuntimeId))
        {
            this.LastResult = "Actor 目标为空。";
            return false;
        }

        var actor = this.actorRegistry.GetByRuntimeId(request.ActorRuntimeId);
        if (actor == null)
        {
            this.LastResult = $"找不到 Actor：{request.ActorRuntimeId}";
            return false;
        }

        actor.CurrentAnimationId = actionTimelineId;
        this.SaveActorBehavior(actor);
        this.LastResult = $"已写入当前 Actor 动画：{ShortId(actor.RuntimeId)} -> {actionTimelineId}";
        return true;
    }

    private bool ApplyActorExpression(ActorAnimationPickerRequest request, ushort actionTimelineId)
    {
        if (string.IsNullOrWhiteSpace(request.ActorRuntimeId))
        {
            this.LastResult = "Actor expression target is empty.";
            return false;
        }

        var actor = this.actorRegistry.GetByRuntimeId(request.ActorRuntimeId);
        if (actor == null)
        {
            this.LastResult = $"Actor not found: {request.ActorRuntimeId}";
            return false;
        }

        actor.CurrentExpressionId = actionTimelineId;
        if (actor.CurrentExpressionLayer == ActorExpressionLayer.None)
            actor.CurrentExpressionLayer = ActorExpressionLayer.Facial;
        this.SaveActorBehavior(actor);
        this.LastResult = $"Actor expression selected: {ShortId(actor.RuntimeId)} -> {actionTimelineId}";
        return true;
    }

    private bool ApplyStep(ActorAnimationPickerRequest request, ushort actionTimelineId, bool expression)
    {
        if (string.IsNullOrWhiteSpace(request.ActorRuntimeId) || request.StepId == null)
        {
            this.LastResult = "动作步骤目标不完整。";
            return false;
        }

        var actor = this.actorRegistry.GetByRuntimeId(request.ActorRuntimeId);
        if (actor == null)
        {
            this.LastResult = $"找不到 Actor：{request.ActorRuntimeId}";
            return false;
        }

        var step = actor.ActionSequence.FirstOrDefault(item => item.Id == request.StepId.Value);
        if (step == null)
        {
            this.LastResult = $"找不到动作步骤：{request.StepId}";
            return false;
        }

        if (expression)
            step.ExpressionId = actionTimelineId;
        else
            step.AnimationId = actionTimelineId;

        this.SaveActorBehavior(actor);
        this.LastResult = expression
            ? $"已写入步骤表情 ID：{step.Name} -> {actionTimelineId}"
            : $"已写入步骤动画 ID：{step.Name} -> {actionTimelineId}";
        return true;
    }

    private void SaveActorBehavior(RuntimeActorInstance actor)
    {
        var config = this.database.ActorConfigs.FirstOrDefault(item => string.Equals(item.RuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase));
        if (config == null)
            return;

        config.CurrentAnimationId = actor.CurrentAnimationId;
        config.CurrentExpressionId = actor.CurrentExpressionId;
        config.CurrentExpressionLayer = actor.CurrentExpressionLayer;
        config.EnableActionSequence = actor.EnableActionSequence;
        config.ActionSequenceLoop = actor.ActionSequenceLoop;
        config.ActionSequenceLoopDelay = actor.ActionSequenceLoopDelay;
        config.ActionSequence = actor.ActionSequence.Select(CloneStep).ToList();
        this.database.Save();
    }

    private static ActorActionSequenceStep CloneStep(ActorActionSequenceStep step)
        => new()
        {
            Id = step.Id,
            Name = step.Name,
            Kind = step.Kind,
            AnimationId = step.AnimationId,
            LoopAnimation = step.LoopAnimation,
            StayInPose = step.StayInPose,
            RepeatAfterSeconds = step.RepeatAfterSeconds,
            ExpressionId = step.ExpressionId,
            PlayExpressionWithAction = step.PlayExpressionWithAction,
            ExpressionDelaySeconds = step.ExpressionDelaySeconds,
            ExpressionDurationSeconds = step.ExpressionDurationSeconds,
            LoopExpression = step.LoopExpression,
            ExpressionWeight = step.ExpressionWeight,
            ExpressionLayer = step.ExpressionLayer,
            LipTalkKey = step.LipTalkKey,
            LipTalkId = step.LipTalkId,
            PlayLipTalkWithAction = step.PlayLipTalkWithAction,
            LipTalkDelaySeconds = step.LipTalkDelaySeconds,
            LipTalkDurationSeconds = step.LipTalkDurationSeconds,
            LoopLipTalk = step.LoopLipTalk,
            DurationSeconds = step.DurationSeconds,
            BubbleText = step.BubbleText,
            BubbleDurationSeconds = step.BubbleDurationSeconds,
            BubbleUseAutoDuration = step.BubbleUseAutoDuration,
            ShowBubbleOnEnter = step.ShowBubbleOnEnter,
            HideBubbleOnDespawn = step.HideBubbleOnDespawn,
            AllowLookAtDuringStep = step.AllowLookAtDuringStep,
            MoveStartWorldOffset = step.MoveStartWorldOffset,
            MoveEndWorldOffset = step.MoveEndWorldOffset,
            MoveUseAbsoluteWorldTarget = step.MoveUseAbsoluteWorldTarget,
            MoveWorldTarget = step.MoveWorldTarget,
            MoveDurationSeconds = step.MoveDurationSeconds,
            MoveInterpolation = step.MoveInterpolation,
            MoveFaceDirection = step.MoveFaceDirection,
            MoveRestoreAtStepEnd = step.MoveRestoreAtStepEnd,
            MoveAffectsRotation = step.MoveAffectsRotation,
            MoveYawDegrees = step.MoveYawDegrees,
            MoveAnimationId = step.MoveAnimationId,
            PlayMoveAnimationOnEnter = step.PlayMoveAnimationOnEnter,
        };

    private static string ShortId(string id)
        => string.IsNullOrWhiteSpace(id) ? string.Empty : id[..Math.Min(8, id.Length)];
}
