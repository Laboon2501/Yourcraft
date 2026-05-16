using Yourcraft.Models;
using System.Numerics;

namespace Yourcraft.Services;

public sealed class BgPartCarrierAllocationResult
{
    public List<LayoutProbeInstance> Selected { get; } = [];

    public Dictionary<string, int> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> AcceptedWarnings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<LayoutProbeInstance> AcceptedTop10 { get; } = [];

    public List<LayoutProbeInstance> RejectedTop10 { get; } = [];

    public bool IsOrderValid { get; set; } = true;

    public string OrderValidationMessage { get; set; } = "ok";

    public int TotalSlots { get; set; }

    public int OccupiedCount { get; set; }

    public int ReservedCount { get; set; }

    public int FreeCount { get; set; }

    public int SameModelAvailable { get; set; }

    public int PreferredModifyAvailable { get; set; }

    public int AnyValidAvailable { get; set; }

    public int FallbackAvailable
    {
        get => this.AnyValidAvailable;
        set => this.AnyValidAvailable = value;
    }

    public int TotalAvailable => this.SameModelAvailable + this.PreferredModifyAvailable + this.AnyValidAvailable;

    public void AddReject(string reason)
    {
        var key = NormalizeRejectReason(reason);
        this.Rejected[key] = this.Rejected.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    public void AddAcceptedWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return;

        foreach (var item in warning.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var key = NormalizeWarningReason(item);
            this.AcceptedWarnings[key] = this.AcceptedWarnings.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }

    private static string NormalizeRejectReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Other";
        if (reason.Contains("AlreadyOccupied", StringComparison.OrdinalIgnoreCase) || reason.Contains("Occupied", StringComparison.OrdinalIgnoreCase))
            return "AlreadyOccupied";
        if (reason.Contains("Reserved", StringComparison.OrdinalIgnoreCase))
            return "Reserved";
        if (reason.Contains("template", StringComparison.OrdinalIgnoreCase) || reason.Contains("source", StringComparison.OrdinalIgnoreCase))
            return "TemplateSlot";
        if (reason.Contains("Protected", StringComparison.OrdinalIgnoreCase))
            return "Protected";
        if (reason.Contains("SharedGroupChild", StringComparison.OrdinalIgnoreCase))
            return "SharedGroupChild";
        if (reason.Contains("DynamicControlled", StringComparison.OrdinalIgnoreCase) || reason.Contains("controller", StringComparison.OrdinalIgnoreCase))
            return "DynamicControlled";
        if (reason.Contains("Unsafe", StringComparison.OrdinalIgnoreCase))
            return "UnsafeComplex";
        if (reason.Contains("GraphicsObject", StringComparison.OrdinalIgnoreCase))
            return "InvalidGraphicsObject";
        if (reason.Contains("ModelHandle", StringComparison.OrdinalIgnoreCase) || reason.Contains("ModelResourceHandle", StringComparison.OrdinalIgnoreCase))
            return "InvalidModelHandle";
        if (reason.Contains("Snapshot", StringComparison.OrdinalIgnoreCase))
            return "NoSnapshot";
        return reason;
    }

