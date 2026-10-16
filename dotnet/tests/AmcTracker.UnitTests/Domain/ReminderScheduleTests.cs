using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

public class ReminderScheduleTests
{
    private static Plan PlanOf(PlanType type, DateOnly start) =>
        Plan.Create(1, type, 100m, start, start);

    [Fact]
    public void Yearly_reminder_is_one_month_before_the_end_date()
    {
        var plan = PlanOf(PlanType.AMC, new DateOnly(2026, 10, 12));
        Assert.Equal(new DateOnly(2027, 10, 12), plan.EndDate);

        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2027, 9, 11)));
        var due = ReminderSchedule.OpenOn(plan, new DateOnly(2027, 9, 12));
        Assert.Equal(new DateOnly(2027, 9, 12), due!.Value.ScheduledDate);
        Assert.Equal(new DateOnly(2027, 10, 12), due.Value.DueOn); // the message names the AMC end date
        Assert.NotNull(ReminderSchedule.OpenOn(plan, new DateOnly(2027, 10, 12))); // still open on the end date (retries)
        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2027, 10, 13)));
    }

    [Fact]
    public void Yearly_reminder_never_fires_during_the_year()
    {
        var plan = PlanOf(PlanType.AMC, new DateOnly(2026, 10, 12));
        for (var d = new DateOnly(2026, 10, 12); d < new DateOnly(2027, 9, 12); d = d.AddDays(1))
            Assert.Null(ReminderSchedule.OpenOn(plan, d));
    }

    [Fact]
    public void Monthly_reminder_is_seven_days_before_each_monthly_due_date()
    {
        var plan = PlanOf(PlanType.Monthly, new DateOnly(2026, 10, 12));

        // First due date is 12 Nov 2026, so the first reminder is 5 Nov.
        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2026, 11, 4)));
        var first = ReminderSchedule.OpenOn(plan, new DateOnly(2026, 11, 5));
        Assert.Equal(new DateOnly(2026, 11, 5), first!.Value.ScheduledDate);
        Assert.Equal(new DateOnly(2026, 11, 12), first.Value.DueOn);

        // Open until the due date itself, then quiet until the next cycle.
        Assert.NotNull(ReminderSchedule.OpenOn(plan, new DateOnly(2026, 11, 12)));
        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2026, 11, 13)));

        // Second cycle: due 12 Dec, reminder 5 Dec.
        var second = ReminderSchedule.OpenOn(plan, new DateOnly(2026, 12, 5));
        Assert.Equal(new DateOnly(2026, 12, 12), second!.Value.DueOn);
    }

    [Fact]
    public void Monthly_plan_has_twelve_reminders_and_the_last_one_is_for_the_end_date()
    {
        var plan = PlanOf(PlanType.Monthly, new DateOnly(2026, 10, 12));
        var dues = ReminderSchedule.MonthlyDueDates(plan.StartDate).ToList();
        Assert.Equal(12, dues.Count);
        Assert.Equal(new DateOnly(2026, 11, 12), dues.First());
        Assert.Equal(plan.EndDate, dues.Last());

        var opened = new List<DateOnly>();
        for (var d = plan.StartDate; d <= plan.EndDate.AddDays(10); d = d.AddDays(1))
            if (ReminderSchedule.OpenOn(plan, d) is { } r && !opened.Contains(r.ScheduledDate))
                opened.Add(r.ScheduledDate);
        Assert.Equal(12, opened.Count);
        Assert.Equal(dues.Select(ReminderSchedule.MonthlyReminderDate), opened);
    }

    [Fact]
    public void Monthly_due_dates_clamp_to_month_end_without_drifting()
    {
        // Started on the 31st: Feb is clamped, but March goes back to the 31st.
        var dues = ReminderSchedule.MonthlyDueDates(new DateOnly(2027, 1, 31)).ToList();
        Assert.Equal(new DateOnly(2027, 2, 28), dues[0]);
        Assert.Equal(new DateOnly(2027, 3, 31), dues[1]);
        Assert.Equal(new DateOnly(2027, 4, 30), dues[2]);
        Assert.Equal(new DateOnly(2028, 1, 31), dues[11]);

        var leap = ReminderSchedule.MonthlyDueDates(new DateOnly(2028, 1, 31)).First();
        Assert.Equal(new DateOnly(2028, 2, 29), leap);
        Assert.Equal(new DateOnly(2028, 2, 22), ReminderSchedule.MonthlyReminderDate(leap));
    }

    [Fact]
    public void A_monthly_plan_stays_quiet_before_it_starts_and_after_it_ends()
    {
        var plan = PlanOf(PlanType.Monthly, new DateOnly(2026, 10, 12));
        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2026, 10, 12)));
        Assert.Null(ReminderSchedule.OpenOn(plan, new DateOnly(2027, 10, 13)));
    }
}
