using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Enums;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class DashboardServiceTests
{
    private readonly IDashboardRepository _repo = Substitute.For<IDashboardRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public DashboardServiceTests()
    {
        _clock.Today.Returns(new DateOnly(2026, 10, 21));
        _repo.CountEndingByMonthAsync(default, default, default).ReturnsForAnyArgs([]);
        _repo.ListNeedingActionAsync(default, default, default).ReturnsForAnyArgs([]);
        _repo.CountByStatusAsync(default, default).ReturnsForAnyArgs(new StatusCounts(0, 0, 0, 0));
    }

    private DashboardService Service() => new(_repo, _clock);

    // ---- Summary ----

    [Fact]
    public async Task Summary_carries_the_counts_and_the_date_they_are_for()
    {
        _repo.CountByStatusAsync(new DateOnly(2026, 10, 21), Arg.Any<CancellationToken>()).Returns(new StatusCounts(5, 3, 2, 9));
        _repo.CountEndingWithinAsync(new DateOnly(2026, 10, 21), 30, Arg.Any<CancellationToken>()).Returns(4);
        _repo.CountActiveCustomersAsync(Arg.Any<CancellationToken>()).Returns(8);

        var s = await Service().GetSummaryAsync();

        Assert.Equal((5, 3, 2, 4, 8, new DateOnly(2026, 10, 21)),
            (s.ActiveCount, s.DueCount, s.ExpiredCount, s.EndingIn30DaysCount, s.CustomerCount, s.AsOf));
    }

    [Fact]
    public async Task Renewed_plans_are_not_part_of_any_summary_count()
    {
        _repo.CountByStatusAsync(default, default).ReturnsForAnyArgs(new StatusCounts(1, 1, 1, Renewed: 50));
        var s = await Service().GetSummaryAsync();
        Assert.Equal(3, s.ActiveCount + s.DueCount + s.ExpiredCount);
    }

    [Fact]
    public async Task The_summary_json_has_no_amount_field()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(await Service().GetSummaryAsync());
        foreach (var banned in new[] { "revenue", "amount", "total" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Upcoming ----

    [Fact]
    public async Task Upcoming_defaults_to_seven_days()
    {
        await Service().GetUpcomingAsync();
        await _repo.Received(1).ListNeedingActionAsync(new DateOnly(2026, 10, 21), 7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task Upcoming_days_outside_1_to_90_is_422(int days)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().GetUpcomingAsync(days));
        Assert.Equal((422, "validation_error"), (ex.StatusCode, ex.Code));
        await _repo.DidNotReceiveWithAnyArgs().ListNeedingActionAsync(default, default, default);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    public async Task Upcoming_days_1_and_90_are_allowed(int days) => await Service().GetUpcomingAsync(days);

    [Fact]
    public async Task Upcoming_status_comes_from_the_shared_rules_and_the_order_is_kept()
    {
        var today = new DateOnly(2026, 10, 21);
        _repo.ListNeedingActionAsync(today, 7, Arg.Any<CancellationToken>()).Returns(
        [
            new UpcomingRow(1, 10, "A", "+919000000001", null, PlanType.Monthly, today, today.AddDays(-7), ReminderStatus.Sent),
            new UpcomingRow(2, 11, "B", "+919000000002", "CCTV", PlanType.AMC, today.AddDays(3), today.AddDays(2), null),
        ]);

        var items = (await Service().GetUpcomingAsync()).Items;

        Assert.Equal([1, 2], items.Select(i => i.PlanId));
        Assert.Equal([PlanStatus.DueForRenewal, PlanStatus.Active], items.Select(i => i.Status)); // reminder in 2 days
        Assert.Equal([ReminderStatus.Sent, null], items.Select(i => i.ReminderStatus));
    }

    // ---- Monthly renewals ----

    [Fact]
    public async Task Monthly_renewals_defaults_to_twelve_months_starting_this_month()
    {
        var months = (await Service().GetMonthlyRenewalsAsync()).Months;
        Assert.Equal(12, months.Count);
        Assert.Equal("2026-10", months[0].Month);
        Assert.Equal("2027-09", months[^1].Month);
        await _repo.Received(1).CountEndingByMonthAsync(
            new DateOnly(2026, 10, 1), new DateOnly(2027, 9, 30), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public async Task Monthly_renewals_months_outside_1_to_24_is_422(int months)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().GetMonthlyRenewalsAsync(months));
        Assert.Equal((422, "validation_error"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task Months_cross_the_year_boundary_and_missing_months_are_zero()
    {
        _clock.Today.Returns(new DateOnly(2026, 11, 20));
        _repo.CountEndingByMonthAsync(default, default, default).ReturnsForAnyArgs(
        [
            new MonthCount(2026, 12, 3, 1),
            new MonthCount(2027, 1, 2, 2),
        ]);

        var months = (await Service().GetMonthlyRenewalsAsync(4)).Months;

        Assert.Equal(["2026-11", "2026-12", "2027-01", "2027-02"], months.Select(m => m.Month));
        Assert.Equal([(0, 0, 0), (3, 1, 2), (2, 2, 0), (0, 0, 0)],
            months.Select(m => (m.EndingCount, m.RenewedCount, m.PendingCount)));
    }

    [Fact]
    public async Task Renewed_plus_pending_always_equals_ending()
    {
        _repo.CountEndingByMonthAsync(default, default, default).ReturnsForAnyArgs(
        [
            new MonthCount(2026, 10, 5, 2), new MonthCount(2026, 12, 7, 0), new MonthCount(2027, 2, 1, 1),
        ]);
        foreach (var m in (await Service().GetMonthlyRenewalsAsync(24)).Months)
            Assert.Equal(m.EndingCount, m.RenewedCount + m.PendingCount);
    }

    [Fact]
    public async Task At_00_30_IST_on_the_1st_the_current_month_is_that_month_not_the_previous_one()
    {
        // 00:30 IST on 1 Nov 2026 is 19:00 UTC on 31 Oct. The business date (IClock.Today) is already 1 Nov.
        _clock.UtcNow.Returns(new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc));
        _clock.Today.Returns(new DateOnly(2026, 11, 1));

        var months = (await Service().GetMonthlyRenewalsAsync(2)).Months;

        Assert.Equal(["2026-11", "2026-12"], months.Select(m => m.Month));
    }
}
