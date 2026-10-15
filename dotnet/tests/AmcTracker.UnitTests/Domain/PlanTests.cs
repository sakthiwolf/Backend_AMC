using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.UnitTests.Domain;

public class PlanStatusCalculatorTests
{
    private static readonly DateOnly Notify = new(2027, 9, 12);
    private static readonly DateOnly End = new(2027, 10, 12);

    [Theory]
    [InlineData(2026, 10, 12, PlanStatus.Active)]
    [InlineData(2027, 9, 11, PlanStatus.Active)]          // day before the 11th month
    [InlineData(2027, 9, 12, PlanStatus.DueForRenewal)]   // exactly NotificationDate
    [InlineData(2027, 10, 1, PlanStatus.DueForRenewal)]
    [InlineData(2027, 10, 12, PlanStatus.DueForRenewal)]  // exactly EndDate is still due
    [InlineData(2027, 10, 13, PlanStatus.Expired)]        // day after EndDate
    [InlineData(2030, 1, 1, PlanStatus.Expired)]
    public void Resolves_status_from_dates(int y, int m, int d, PlanStatus expected) =>
        Assert.Equal(expected, PlanStatusCalculator.Resolve(new DateOnly(y, m, d), Notify, End, isRenewed: false));

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2027, 9, 20)]
    [InlineData(2030, 1, 1)]
    public void Renewed_wins_on_any_date(int y, int m, int d) =>
        Assert.Equal(PlanStatus.Renewed, PlanStatusCalculator.Resolve(new DateOnly(y, m, d), Notify, End, isRenewed: true));
}

public class PlanTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    [Fact]
    public void Create_calculates_dates_and_status()
    {
        var plan = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2026, 10, 12), Today);
        Assert.Equal(new DateOnly(2027, 10, 12), plan.EndDate);
        Assert.Equal(new DateOnly(2027, 9, 12), plan.NotificationDate);
        Assert.Equal(PlanStatus.Active, plan.Status);
        Assert.Null(plan.PreviousPlanId);
    }

    [Fact]
    public void Plan_that_started_over_eleven_months_ago_is_due()
    {
        var plan = Plan.Create(1, PlanType.Monthly, 500m, new DateOnly(2025, 11, 15), Today);
        Assert.Equal(PlanStatus.DueForRenewal, plan.Status);
    }

    [Fact]
    public void Renew_links_marks_old_plan_Renewed_and_defaults_to_next_day()
    {
        var old = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2025, 10, 12), Today);
        old.PlanId = 7;

        var renewal = old.Renew(Today, startDate: null, amount: null);

        Assert.Equal(PlanStatus.Renewed, old.Status);
        Assert.Equal(7, renewal.PreviousPlanId);
        Assert.Equal(new DateOnly(2026, 10, 13), renewal.StartDate); // previous EndDate + 1 day
        Assert.Equal(new DateOnly(2027, 10, 13), renewal.EndDate);
        Assert.Equal(new DateOnly(2027, 9, 13), renewal.NotificationDate);
        Assert.Equal(12000m, renewal.Amount);
        Assert.Equal(PlanType.AMC, renewal.PlanType);
        Assert.Equal(old.CustomerId, renewal.CustomerId);
    }

    [Fact]
    public void Renew_allows_start_date_override()
    {
        var old = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2025, 10, 12), Today);
        var renewal = old.Renew(Today, new DateOnly(2026, 11, 1), 15000m);
        Assert.Equal(new DateOnly(2026, 11, 1), renewal.StartDate);
        Assert.Equal(new DateOnly(2027, 11, 1), renewal.EndDate);
        Assert.Equal(15000m, renewal.Amount);
    }

    [Fact]
    public void Renewing_twice_is_rejected()
    {
        var old = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2025, 10, 12), Today);
        old.Renew(Today, null, null);
        Assert.Throws<InvalidOperationException>(() => old.Renew(Today, null, null));
    }

    [Fact]
    public void Renewed_plan_reports_Renewed_even_after_it_has_ended()
    {
        var old = Plan.Create(1, PlanType.AMC, 12000m, new DateOnly(2020, 1, 1), Today);
        old.Renew(Today, null, null);
        Assert.Equal(PlanStatus.Renewed, old.EffectiveStatus(Today));
    }
}

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+919876543210")]
    [InlineData("+14155552671")]
    [InlineData("+442071838750")]
    public void Accepts_valid_E164(string value) => Assert.True(PhoneNumber.IsValidE164(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9876543210")]        // no +
    [InlineData("+0919876543210")]    // leading zero after +
    [InlineData("+91 98765 43210")]   // spaces
    [InlineData("+91-9876543210")]
    [InlineData("+1234567")]          // too short
    [InlineData("+1234567890123456")] // too long
    [InlineData("+91abc6543210")]
    public void Rejects_invalid(string? value) => Assert.False(PhoneNumber.IsValidE164(value));

    [Fact]
    public void Mask_hides_the_middle_digits()
    {
        var masked = PhoneNumber.Mask("+919876543210");
        Assert.Equal("+91******3210", masked);
        Assert.DoesNotContain("98765", masked);
    }
}
