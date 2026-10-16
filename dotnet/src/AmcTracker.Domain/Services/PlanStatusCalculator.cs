using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Services;

/// <summary>Plan status rules (CLAUDE.md 4.2).</summary>
public static class PlanStatusCalculator
{
    /// <summary>
    /// Renewed wins once a renewal exists. Otherwise: before NotificationDate is Active;
    /// from NotificationDate through EndDate (inclusive) is DueForRenewal; after EndDate is Expired.
    /// </summary>
    public static PlanStatus Resolve(
        DateOnly today, DateOnly notificationDate, DateOnly endDate, bool isRenewed)
    {
        if (isRenewed) return PlanStatus.Renewed;
        if (today < notificationDate) return PlanStatus.Active;
        if (today <= endDate) return PlanStatus.DueForRenewal;
        return PlanStatus.Expired;
    }
}
