using AmcTracker.Application.Abstractions;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.Infrastructure.Persistence;

/// <summary>
/// Report queries (CLAUDE.md 4.6). Each report is one filtered query over plans (or reminders) of active customers,
/// joining the customer, and for renewals the previous plan and its latest payment, in the same statement: no
/// per-row queries. When a total is wanted a count query runs first (and the rows query is skipped if it is 0).
/// Status conditions come from <see cref="PlanSpec"/>, so the SQL agrees with <c>Plan.StatusOn</c>. AsNoTracking.
/// </summary>
internal class ReportRepository(AppDbContext db) : IReportRepository
{
    public async Task<ReportRows<PlanReportRow>> PlansAsync(PlansReportQuery q, bool withTotal, CancellationToken ct = default)
    {
        // A plan is in the range when its period overlaps it.
        IQueryable<Plan> plans = db.Plans.AsNoTracking()
            .Where(p => p.Customer!.IsActive && p.StartDate <= q.To && p.EndDate >= q.From);
        if (q.Type is { } type) plans = plans.Where(p => p.PlanType == type);
        if (q.Status is { } status) plans = plans.Where(PlanSpec.HasStatus(status, q.Today));
        if (!q.IncludeRenewed) plans = plans.Where(p => p.Renewal == null);

        var total = withTotal ? await plans.CountAsync(ct) : (int?)null;
        if (total == 0) return new ReportRows<PlanReportRow>([], 0);
        var rows = await plans.OrderBy(p => p.EndDate).ThenBy(p => p.PlanId)
            .Skip(q.Skip).Take(q.Take)
            .Select(p => new PlanReportRow(
                p.PlanId, p.Customer!.CustomerName, p.Customer.Phone, p.Customer.Location, p.Customer.ProjectName,
                p.PlanType, p.Amount, p.StartDate, p.EndDate, p.ReminderDate, p.Renewal != null))
            .ToListAsync(ct);
        return new ReportRows<PlanReportRow>(rows, total);
    }

    public async Task<IReadOnlyList<PlanReportRow>> DueAsync(DueReportQuery q, CancellationToken ct = default)
    {
        var dueOrExpired = PlanSpec.Or(
            PlanSpec.HasStatus(PlanStatus.DueForRenewal, q.AsOf), PlanSpec.HasStatus(PlanStatus.Expired, q.AsOf));
        IQueryable<Plan> plans = db.Plans.AsNoTracking().Where(p => p.Customer!.IsActive).Where(dueOrExpired);
        if (q.Type is { } type) plans = plans.Where(p => p.PlanType == type);

        return await plans.OrderBy(p => p.EndDate).ThenBy(p => p.PlanId)
            .Select(p => new PlanReportRow(
                p.PlanId, p.Customer!.CustomerName, p.Customer.Phone, p.Customer.Location, p.Customer.ProjectName,
                p.PlanType, p.Amount, p.StartDate, p.EndDate, p.ReminderDate, p.Renewal != null))
            .ToListAsync(ct);
    }

    public async Task<ReportRows<ReminderReportRow>> RemindersAsync(RemindersReportQuery q, bool withTotal, CancellationToken ct = default)
    {
        IQueryable<Reminder> reminders = db.Reminders.AsNoTracking()
            .Where(r => r.Plan!.Customer!.IsActive && r.ScheduledDate >= q.From && r.ScheduledDate <= q.To);
        if (q.Status is { } status) reminders = reminders.Where(r => r.Status == status);

        var total = withTotal ? await reminders.CountAsync(ct) : (int?)null;
        if (total == 0) return new ReportRows<ReminderReportRow>([], 0);
        var rows = await reminders.OrderByDescending(r => r.ScheduledDate).ThenByDescending(r => r.ReminderId)
            .Skip(q.Skip).Take(q.Take)
            .Select(r => new ReminderReportRow(
                r.ReminderId, r.PlanId, r.Plan!.Customer!.CustomerName, r.Plan.Customer.Phone, r.ScheduledDate,
                r.Status, r.Attempts, r.SentAt, r.ErrorMessage))
            .ToListAsync(ct);
        return new ReportRows<ReminderReportRow>(rows, total);
    }

    public async Task<ReportRows<RenewalReportRow>> RenewalsAsync(RenewalsReportQuery q, bool withTotal, CancellationToken ct = default)
    {
        // The new plan is the one that points back (PreviousPlanId); it is chosen by its own start date.
        IQueryable<Plan> renewals = db.Plans.AsNoTracking()
            .Where(p => p.PreviousPlanId != null && p.Customer!.IsActive && p.StartDate >= q.From && p.StartDate <= q.To);

        var total = withTotal ? await renewals.CountAsync(ct) : (int?)null;
        if (total == 0) return new ReportRows<RenewalReportRow>([], 0);
        var rows = await renewals.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.PlanId)
            .Skip(q.Skip).Take(q.Take)
            .Select(p => new RenewalReportRow(
                p.PlanId, p.PreviousPlanId!.Value, p.Customer!.CustomerName, p.Customer.Phone, p.Customer.ProjectName,
                p.PlanType, p.PreviousPlan!.EndDate, p.StartDate, p.EndDate, p.Amount,
                // The payment recorded for the previous plan (its latest, if more than one); null when none.
                db.Payments.Where(x => x.PlanId == p.PreviousPlanId)
                    .OrderByDescending(x => x.PaidOn).ThenByDescending(x => x.PaymentId)
                    .Select(x => (DateOnly?)x.PaidOn).FirstOrDefault(),
                db.Payments.Where(x => x.PlanId == p.PreviousPlanId)
                    .OrderByDescending(x => x.PaidOn).ThenByDescending(x => x.PaymentId)
                    .Select(x => (PaymentMode?)x.Mode).FirstOrDefault(),
                db.Payments.Where(x => x.PlanId == p.PreviousPlanId)
                    .OrderByDescending(x => x.PaidOn).ThenByDescending(x => x.PaymentId)
                    .Select(x => (decimal?)x.Amount).FirstOrDefault()))
            .ToListAsync(ct);
        return new ReportRows<RenewalReportRow>(rows, total);
    }
}
