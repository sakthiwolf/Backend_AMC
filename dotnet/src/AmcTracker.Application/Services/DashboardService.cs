using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Services;

namespace AmcTracker.Application.Services;

/// <summary>
/// The dashboard: counts and dates only, never revenue, totals or any amount. "Today" is the Asia/Kolkata date
/// from <see cref="IClock"/>. Status is calculated on read by the rules in <see cref="PlanSpec"/> /
/// <see cref="PlanStatusCalculator"/>; plans of inactive customers are not counted; renewed plans are left out
/// of every count except the monthly chart, where they are shown apart. Nothing here is stored.
/// </summary>
public class DashboardService(IDashboardRepository dashboard, IClock clock)
{
    public const int EndingSoonDays = 30;
    public const int DefaultDays = 7, MinDays = 1, MaxDays = 90;
    public const int DefaultMonths = 12, MinMonths = 1, MaxMonths = 24;

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var byStatus = await dashboard.CountByStatusAsync(today, ct);
        var endingSoon = await dashboard.CountEndingWithinAsync(today, EndingSoonDays, ct);
        var customers = await dashboard.CountActiveCustomersAsync(ct);
        return new DashboardSummaryDto(byStatus.Active, byStatus.Due, byStatus.Expired, endingSoon, customers, today);
    }

    /// <summary>Plans that need action now, soonest end date first. <paramref name="days"/> must be 1 to 90.</summary>
    public async Task<UpcomingResponse> GetUpcomingAsync(int days = DefaultDays, CancellationToken ct = default)
    {
        if (days is < MinDays or > MaxDays) throw OutOfRange("days", MinDays, MaxDays);
        var today = clock.Today;
        var rows = await dashboard.ListNeedingActionAsync(today, days, ct);
        var items = rows.Select(r => new UpcomingPlanDto(
            r.PlanId, r.CustomerId, r.CustomerName, r.Phone, r.ProjectName, r.PlanType, r.EndDate, r.ReminderDate,
            // The same rules as everywhere else; rows are never renewed plans.
            PlanStatusCalculator.Resolve(today, r.ReminderDate, r.EndDate, isRenewed: false),
            r.ReminderStatus)).ToList();
        return new UpcomingResponse(items);
    }

    /// <summary>
    /// One entry per calendar month starting with the current month (in Asia/Kolkata).
    /// <paramref name="months"/> must be 1 to 24. <c>EndingCount = RenewedCount + PendingCount</c>.
    /// </summary>
    public async Task<MonthlyRenewalsResponse> GetMonthlyRenewalsAsync(int months = DefaultMonths, CancellationToken ct = default)
    {
        if (months is < MinMonths or > MaxMonths) throw OutOfRange("months", MinMonths, MaxMonths);
        var today = clock.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        var last = first.AddMonths(months).AddDays(-1);
        var counts = (await dashboard.CountEndingByMonthAsync(first, last, ct))
            .ToDictionary(c => (c.Year, c.Month));

        var points = new List<MonthlyRenewalPointDto>(months);
        for (var i = 0; i < months; i++)
        {
            var month = first.AddMonths(i);
            counts.TryGetValue((month.Year, month.Month), out var c);
            var ending = c?.Ending ?? 0;
            var renewed = c?.Renewed ?? 0;
            points.Add(new MonthlyRenewalPointDto($"{month.Year:0000}-{month.Month:00}", ending, renewed, ending - renewed));
        }
        return new MonthlyRenewalsResponse(points);
    }

    private static AppException OutOfRange(string name, int min, int max) =>
        new(422, "validation_error", $"Invalid or missing fields: {name} (a whole number from {min} to {max}).");
}
