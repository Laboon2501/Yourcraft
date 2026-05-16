using Dalamud.Plugin.Services;
using Yourcraft.Models;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Yourcraft.Services;

public sealed class ActorNativeBubbleService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;
    private readonly Dictionary<string, NativeBubbleEntry> bubbles = new(StringComparer.OrdinalIgnoreCase);
    private uint generation;

    public ActorNativeBubbleService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public string LastResult { get; private set; } = "Native bubble idle.";

    public void Show(RuntimeActorInstance actor, string text, float durationSeconds, bool useAutoDuration)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text) || actor.VisibilityRuntimeState == ActorVisibilityRuntimeState.SequenceHidden)
            return;

        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            this.LastResult = "UnsafeMode=false, native balloon skipped.";
            return;
        }

        if (!TryReadAddress(actor, out var address) || address == 0)
        {
            this.LastResult = $"Actor address unavailable: {actor.Address}";
            return;
        }

        var duration = useAutoDuration || durationSeconds <= 0f
            ? Math.Clamp(1.2f + text.Length * 0.08f, 1f, 8f)
            : durationSeconds;

        try
        {
            unsafe
            {
                var character = (NativeCharacter*)address;
                character->YellBalloon.OpenBalloon(text, duration, false, 0f, false, false, true, 25);
            }

            var entryGeneration = ++this.generation;
            this.bubbles[actor.RuntimeId] = new NativeBubbleEntry(actor.RuntimeId, address, text, DateTime.UtcNow.AddSeconds(duration + 0.2f), entryGeneration);
            this.LastResult = $"Native balloon opened: actor={Short(actor.RuntimeId)}, duration={duration:F1}s";
        }
        catch (Exception ex)
        {
            this.LastResult = $"Native balloon failed: {ex.Message}";
            this.log.Warning(ex, "Failed to show native actor balloon. RuntimeId={RuntimeId}", actor.RuntimeId);
        }
    }

    public void Clear(RuntimeActorInstance actor)
    {
        if (actor == null)
            return;

        if (TryReadAddress(actor, out var currentAddress))
            this.CloseIfSafe(actor.RuntimeId, currentAddress);

        this.bubbles.Remove(actor.RuntimeId);
    }

    public void Clear(string runtimeId)
    {
        if (this.bubbles.TryGetValue(runtimeId, out var entry))
            this.CloseIfSafe(runtimeId, entry.CharacterAddress);

        this.bubbles.Remove(runtimeId);
    }

    public void ClearAll()
    {
        foreach (var entry in this.bubbles.Values.ToArray())
            this.CloseIfSafe(entry.RuntimeId, entry.CharacterAddress);

        this.bubbles.Clear();
    }

    public void Update(IEnumerable<RuntimeActorInstance> actors)
    {
        var now = DateTime.UtcNow;
        var valid = actors
            .Where(actor => actor.IsValid && actor.VisibilityRuntimeState != ActorVisibilityRuntimeState.SequenceHidden && TryReadAddress(actor, out var address) && address != 0)
            .ToDictionary(actor => actor.RuntimeId, actor => actor, StringComparer.OrdinalIgnoreCase);

        foreach (var key in this.bubbles.Keys.ToArray())
        {
            var entry = this.bubbles[key];
            if (entry.ExpiresAtUtc <= now || !valid.TryGetValue(key, out var actor) || !TryReadAddress(actor, out var address) || address != entry.CharacterAddress)
                this.bubbles.Remove(key);
        }
    }

    private void CloseIfSafe(string runtimeId, nint address)
    {
        if (address == 0 || !this.brioAssemblyBridge.EnableUnsafeNativeWrites)
            return;

        try
        {
            unsafe
            {
                var character = (NativeCharacter*)address;
                character->YellBalloon.CloseBalloon();
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to close native actor balloon. RuntimeId={RuntimeId}", runtimeId);
        }
    }

    private static bool TryReadAddress(RuntimeActorInstance actor, out nint address)
    {
        address = 0;
        var raw = actor.Address?.Trim() ?? string.Empty;
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

    private static string Short(string runtimeId)
        => runtimeId.Length <= 8 ? runtimeId : runtimeId[..8];

    private sealed record NativeBubbleEntry(string RuntimeId, nint CharacterAddress, string Text, DateTime ExpiresAtUtc, uint Generation);
}
