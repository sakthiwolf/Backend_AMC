namespace AmcTracker.Domain.Services;

/// <summary>
/// Plan date rules (CLAUDE.md 4.2, 4.3). Calendar-month arithmetic, never 30-day steps.
/// Both dates are always derived from the start date, so clamping never accumulates
/// (31 Jan + 1 month = 28/29 Feb; 29 Feb 2028 + 12 months = 28 Feb 2029).
/// </summary>
public static class PlanDateCalculator
{
    public const int PlanLengthMonths = 12;
    public const int NotificationOffsetMonths = 11;

    /// <summary>StartDate + 12 months, clamped to the last day of the month.</summary>
    public static DateOnly EndDate(DateOnly startDate) => startDate.AddMonths(PlanLengthMonths);

    /// <summary>StartDate + 11 months (one month before the end), clamped likewise.</summary>
    public static DateOnly NotificationDate(DateOnly startDate) =>
        startDate.AddMonths(NotificationOffsetMonths);

    /// <summary>Default start of a renewal: the day after the previous plan ends.</summary>
    public static DateOnly DefaultRenewalStart(DateOnly previousEndDate) =>
        previousEndDate.AddDays(1);
}
