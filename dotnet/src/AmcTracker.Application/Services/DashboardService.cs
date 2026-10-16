using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Services;

public class DashboardService(IPlanRepository plans, IClock clock)
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var all = await plans.ListAsync(new PlanFilter(), today, ct);
        var open = all.Where(p => !p.IsRenewed).ToList();

        var active = open.Where(p => p.EffectiveStatus(today) == PlanStatus.Active).ToList();
        var due = open.Count(p => p.EffectiveStatus(today) == PlanStatus.DueForRenewal);
        var expired = open.Count(p => p.EffectiveStatus(today) == PlanStatus.Expired);
        var dueIn30 = open.Count(p => p.NotificationDate >= today && p.NotificationDate <= today.AddDays(30));

        // TODO(open decision 1, CLAUDE.md section 9): "monthly revenue" is undefined in CLAUDE.md and
        // depends on whether a Monthly plan's Amount is the rate or the contract total. For now this
        // sums Amount of non-renewed, non-expired Monthly plans exactly as stored (no x12, no /12).
        var revenue = open
            .Where(p => p.PlanType == PlanType.Monthly && p.EffectiveStatus(today) != PlanStatus.Expired)
            .Sum(p => p.Amount);

        return new DashboardSummaryDto(active.Count, due, dueIn30, expired, revenue);
    }

    /// <summary>Plans whose reminder date falls within the next <paramref name="days"/> days.</summary>
    public async Task<IReadOnlyList<PlanDto>> GetUpcomingAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 0, 366);
        var today = clock.Today;
        var list = await plans.ListAsync(
            new PlanFilter(Basis: DateBasis.NotificationDate, From: today, To: today.AddDays(days)), today, ct);
        return list.Where(p => !p.IsRenewed)
            .OrderBy(p => p.NotificationDate).ThenBy(p => p.PlanId)
            .Select(p => p.ToDto(today)).ToList();
    }

    /// <summary>Plans ending in each of the next 12 calendar months (this month included), renewed plans excluded.</summary>
    public async Task<IReadOnlyList<MonthlyRenewalDto>> GetMonthlyRenewalsAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        var last = first.AddMonths(12).AddDays(-1);
        var list = await plans.ListAsync(new PlanFilter(From: first, To: last), today, ct);
        var counts = list.Where(p => !p.IsRenewed)
            .GroupBy(p => (p.EndDate.Year, p.EndDate.Month))
            .ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, 12).Select(i =>
        {
            var m = first.AddMonths(i);
            counts.TryGetValue((m.Year, m.Month), out var n);
            return new MonthlyRenewalDto(m.Year, m.Month, m.ToString("MMM yyyy"), n);
        }).ToList();
    }
}