    private static string NormalizeWarningReason(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return "Other";
        if (warning.Contains("SharedGroupChild", StringComparison.OrdinalIgnoreCase))
            return "SharedGroupChild";
        if (warning.Contains("Dynamic", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("Animated", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("controller", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("UnsafeComplexModel", StringComparison.OrdinalIgnoreCase))
        {
            return "DynamicSuspected";
        }

        return warning;
    }
}

public sealed class BgPartCarrierAllocator
{
    public BgPartCarrierAllocationResult AllocateCarriers(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int requestedCount,
        CarrierAllocationPolicy policy,
        Vector3? playerPosition,
        Func<LayoutProbeInstance?, ISet<string>, string> getBasicRejectReason,
        Func<LayoutProbeInstance, string> getPreferredReason,
        Func<LayoutProbeInstance, string> getWarningReason,
        Func<string, bool> isOccupied,
        Func<string, bool> isReserved)
    {
        requestedCount = Math.Max(0, requestedCount);

        var result = new BgPartCarrierAllocationResult();
        var excluded = template == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { template.Address };

        var bgParts = candidateSlots
            .Where(slot => string.Equals(slot.Type, "BgPart", StringComparison.Ordinal))
            .ToList();
        result.TotalSlots = bgParts.Count;
        result.OccupiedCount = bgParts.Count(slot => isOccupied(slot.Address));
        result.ReservedCount = bgParts.Count(slot => isReserved(slot.Address));
        result.FreeCount = Math.Max(0, result.TotalSlots - result.OccupiedCount - result.ReservedCount);

        var sameModel = new List<LayoutProbeInstance>();
        var preferredSlot = new List<LayoutProbeInstance>();
        var preferredResourcePath = new List<LayoutProbeInstance>();
        var anyValid = new List<LayoutProbeInstance>();
        var accepted = new List<LayoutProbeInstance>();
        foreach (var slot in bgParts)
        {
            slot.CarrierRejectReason = string.Empty;
            slot.CarrierWarningReason = string.Empty;
            slot.CarrierAllocationStage = string.Empty;

            var basicReject = getBasicRejectReason(slot, excluded);
            if (!string.IsNullOrWhiteSpace(basicReject))
            {
                result.AddReject(basicReject);
                slot.CarrierRejectReason = basicReject;
                continue;
            }

            if (template != null && string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase))
            {
                sameModel.Add(slot);
                accepted.Add(slot);
                var warning = getWarningReason(slot);
                slot.CarrierWarningReason = warning;
                slot.CarrierAllocationStage = "SameModel";
                result.AddAcceptedWarning(warning);
                slot.CarrierRejectReason = string.Empty;
                continue;
            }

            var preferredReason = getPreferredReason(slot);
            if (!string.IsNullOrWhiteSpace(preferredReason))
            {
                if (preferredReason.Contains("PreferredSlot", StringComparison.OrdinalIgnoreCase))
                    preferredSlot.Add(slot);
                else
                    preferredResourcePath.Add(slot);
                slot.CarrierAllocationStage = "PreferredModifyList";
            }
            else
            {
                anyValid.Add(slot);
                slot.CarrierAllocationStage = "AnyValidBgPart";
            }

            accepted.Add(slot);
            var fallbackWarning = getWarningReason(slot);
            slot.CarrierWarningReason = fallbackWarning;
            result.AddAcceptedWarning(fallbackWarning);
            slot.CarrierRejectReason = string.Empty;
        }

        result.SameModelAvailable = sameModel.Count;
        result.PreferredModifyAvailable = preferredSlot.Count + preferredResourcePath.Count;
        result.AnyValidAvailable = anyValid.Count;
        result.AcceptedTop10.AddRange(SortByFarthestFromPlayer(accepted, playerPosition).Take(10));
        result.RejectedTop10.AddRange(SortRejectedByFarthestFromPlayer(bgParts, playerPosition)
            .Where(slot => !string.IsNullOrWhiteSpace(slot.CarrierRejectReason))
            .Take(10));

        var sortedSameModel = SortByFarthestFromPlayer(sameModel, playerPosition).ToList();
        var sortedPreferredSlot = SortByFarthestFromPlayer(preferredSlot, playerPosition).ToList();
        var sortedPreferredResourcePath = SortByFarthestFromPlayer(preferredResourcePath, playerPosition).ToList();
        var sortedAnyValid = SortByFarthestFromPlayer(anyValid, playerPosition).ToList();
        result.Selected.AddRange(sortedSameModel);
        result.Selected.AddRange(sortedPreferredSlot);
        result.Selected.AddRange(sortedPreferredResourcePath);
        result.Selected.AddRange(sortedAnyValid);

        if (requestedCount < result.Selected.Count)
            result.Selected.RemoveRange(requestedCount, result.Selected.Count - requestedCount);

        result.IsOrderValid = ValidateOrder(result.Selected, sortedSameModel, sortedPreferredSlot, sortedPreferredResourcePath, sortedAnyValid, playerPosition, out var orderError);
        result.OrderValidationMessage = orderError;

        return result;
    }

    public static IOrderedEnumerable<LayoutProbeInstance> SortByFarthestFromPlayer(IEnumerable<LayoutProbeInstance> slots, Vector3? playerPosition)
        => slots
            .OrderByDescending(slot => GetDistance(slot, playerPosition))
            .ThenByDescending(slot => !slot.Visible)
            .ThenBy(slot => slot.Address);

    private static IOrderedEnumerable<LayoutProbeInstance> SortRejectedByFarthestFromPlayer(IEnumerable<LayoutProbeInstance> slots, Vector3? playerPosition)
        => slots
            .OrderByDescending(slot => GetDistance(slot, playerPosition))
            .ThenBy(slot => slot.Address);

    public static float GetDistance(LayoutProbeInstance slot, Vector3? playerPosition)
        => playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, slot.Position) : slot.DistanceToPlayer;

    private static bool ValidateOrder(
        IReadOnlyList<LayoutProbeInstance> selected,
        IReadOnlyList<LayoutProbeInstance> sameModel,
        IReadOnlyList<LayoutProbeInstance> preferredSlot,
        IReadOnlyList<LayoutProbeInstance> preferredResourcePath,
        IReadOnlyList<LayoutProbeInstance> anyValid,
        Vector3? playerPosition,
        out string message)
    {
        message = "ok";
        if (selected.Count == 0)
            return true;

        var expected = sameModel.Count > 0
            ? sameModel[0]
            : preferredSlot.Count > 0
                ? preferredSlot[0]
                : preferredResourcePath.Count > 0
                    ? preferredResourcePath[0]
                    : anyValid.FirstOrDefault();

        if (expected == null || string.Equals(expected.Address, selected[0].Address, StringComparison.OrdinalIgnoreCase))
            return true;

        message = $"Allocator order violation: selected slot is not farthest. selected={selected[0].Address} distance={GetDistance(selected[0], playerPosition):F1}; expected={expected.Address} distance={GetDistance(expected, playerPosition):F1}";
        return false;
    }
}
