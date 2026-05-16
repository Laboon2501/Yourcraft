using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class PreferredModifyBgPartRegistry
{
    private const float PositionEpsilon = 0.25f;

    private readonly Configuration configuration;
    private readonly Func<uint> getTerritoryType;
    private readonly Action save;

    public PreferredModifyBgPartRegistry(Configuration configuration, Func<uint> getTerritoryType, Action save)
    {
        this.configuration = configuration;
        this.getTerritoryType = getTerritoryType;
        this.save = save;
    }

    public IReadOnlyList<PreferredModifyBgPartSlot> PreferredSlots => this.configuration.PreferredModifyBgPartSlots;

    public IReadOnlyList<PreferredModifyBgPartResourcePath> PreferredResourcePaths => this.configuration.PreferredModifyBgPartResourcePaths;

    public bool IsPreferred(LayoutProbeInstance? slot, out string reason)
    {
        reason = string.Empty;
        if (slot == null)
            return false;

        var territory = this.getTerritoryType();
        var slotMatch = this.configuration.PreferredModifyBgPartSlots.FirstOrDefault(item => this.IsSlotMatch(item, slot, territory));
        if (slotMatch != null)
        {
            reason = $"PreferredSlot: {slotMatch.ResourcePath}";
            return true;
        }

        var resourceMatch = this.configuration.PreferredModifyBgPartResourcePaths.FirstOrDefault(item =>
            string.Equals(item.ResourcePath, slot.ResourcePath, StringComparison.OrdinalIgnoreCase)
            && (!item.AppliesToCurrentTerritoryOnly || item.TerritoryType == territory));
        if (resourceMatch != null)
        {
            reason = $"PreferredResourcePath: {resourceMatch.ResourcePath}";
            return true;
        }

        return false;
    }

    public bool IsPreferredSlot(LayoutProbeInstance? slot)
    {
        if (slot == null)
            return false;

        var territory = this.getTerritoryType();
        return this.configuration.PreferredModifyBgPartSlots.Any(item => this.IsSlotMatch(item, slot, territory));
    }

    public bool ProtectSlot(LayoutProbeInstance? slot, string note = "")
    {
        if (slot == null)
            return false;
        if (this.IsPreferredSlot(slot))
            return false;

        this.configuration.PreferredModifyBgPartSlots.Add(new PreferredModifyBgPartSlot
        {
            TerritoryType = this.getTerritoryType(),
            SourceType = FirstNonEmpty(slot.SourceKind, "Unknown"),
            ResourcePath = slot.ResourcePath,
            OriginalPosition = ToVector3Data(slot.Position),
            OriginalRotation = slot.Rotation,
            OriginalScale = ToVector3Data(slot.Scale),
            LayoutInstanceAddress = slot.Address,
            SharedGroupPath = slot.SharedGroupPath,
            ChildIndex = slot.ChildIndex,
            StableKey = FirstNonEmpty(slot.Key, slot.ParentKey, slot.Address),
            Note = note,
        });
        this.save();
        return true;
    }

    public int UnprotectSlot(LayoutProbeInstance? slot)
    {
        if (slot == null)
            return 0;

        var territory = this.getTerritoryType();
        var removed = this.configuration.PreferredModifyBgPartSlots.RemoveAll(item => this.IsSlotMatch(item, slot, territory));
        if (removed > 0)
            this.save();
        return removed;
    }

    public bool ProtectResourcePath(string? resourcePath, bool currentTerritoryOnly = true, string note = "")
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
            return false;

        var territory = currentTerritoryOnly ? this.getTerritoryType() : 0u;
        if (this.configuration.PreferredModifyBgPartResourcePaths.Any(item =>
            string.Equals(item.ResourcePath, resourcePath, StringComparison.OrdinalIgnoreCase)
            && item.AppliesToCurrentTerritoryOnly == currentTerritoryOnly
            && item.TerritoryType == territory))
        {
            return false;
        }

        this.configuration.PreferredModifyBgPartResourcePaths.Add(new PreferredModifyBgPartResourcePath
        {
            ResourcePath = resourcePath.Trim(),
            TerritoryType = territory,
            AppliesToCurrentTerritoryOnly = currentTerritoryOnly,
            Note = note,
        });
        this.save();
        return true;
    }

    public int UnprotectResourcePath(string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
            return 0;

        var territory = this.getTerritoryType();
        var removed = this.configuration.PreferredModifyBgPartResourcePaths.RemoveAll(item =>
            string.Equals(item.ResourcePath, resourcePath, StringComparison.OrdinalIgnoreCase)
            && (!item.AppliesToCurrentTerritoryOnly || item.TerritoryType == territory));
        if (removed > 0)
            this.save();
        return removed;
    }

    public bool RemoveSlotEntry(PreferredModifyBgPartSlot? slot)
    {
        if (slot == null)
            return false;

        var removed = this.configuration.PreferredModifyBgPartSlots.Remove(slot);
        if (removed)
            this.save();
        return removed;
    }

    public bool RemoveResourcePathEntry(PreferredModifyBgPartResourcePath? resourcePath)
    {
        if (resourcePath == null)
            return false;

        var removed = this.configuration.PreferredModifyBgPartResourcePaths.Remove(resourcePath);
        if (removed)
            this.save();
        return removed;
    }

    public void Clear()
    {
        this.configuration.PreferredModifyBgPartSlots.Clear();
        this.configuration.PreferredModifyBgPartResourcePaths.Clear();
        this.save();
    }

    private bool IsSlotMatch(PreferredModifyBgPartSlot item, LayoutProbeInstance slot, uint territory)
    {
        if (item.TerritoryType != 0 && item.TerritoryType != territory)
            return false;
        if (!string.Equals(item.ResourcePath, slot.ResourcePath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(item.SourceType, slot.SourceKind, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(item.SharedGroupPath) && !string.Equals(item.SharedGroupPath, slot.SharedGroupPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.ChildIndex >= 0 && item.ChildIndex != slot.ChildIndex)
            return false;

        var savedPosition = ToVector3(item.OriginalPosition);
        return Vector3.Distance(savedPosition, slot.Position) <= PositionEpsilon
            || (!string.IsNullOrWhiteSpace(item.LayoutInstanceAddress) && string.Equals(item.LayoutInstanceAddress, slot.Address, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(item.StableKey) && (string.Equals(item.StableKey, slot.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(item.StableKey, slot.ParentKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static Vector3Data ToVector3Data(Vector3 vector)
        => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

    private static Vector3 ToVector3(Vector3Data data)
        => new(data.X, data.Y, data.Z);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
