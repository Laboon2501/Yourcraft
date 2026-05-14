using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed class ActorBubbleService
{
    private readonly Dictionary<string, ActorBubbleEntry> bubbles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ActorBubbleEntry> ActiveBubbles => this.bubbles.Values;

    public void Show(RuntimeActorInstance actor, string text, float durationSeconds)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var duration = durationSeconds > 0f
            ? durationSeconds
            : Math.Clamp(1.2f + text.Length * 0.08f, 1f, 8f);

        this.bubbles[actor.RuntimeId] = new ActorBubbleEntry(
            actor.RuntimeId,
            text,
            DateTime.UtcNow.AddSeconds(duration));
    }

    public void Clear(string runtimeId)
        => this.bubbles.Remove(runtimeId);

    public void ClearAll()
        => this.bubbles.Clear();

    public void Update(IEnumerable<RuntimeActorInstance> actors)
    {
        var now = DateTime.UtcNow;
        var validIds = actors
            .Where(actor => actor.IsValid)
            .Select(actor => actor.RuntimeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in this.bubbles
                     .Where(item => item.Value.ExpiresAtUtc <= now || !validIds.Contains(item.Key))
                     .Select(item => item.Key)
                     .ToArray())
        {
            this.bubbles.Remove(key);
        }
    }
}

public sealed record ActorBubbleEntry(string RuntimeId, string Text, DateTime ExpiresAtUtc);
