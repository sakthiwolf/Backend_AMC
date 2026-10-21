using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Services;

/// <summary>When a plan ends and when its WhatsApp reminder goes out.</summary>
/// <param name="EndDate">The last day of the plan.</param>
/// <param name="ReminderDate">The day the reminder is sent.</param>
public readonly record struct PlanSchedule(DateOnly EndDate, DateOnly ReminderDate);

/// <summary>
/// Plan-type rules (CLAUDE.md 4.2, 4.3). The duration comes from the type, never from staff:
/// <list type="bullet">
/// <item><b>AMC</b>: 1 year. EndDate = StartDate + 1 year; reminder 1 month before EndDate.</item>
/// <item><b>Monthly</b>: 1 month. EndDate = StartDate + 1 month; reminder 7 days before EndDate.</item>
/// </list>
/// Calendar arithmetic only (<see cref="DateOnly.AddYears"/> / <see cref="DateOnly.AddMonths"/>), which clamps
/// to the last valid day: Monthly 31 Jan 2027 ends 28 Feb 2027; AMC 29 Feb 2028 ends 28 Feb 2029.
/// Pure: no clock, no database.
/// </summary>
public static class PlanScheduleCalculator
{
    public const int AmcReminderLeadMonths = 1;
    public const int MonthlyReminderLeadDays = 7;

    public static PlanSchedule Calculate(PlanType type, DateOnly startDate) => type switch
    {
        PlanType.AMC => AmcSchedule(startDate.AddYears(1)),
        PlanType.Monthly => MonthlySchedule(startDate.AddMonths(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown plan type."),
    };

    /// <summary>
    /// Start of the next cycle (CLAUDE.md 4.4): the later of the day after the previous plan ends and the
    /// payment date. Paid late, the new cycle starts on the payment date; paid early or on the end date,
    /// it starts the day after the previous end.
    /// </summary>
    public static DateOnly RenewalStart(DateOnly previousEndDate, DateOnly paidOn)
    {
        var dayAfterEnd = previousEndDate.AddDays(1);
        return paidOn > dayAfterEnd ? paidOn : dayAfterEnd;
    }

    private static PlanSchedule AmcSchedule(DateOnly end) => new(end, end.AddMonths(-AmcReminderLeadMonths));

    private static PlanSchedule MonthlySchedule(DateOnly end) => new(end, end.AddDays(-MonthlyReminderLeadDays));
}
