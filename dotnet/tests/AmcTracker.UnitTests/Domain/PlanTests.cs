using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

public class PlanStatusCalculatorTests
{
    private static readonly DateOnly Reminder = new(2027, 9, 12);
    private static readonly DateOnly End = new(2027, 10, 12);

    [Theory]
    [InlineData(2026, 10, 12, PlanStatus.Active)]
    [InlineData(2027, 9, 11, PlanStatus.Active)]          // the day before ReminderDate
    [InlineData(2027, 9, 12, PlanStatus.DueForRenewal)]   // exactly ReminderDate
    [InlineData(2027, 10, 1, PlanStatus.DueForRenewal)]
    [InlineData(2027, 10, 12, PlanStatus.DueForRenewal)]  // exactly EndDate is still due
    [InlineData(2027, 10, 13, PlanStatus.Expired)]        // the day after EndDate
    [InlineData(2030, 1, 1, PlanStatus.Expired)]
    public void Resolves_status_from_dates(int y, int m, int d, PlanStatus expected) =>
        Assert.Equal(expected, PlanStatusCalculator.Resolve(new DateOnly(y, m, d), Reminder, End, isRenewed: false));

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2027, 9, 20)]
    [InlineData(2030, 1, 1)]
    public void Renewed_wins_on_any_date(int y, int m, int d) =>
        Assert.Equal(PlanStatus.Renewed, PlanStatusCalculator.Resolve(new DateOnly(y, m, d), Reminder, End, isRenewed: true));
}

public class PlanTests
{
    [Fact]
    public void Create_takes_its_dates_from_the_type()
    {
        var amc = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2026, 10, 12));
        Assert.Equal((new DateOnly(2027, 10, 12), new DateOnly(2027, 9, 12)), (amc.EndDate, amc.ReminderDate));
        Assert.Null(amc.PreviousPlanId);

        var monthly = Plan.Create(1, PlanType.Monthly, 500m, new DateOnly(2026, 10, 12));
        Assert.Equal((new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 5)), (monthly.EndDate, monthly.ReminderDate));
    }

    /// <summary>Status on read, with the "today" a clock would inject, for each of the four states.</summary>
    [Fact]
    public void Status_is_calculated_on_read_for_every_state()
    {
        var plan = Plan.Create(1, PlanType.Monthly, 500m, new DateOnly(2026, 10, 12)); // ends 12 Nov, reminder 5 Nov
        Assert.Equal(PlanStatus.Active, plan.StatusOn(new DateOnly(2026, 11, 4)));
        Assert.Equal(PlanStatus.DueForRenewal, plan.StatusOn(new DateOnly(2026, 11, 5)));
        Assert.Equal(PlanStatus.DueForRenewal, plan.StatusOn(new DateOnly(2026, 11, 12)));
        Assert.Equal(PlanStatus.Expired, plan.StatusOn(new DateOnly(2026, 11, 13)));

        plan.PlanId = 3;
        plan.Renew(new DateOnly(2026, 11, 13));
        Assert.Equal(PlanStatus.Renewed, plan.StatusOn(new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public void Renew_copies_the_type_and_amount_starts_where_told_and_links_back()
    {
        var old = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2025, 10, 12));
        old.PlanId = 7;

        var renewal = old.Renew(new DateOnly(2026, 10, 13));

        Assert.True(old.IsRenewed);
        Assert.Same(renewal, old.Renewal);
        Assert.Equal(7, renewal.PreviousPlanId);
        Assert.Equal(PlanType.AMC, renewal.PlanType);
        Assert.Equal(12000m, renewal.Amount);
        Assert.Equal(new DateOnly(2026, 10, 13), renewal.StartDate);
        Assert.Equal(new DateOnly(2027, 10, 13), renewal.EndDate);
        Assert.Equal(new DateOnly(2027, 9, 13), renewal.ReminderDate);
        // The old plan itself is untouched.
        Assert.Equal((new DateOnly(2025, 10, 12), new DateOnly(2026, 10, 12)), (old.StartDate, old.EndDate));
    }

    [Fact]
    public void Renew_takes_a_plan_amount_when_given()
    {
        var old = Plan.Create(1, PlanType.Monthly, 500m, new DateOnly(2026, 10, 12));
        var renewal = old.Renew(new DateOnly(2026, 12, 1), 650m);
        Assert.Equal(new DateOnly(2026, 12, 1), renewal.StartDate);
        Assert.Equal(new DateOnly(2027, 1, 1), renewal.EndDate);
        Assert.Equal(650m, renewal.Amount);
    }

    [Fact]
    public void A_plan_can_be_renewed_only_once()
    {
        var old = Plan.Create(1, PlanType.AMC, 1m, new DateOnly(2025, 1, 1));
        old.Renew(new DateOnly(2025, 1, 2));
        Assert.Throws<InvalidOperationException>(() => old.Renew(new DateOnly(2025, 1, 2)));
    }

    [Fact]
    public void Reschedule_recalculates_from_the_new_type_and_start()
    {
        var plan = Plan.Create(1, PlanType.AMC, 1m, new DateOnly(2026, 1, 31));
        plan.Reschedule(PlanType.Monthly, new DateOnly(2027, 1, 31));
        Assert.Equal(PlanType.Monthly, plan.PlanType);
        Assert.Equal((new DateOnly(2027, 2, 28), new DateOnly(2027, 2, 21)), (plan.EndDate, plan.ReminderDate));
    }

    [Theory]
    [InlineData(ReminderStatus.Failed, 1, false, "2026-11-12", true)]
    [InlineData(ReminderStatus.Failed, 4, false, "2026-11-12", true)]
    [InlineData(ReminderStatus.Failed, 5, false, "2026-11-12", false)]  // out of attempts
    [InlineData(ReminderStatus.Failed, 1, true, "2026-11-12", false)]   // plan renewed
    [InlineData(ReminderStatus.Failed, 1, false, "2026-11-13", false)]  // plan ended
    [InlineData(ReminderStatus.Sent, 1, false, "2026-11-12", false)]
    [InlineData(ReminderStatus.Pending, 0, false, "2026-11-12", false)]
    public void Reminder_can_be_retried_only_while_failed_with_attempts_left_and_the_plan_current(
        ReminderStatus status, int attempts, bool renewed, string today, bool expected)
    {
        var plan = Plan.Create(1, PlanType.Monthly, 1m, new DateOnly(2026, 10, 12)); // ends 12 Nov 2026
        if (renewed) plan.Renew(new DateOnly(2026, 11, 13));
        var reminder = new Reminder { Status = status, Attempts = attempts, Plan = plan };
        Assert.Equal(expected, reminder.CanRetry(DateOnly.Parse(today)));
    }
}
