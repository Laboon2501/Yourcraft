using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Collections.Generic;
using System.Diagnostics;

namespace LocalQuestReborn.Services;

public sealed class AppearanceApplyQueue
{
    private const int TimeoutMilliseconds = 500;

    private readonly Queue<AppearanceApplyJob> jobs = new();
    private readonly QuestDatabase database;
    private readonly RuntimeActorRegistry registry;
    private readonly AppearanceApplyService appearanceApplyService;
    private readonly IPluginLog log;

    public AppearanceApplyQueue(
        QuestDatabase database,
        RuntimeActorRegistry registry,
        AppearanceApplyService appearanceApplyService,
        IPluginLog log)
    {
        this.database = database;
        this.registry = registry;
        this.appearanceApplyService = appearanceApplyService;
        this.log = log;
    }

    public int Count => this.jobs.Count;

    public string CurrentActorRuntimeId { get; private set; } = string.Empty;

    public long LastElapsedMilliseconds { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public string LastStatus { get; private set; } = "外观队列空闲。";

    public void Enqueue(string runtimeId, string reason)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return;

        this.jobs.Enqueue(new AppearanceApplyJob(runtimeId, reason, DateTime.Now));
        this.LastStatus = $"已加入外观队列：{runtimeId}，当前队列 {this.jobs.Count}。";
    }

    public void EnqueueForNpc(string npcId)
    {
        var count = 0;
        foreach (var actor in this.registry.GetByNpcId(npcId).ToList())
        {
            this.Enqueue(actor.RuntimeId, $"NPC {npcId} 批量应用外观");
            count++;
        }

        this.LastStatus = $"已加入 {count} 个 Actor 的外观应用任务。";
    }

    public void EnqueueAll()
    {
        var count = 0;
        foreach (var actor in this.registry.GetAll().ToList())
        {
            this.Enqueue(actor.RuntimeId, "全部 Actor 批量应用外观");
            count++;
        }

        this.LastStatus = $"已加入全部 {count} 个 Actor 的外观应用任务。";
    }

    public int RemoveJobsForActor(string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId) || this.jobs.Count == 0)
            return 0;

        var remaining = this.jobs
            .Where(job => !string.Equals(job.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var removed = this.jobs.Count - remaining.Count;
        if (removed <= 0)
            return 0;

        this.jobs.Clear();
        foreach (var job in remaining)
            this.jobs.Enqueue(job);

        if (string.Equals(this.CurrentActorRuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
            this.CurrentActorRuntimeId = string.Empty;

        this.LastStatus = $"已移除 Actor {runtimeId} 的待处理外观任务：{removed} 个。";
        return removed;
    }

    public void Update()
    {
        if (this.jobs.Count == 0)
        {
            this.CurrentActorRuntimeId = string.Empty;
            return;
        }

        var job = this.jobs.Dequeue();
        this.CurrentActorRuntimeId = job.RuntimeId;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var actor = this.registry.GetByRuntimeId(job.RuntimeId);
            if (actor == null)
            {
                this.LastError = $"Actor 不存在：{job.RuntimeId}";
                this.LastStatus = this.LastError;
                return;
            }

            var npc = this.database.GetNpcById(actor.NpcId);
            if (npc == null)
            {
                this.LastError = $"NPC 配置不存在：{actor.NpcId}";
                actor.LastAppearanceError = this.LastError;
                actor.LastAppearanceApplyResult = this.LastError;
                this.LastStatus = this.LastError;
                return;
            }

            var success = this.appearanceApplyService.ApplyNpcAppearance(npc, actor);
            stopwatch.Stop();
            this.LastElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

            if (this.LastElapsedMilliseconds > TimeoutMilliseconds)
            {
                var timeout = $"外观任务超过 {TimeoutMilliseconds}ms：实际 {this.LastElapsedMilliseconds}ms，RuntimeId={job.RuntimeId}";
                actor.LastAppearanceError = timeout;
                actor.LastAppearanceApplyResult = timeout;
                this.LastError = timeout;
                this.LastStatus = timeout;
                this.log.Warning(timeout);
                return;
            }

            this.LastError = success ? string.Empty : actor.LastAppearanceError;
            this.LastStatus = success
                ? $"外观任务完成：{job.RuntimeId}，耗时 {this.LastElapsedMilliseconds}ms。"
                : $"外观任务失败：{job.RuntimeId}，{actor.LastAppearanceError}";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            this.LastElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            this.LastError = ex.Message;
            this.LastStatus = $"外观任务异常：{job.RuntimeId}，{ex.Message}";
            this.log.Error(ex, "Appearance apply job failed. RuntimeId={RuntimeId}", job.RuntimeId);
        }
        finally
        {
            this.CurrentActorRuntimeId = string.Empty;
        }
    }
}

public sealed record AppearanceApplyJob(string RuntimeId, string Reason, DateTime EnqueuedAt);
