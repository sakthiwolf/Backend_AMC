using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

public class PlanDateCalculatorTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(2026, 10, 12, 2027, 10, 12)] // CLAUDE.md example: ends 12 Oct 2027
    [InlineData(2026, 1, 31, 2027, 1, 31)]
    [InlineData(2026, 3, 1, 2027, 3, 1)]
    [InlineData(2026, 12, 31, 2027, 12, 31)]
    public void EndDate_is_start_plus_12_calendar_months(int sy, int sm, int sd, int ey, int em, int ed) =>
        Assert.Equal(D(ey, em, ed), PlanDateCalculator.EndDate(D(sy, sm, sd)));

    [Theory]
    [InlineData(2026, 10, 12, 2027, 9, 12)] // reminder on 12 Sep 2027
    [InlineData(2026, 1, 1, 2026, 12, 1)]
    [InlineData(2026, 1, 31, 2026, 12, 31)]
    [InlineData(2026, 12, 15, 2027, 11, 15)] // crosses a year boundary
    public void NotificationDate_is_start_plus_11_calendar_months(int sy, int sm, int sd, int ny, int nm, int nd) =>
        Assert.Equal(D(ny, nm, nd), PlanDateCalculator.NotificationDate(D(sy, sm, sd)));

    [Theory]
    [InlineData(2026, 3, 31, 2027, 2, 28)] // 31 Mar + 11 months: Feb 2027 has 28 days
    [InlineData(2026, 4, 30, 2027, 3, 30)]
    [InlineData(2027, 3, 31, 2028, 2, 29)] // Feb 2028 is a leap February
    [InlineData(2026, 8, 31, 2027, 7, 31)]
    [InlineData(2026, 7, 31, 2027, 6, 30)] // 31 Jul + 11 months clamps to 30 Jun
    public void NotificationDate_clamps_to_month_end(int sy, int sm, int sd, int ny, int nm, int nd) =>
        Assert.Equal(D(ny, nm, nd), PlanDateCalculator.NotificationDate(D(sy, sm, sd)));

    [Fact]
    public void Leap_day_start_ends_on_28_Feb_of_a_non_leap_year()
    {
        var start = D(2028, 2, 29);
        Assert.Equal(D(2029, 2, 28), PlanDateCalculator.EndDate(start));
        Assert.Equal(D(2029, 1, 29), PlanDateCalculator.NotificationDate(start));
    }

    [Fact]
    public void Leap_year_end_dates_keep_29_Feb_when_it_exists()
    {
        Assert.Equal(D(2028, 2, 28), PlanDateCalculator.EndDate(D(2027, 2, 28))); // not clamped up to the 29th
        Assert.Equal(D(2028, 2, 29), PlanDateCalculator.NotificationDate(D(2027, 3, 29)));
    }

    [Fact]
    public void Dates_are_not_30_day_steps()
    {
        var start = D(2026, 1, 15);
        Assert.NotEqual(start.AddDays(360), PlanDateCalculator.EndDate(start));
        Assert.Equal(D(2027, 1, 15), PlanDateCalculator.EndDate(start));
    }

    [Fact]
    public void Notification_is_always_before_end_and_after_start()
    {
        for (var day = D(2027, 12, 25); day <= D(2028, 3, 5); day = day.AddDays(1))
        {
            var n = PlanDateCalculator.NotificationDate(day);
            Assert.True(day < n && n < PlanDateCalculator.EndDate(day), day.ToString());
        }
    }

    [Fact]
    public void Renewal_starts_the_day_after_the_previous_end() =>
        Assert.Equal(D(2027, 10, 13), PlanDateCalculator.DefaultRenewalStart(D(2027, 10, 12)));
}
