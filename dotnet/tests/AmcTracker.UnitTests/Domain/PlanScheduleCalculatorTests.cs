using System.Text.Json;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

public class PlanScheduleCalculatorTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Amc_is_one_year_with_the_reminder_one_month_before_the_end()
    {
        var s = PlanScheduleCalculator.Calculate(PlanType.AMC, D(2026, 10, 12));
        Assert.Equal(D(2027, 10, 12), s.EndDate);
        Assert.Equal(D(2027, 9, 12), s.ReminderDate);
    }

    [Fact]
    public void Monthly_is_one_month_with_the_reminder_seven_days_before_the_end()
    {
        var s = PlanScheduleCalculator.Calculate(PlanType.Monthly, D(2026, 10, 12));
        Assert.Equal(D(2026, 11, 12), s.EndDate);
        Assert.Equal(D(2026, 11, 5), s.ReminderDate);
    }

    [Fact]
    public void Monthly_from_the_31st_clamps_to_the_end_of_February()
    {
        var s = PlanScheduleCalculator.Calculate(PlanType.Monthly, D(2027, 1, 31));
        Assert.Equal(D(2027, 2, 28), s.EndDate);
        Assert.Equal(D(2027, 2, 21), s.ReminderDate);
    }

    [Fact]
    public void Monthly_from_the_31st_in_a_leap_year_ends_on_29_February()
    {
        var s = PlanScheduleCalculator.Calculate(PlanType.Monthly, D(2028, 1, 31));
        Assert.Equal(D(2028, 2, 29), s.EndDate);
        Assert.Equal(D(2028, 2, 22), s.ReminderDate);
    }

    [Fact]
    public void Amc_from_29_February_ends_on_28_February_the_next_year()
    {
        var s = PlanScheduleCalculator.Calculate(PlanType.AMC, D(2028, 2, 29));
        Assert.Equal(D(2029, 2, 28), s.EndDate);
        Assert.Equal(D(2029, 1, 28), s.ReminderDate);
    }

    [Fact]
    public void Amc_reminder_clamps_when_the_month_before_is_shorter()
    {
        // Ends 31 Mar 2028; one month earlier is 29 Feb 2028 (leap year).
        var s = PlanScheduleCalculator.Calculate(PlanType.AMC, D(2027, 3, 31));
        Assert.Equal(D(2028, 3, 31), s.EndDate);
        Assert.Equal(D(2028, 2, 29), s.ReminderDate);
    }

    // Renewal start = the later of (previous end + 1 day) and the payment date.
    [Theory]
    [InlineData("2027-10-15", "2027-10-12", "2027-10-15")] // paid late: the payment date
    [InlineData("2027-10-01", "2027-10-12", "2027-10-13")] // paid early: the day after the end
    [InlineData("2027-10-12", "2027-10-12", "2027-10-13")] // paid on the end date: the day after
    [InlineData("2027-10-13", "2027-10-12", "2027-10-13")] // paid the day after: same day
    [InlineData("2028-03-01", "2028-02-29", "2028-03-01")] // leap day end
    public void Renewal_starts_on_the_later_of_the_day_after_the_end_and_the_payment_date(
        string paidOn, string previousEnd, string expected) =>
        Assert.Equal(DateOnly.Parse(expected),
            PlanScheduleCalculator.RenewalStart(DateOnly.Parse(previousEnd), DateOnly.Parse(paidOn)));

    [Fact]
    public void Unknown_type_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanScheduleCalculator.Calculate((PlanType)99, D(2026, 1, 1)));

    /// <summary>Every start date for two years: start &lt; reminder &lt; end always holds (the CK_Plans_Dates check).</summary>
    [Theory]
    [InlineData(PlanType.AMC)]
    [InlineData(PlanType.Monthly)]
    public void Reminder_always_falls_strictly_between_start_and_end(PlanType type)
    {
        for (var start = D(2027, 1, 1); start <= D(2028, 12, 31); start = start.AddDays(1))
        {
            var s = PlanScheduleCalculator.Calculate(type, start);
            Assert.True(start < s.ReminderDate && s.ReminderDate < s.EndDate, $"{type} from {start}");
        }
    }

    public static TheoryData<string, string, string, string> SharedCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plan_schedule_cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var data = new TheoryData<string, string, string, string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("type").GetString()!, c.GetProperty("start").GetString()!,
                c.GetProperty("end").GetString()!, c.GetProperty("reminder").GetString()!);
        }
        return data;
    }

    /// <summary>The same file is checked by the app (test/core/plan_dates_test.dart).</summary>
    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Matches_the_cases_shared_with_the_app(string type, string start, string end, string reminder)
    {
        var s = PlanScheduleCalculator.Calculate(Enum.Parse<PlanType>(type), DateOnly.Parse(start));
        Assert.Equal(DateOnly.Parse(end), s.EndDate);
        Assert.Equal(DateOnly.Parse(reminder), s.ReminderDate);
    }
}
