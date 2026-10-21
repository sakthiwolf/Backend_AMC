using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Services;

/// <summary>Plan status rules (CLAUDE.md 4.2). Calculated on every read; never stored.</summary>
public static class PlanStatusCalculator
{
    /// <summary>
    /// Renewed once a renewal plan exists. Otherwise: before ReminderDate is Active; from ReminderDate
    /// through EndDate (both inclusive) is DueForRenewal; after EndDate is Expired.
    /// </summary>
    public static PlanStatus Resolve(DateOnly today, DateOnly reminderDate, DateOnly endDate, bool isRenewed)
    {
        if (isRenewed) return PlanStatus.Renewed;
        if (today < reminderDate) return PlanStatus.Active;
        if (today <= endDate) return PlanStatus.DueForRenewal;
        return PlanStatus.Expired;
    }
}
