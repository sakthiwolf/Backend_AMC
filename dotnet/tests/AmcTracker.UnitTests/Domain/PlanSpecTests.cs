using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

/// <summary>
/// PlanSpec is the query form of the status rules; <see cref="Plan.StatusOn"/> is the in-memory form. They must
/// never differ, so every status is compared for each day from ReminderDate - 1 through EndDate + 1, for plans
/// that include a month-end start, a leap-year start, and renewed plans. Any mismatch fails the build.
/// </summary>
public class PlanSpecTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static Plan Open(PlanType type, DateOnly start) => Plan.Create(1, type, 1m, start);

    private static Plan Renewed(PlanType type, DateOnly start)
    {
        var plan = Open(type, start);
        plan.PlanId = 5;
        plan.Renew(PlanScheduleCalculator.RenewalStart(plan.EndDate, plan.EndDate));
        return plan;
    }

    public static TheoryData<string, PlanType, string> Starts => new()
    {
        { "AMC", PlanType.AMC, "2026-10-12" },
        { "AMC starting 31 Mar (reminder lands on 29 Feb)", PlanType.AMC, "2027-03-31" },
        { "AMC starting on a leap day", PlanType.AMC, "2028-02-29" },
        { "Monthly", PlanType.Monthly, "2026-10-12" },
        { "Monthly month-end (31 Jan 2027 ends 28 Feb)", PlanType.Monthly, "2027-01-31" },
        { "Monthly leap year (31 Jan 2028 ends 29 Feb)", PlanType.Monthly, "2028-01-31" },
        { "Monthly across the year end", PlanType.Monthly, "2026-12-31" },
    };

    /// <summary>The days to check: one before the reminder date, through one after the end.</summary>
    private static IEnumerable<DateOnly> Days(Plan plan)
    {
        for (var d = plan.ReminderDate.AddDays(-1); d <= plan.EndDate.AddDays(1); d = d.AddDays(1)) yield return d;
    }

    [Theory]
    [MemberData(nameof(Starts))]
    public void Every_status_matches_StatusOn_on_every_day_around_the_boundaries(string name, PlanType type, string start)
    {
        foreach (var plan in new[] { Open(type, DateOnly.Parse(start)), Renewed(type, DateOnly.Parse(start)) })
        {
            var days = Days(plan).ToList();
            Assert.True(days.Count >= 8, name); // the check covers a real range
            foreach (var day in days)
            {
                var expected = plan.StatusOn(day);
                var where = $"{name}, renewed={plan.IsRenewed}, day {day}: StatusOn says {expected}";

                Assert.Equal(expected, PlanSpec.StatusOf(day).Compile()(plan));
                foreach (var status in Enum.GetValues<PlanStatus>())
                {
                    Assert.True(
                        PlanSpec.HasStatus(status, day).Compile()(plan) == (expected == status),
                        $"{where}, but HasStatus({status}) disagrees");
                }
            }
        }
    }

    [Fact]
    public void The_status_flips_exactly_on_the_boundary_days()
    {
        var plan = Open(PlanType.AMC, D(2026, 10, 12)); // reminder 12 Sep 2027, ends 12 Oct 2027
        PlanStatus At(DateOnly day) => PlanSpec.StatusOf(day).Compile()(plan);
        Assert.Equal(PlanStatus.Active, At(D(2027, 9, 11)));
        Assert.Equal(PlanStatus.DueForRenewal, At(D(2027, 9, 12)));
        Assert.Equal(PlanStatus.DueForRenewal, At(D(2027, 10, 12)));
        Assert.Equal(PlanStatus.Expired, At(D(2027, 10, 13)));
    }

    [Theory]
    [MemberData(nameof(Starts))]
    public void EndsWithin_includes_today_plus_days_and_excludes_the_day_after(string name, PlanType type, string start)
    {
        foreach (var plan in new[] { Open(type, DateOnly.Parse(start)), Renewed(type, DateOnly.Parse(start)) })
        {
            foreach (var days in new[] { 1, 7, 30 })
            {
                foreach (var day in Days(plan))
                {
                    var spec = PlanSpec.EndsWithin(day, days).Compile();
                    var expected = !plan.IsRenewed && plan.EndDate >= day && plan.EndDate <= day.AddDays(days);
                    Assert.True(spec(plan) == expected, $"{name}, renewed={plan.IsRenewed}, day {day}, days {days}");
                }
            }
        }
    }

    [Fact]
    public void Ending_exactly_today_plus_30_counts_and_today_plus_31_does_not()
    {
        var plan = Open(PlanType.Monthly, D(2027, 3, 14)); // ends 14 Apr 2027
        Assert.True(PlanSpec.EndsWithin(D(2027, 3, 15), 30).Compile()(plan));  // today + 30
        Assert.False(PlanSpec.EndsWithin(D(2027, 3, 14), 30).Compile()(plan)); // today + 31
        Assert.True(PlanSpec.EndsWithin(D(2027, 4, 14), 30).Compile()(plan));  // ends today
        Assert.False(PlanSpec.EndsWithin(D(2027, 4, 15), 30).Compile()(plan)); // ended yesterday
    }

    [Theory]
    [MemberData(nameof(Starts))]
    public void NeedsAction_is_due_or_a_reminder_within_the_days_and_never_a_renewed_plan(string name, PlanType type, string start)
    {
        foreach (var plan in new[] { Open(type, DateOnly.Parse(start)), Renewed(type, DateOnly.Parse(start)) })
        {
            foreach (var day in Days(plan))
            {
                var due = plan.StatusOn(day) == PlanStatus.DueForRenewal;
                var reminds = !plan.IsRenewed && plan.ReminderDate >= day && plan.ReminderDate <= day.AddDays(7);
                Assert.True(PlanSpec.NeedsAction(day, 7).Compile()(plan) == (due || reminds),
                    $"{name}, renewed={plan.IsRenewed}, day {day}");
            }
            if (plan.IsRenewed) Assert.All(Days(plan), day => Assert.False(PlanSpec.NeedsAction(day, 90).Compile()(plan)));
        }
    }
}
