using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Services;

/// <summary>A reminder that should go out: when it is scheduled and the date it tells the customer about.</summary>
/// <param name="ScheduledDate">Identifies the reminder (one row per plan and date).</param>
/// <param name="DueOn">The date named in the message: the AMC end date, or the monthly due date.</param>
public readonly record struct ReminderDue(DateOnly ScheduledDate, DateOnly DueOn);

/// <summary>
/// When WhatsApp reminders go out for a plan.
/// <list type="bullet">
/// <item><b>Yearly (AMC)</b>: once, one month before the plan ends (StartDate + 11 months).</item>
/// <item><b>Monthly</b>: every month, 7 days before that month's due date. The due dates are
/// StartDate + 1 month, + 2 months ... + 12 months, so the last one is the plan's EndDate.</item>
/// </list>
/// Calendar months are always counted from StartDate, so clamping (31 Jan + 1 month = 28 Feb)
/// never accumulates.
/// </summary>
public static class ReminderSchedule
{
    public const int MonthlyLeadDays = 7;

    /// <summary>Due dates of a monthly plan: StartDate + 1 .. 12 months.</summary>
    public static IEnumerable<DateOnly> MonthlyDueDates(DateOnly startDate) =>
        Enumerable.Range(1, PlanDateCalculator.PlanLengthMonths).Select(startDate.AddMonths);

    /// <summary>The reminder date for a monthly due date.</summary>
    public static DateOnly MonthlyReminderDate(DateOnly dueDate) => dueDate.AddDays(-MonthlyLeadDays);

    /// <summary>
    /// The next reminder date from <paramref name="today"/> on (today counts), or null when none is left.
    /// Yearly: the single reminder a month before the end. Monthly: the next of the twelve monthly ones.
    /// </summary>
    public static DateOnly? NextScheduled(Plan plan, DateOnly today)
    {
        if (plan.PlanType == PlanType.AMC)
            return plan.NotificationDate >= today ? plan.NotificationDate : null;
        foreach (var due in MonthlyDueDates(plan.StartDate))
        {
            var scheduled = MonthlyReminderDate(due);
            if (scheduled >= today) return scheduled;
        }
        return null;
    }

    /// <summary>
    /// The reminder that is open on <paramref name="today"/>, or null. A reminder stays open from its
    /// scheduled date until the date it is about, so a failed send is retried on later runs.
    /// </summary>
    public static ReminderDue? OpenOn(Plan plan, DateOnly today)
    {
        if (plan.PlanType == PlanType.AMC)
        {
            return plan.NotificationDate <= today && today <= plan.EndDate
                ? new ReminderDue(plan.NotificationDate, plan.EndDate)
                : null;
        }

        // Latest monthly reminder that has started and whose due date has not passed.
        ReminderDue? open = null;
        foreach (var due in MonthlyDueDates(plan.StartDate))
        {
            var scheduled = MonthlyReminderDate(due);
            if (scheduled <= today && today <= due) open = new ReminderDue(scheduled, due);
        }
        return open;
    }
}
