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

    public string LastStatus { get; private set; } = "Local actor appearance queue idle.";

    public void Enqueue(string runtimeId, string reason)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
            return;

        this.jobs.Enqueue(new AppearanceApplyJob(runtimeId, reason, DateTime.Now));
        this.LastStatus = $"Queued local actor appearance apply: {runtimeId}, pending={this.jobs.Count}.";
    }

    public void EnqueueForNpc(string npcId)
    {
        var count = 0;
        foreach (var actor in this.registry.GetByNpcId(npcId).ToList())
        {
            this.Enqueue(actor.RuntimeId, $"legacy npc {npcId} batch appearance apply");
            count++;
        }

        this.LastStatus = $"Queued {count} legacy actor appearance job(s).";
    }

    public void EnqueueAll()
    {
        var count = 0;
        foreach (var actor in this.registry.GetAll().ToList())
        {
            this.Enqueue(actor.RuntimeId, "batch local actor appearance apply");
            count++;
        }

        this.LastStatus = $"Queued all actor appearance jobs: {count}.";
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

        this.LastStatus = $"Removed pending appearance jobs for {runtimeId}: {removed}.";
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
                this.LastError = $"Actor not found: {job.RuntimeId}";
                this.LastStatus = this.LastError;
                return;
            }

            var config = this.database.ActorConfigs.FirstOrDefault(item => string.Equals(item.RuntimeId, actor.RuntimeId, StringComparison.OrdinalIgnoreCase));
            if (config == null)
            {
                this.LastError = $"Actor config not found: {actor.RuntimeId}";
                actor.LastAppearanceError = this.LastError;
                actor.LastAppearanceApplyResult = this.LastError;
                this.LastStatus = this.LastError;
                return;
            }

            var success = this.appearanceApplyService.ApplyActorConfigAppearance(config, actor);
            actor.RuntimeAppearanceApplied = success;
            actor.LastSuccessfulAppearanceApplyAt = success ? DateTime.UtcNow : actor.LastSuccessfulAppearanceApplyAt;
            stopwatch.Stop();
            this.LastElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

            if (this.LastElapsedMilliseconds > TimeoutMilliseconds)
            {
                var timeout = $"Appearance job exceeded {TimeoutMilliseconds}ms: actual={this.LastElapsedMilliseconds}ms, runtimeId={job.RuntimeId}";
                actor.LastAppearanceError = timeout;
                actor.LastAppearanceApplyResult = timeout;
                this.LastError = timeout;
                this.LastStatus = timeout;
                this.log.Warning(timeout);
                return;
            }

            this.LastError = success ? string.Empty : actor.LastAppearanceError;
            this.LastStatus = success
                ? $"Local appearance job completed: {job.RuntimeId}, elapsed={this.LastElapsedMilliseconds}ms."
                : $"Local appearance job failed: {job.RuntimeId}, {actor.LastAppearanceError}";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            this.LastElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            this.LastError = ex.Message;
            this.LastStatus = $"Appearance job exception: {job.RuntimeId}, {ex.Message}";
            this.log.Error(ex, "Appearance apply job failed. RuntimeId={RuntimeId}", job.RuntimeId);
        }
        finally
        {
            this.CurrentActorRuntimeId = string.Empty;
        }
    }
}

public sealed record AppearanceApplyJob(string RuntimeId, string Reason, DateTime EnqueuedAt);
