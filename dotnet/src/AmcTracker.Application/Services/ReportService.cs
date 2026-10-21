using System.Diagnostics;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AmcTracker.Application.Services;

/// <summary>
/// The four reports (CLAUDE.md 4.6): Plans, Due and expiring, Reminders, Renewals. Stored values and counts only:
/// no sums, averages, totals or derived amounts. "Today" is the Asia/Kolkata date from <see cref="IClock"/>; status
/// comes from <see cref="PlanSpec"/> in the query and <see cref="PlanStatusCalculator"/> on the row. Inactive
/// customers never appear. Logs carry the report name, row count and duration only.
/// </summary>
public class ReportService(IReportRepository reports, IClock clock, ILogger<ReportService> logger)
{
    // ---- On screen (paged) ----

    public async Task<PagedResult<PlansReportItemDto>> GetPlansAsync(PlansReportFilter f, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var query = PlansQuery(f, out var page, out var size);
        var result = await reports.PlansAsync(query, withTotal: true, ct);
        var items = result.Rows.Select(r => PlanItem(r, query.Today)).ToList();
        Log("plans", items.Count, started);
        return new PagedResult<PlansReportItemDto>(items, page, size, result.Total ?? items.Count);
    }

    /// <summary>Plans DueForRenewal or Expired, soonest end date first. Not paged.</summary>
    public async Task<DueReportDto> GetDueAndExpiringAsync(DueReportFilter f, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var asOf = f.AsOf ?? clock.Today;
        var rows = await reports.DueAsync(new DueReportQuery(asOf, ReportRules.Type(f.Type)), ct);
        var items = rows.Select(r => DueItem(r, asOf)).ToList();
        Log("due", items.Count, started);
        return new DueReportDto(items, asOf);
    }

    public async Task<PagedResult<RemindersReportItemDto>> GetRemindersAsync(RemindersReportFilter f, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var query = RemindersQuery(f, out var page, out var size);
        var result = await reports.RemindersAsync(query, withTotal: true, ct);
        var items = result.Rows.Select(ReminderItem).ToList();
        Log("reminders", items.Count, started);
        return new PagedResult<RemindersReportItemDto>(items, page, size, result.Total ?? items.Count);
    }

    public async Task<PagedResult<RenewalsReportItemDto>> GetRenewalsAsync(RenewalsReportFilter f, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var query = RenewalsQuery(f, out var page, out var size);
        var result = await reports.RenewalsAsync(query, withTotal: true, ct);
        var items = result.Rows.Select(RenewalItem).ToList();
        Log("renewals", items.Count, started);
        return new PagedResult<RenewalsReportItemDto>(items, page, size, result.Total ?? items.Count);
    }

    // ---- For export: every row in the range (one query, no count), at most 20,000 ----

    public async Task<IReadOnlyList<PlansReportItemDto>> GetPlansForExportAsync(PlansReportFilter f, CancellationToken ct = default)
    {
        var query = PlansQuery(f with { Page = 1, PageSize = null }, out _, out _) with { Skip = 0, Take = ReportRules.MaxExportRows + 1 };
        var result = await reports.PlansAsync(query, withTotal: false, ct);
        return CapExport(result.Rows).Select(r => PlanItem(r, query.Today)).ToList();
    }

    public async Task<(IReadOnlyList<DueReportItemDto> Items, DateOnly AsOf)> GetDueForExportAsync(DueReportFilter f, CancellationToken ct = default)
    {
        var due = await GetDueAndExpiringAsync(f, ct);
        if (due.Items.Count > ReportRules.MaxExportRows) throw ExportTooLarge();
        return (due.Items, due.AsOf);
    }

    public async Task<IReadOnlyList<RemindersReportItemDto>> GetRemindersForExportAsync(RemindersReportFilter f, CancellationToken ct = default)
    {
        var query = RemindersQuery(f with { Page = 1, PageSize = null }, out _, out _) with { Skip = 0, Take = ReportRules.MaxExportRows + 1 };
        var result = await reports.RemindersAsync(query, withTotal: false, ct);
        return CapExport(result.Rows).Select(ReminderItem).ToList();
    }

    public async Task<IReadOnlyList<RenewalsReportItemDto>> GetRenewalsForExportAsync(RenewalsReportFilter f, CancellationToken ct = default)
    {
        var query = RenewalsQuery(f with { Page = 1, PageSize = null }, out _, out _) with { Skip = 0, Take = ReportRules.MaxExportRows + 1 };
        var result = await reports.RenewalsAsync(query, withTotal: false, ct);
        return CapExport(result.Rows).Select(RenewalItem).ToList();
    }

    // ---- Validation into queries ----

    private PlansReportQuery PlansQuery(PlansReportFilter f, out int page, out int size)
    {
        var today = clock.Today;
        var (from, to) = ReportRules.Range(f.From, f.To, today);
        var type = ReportRules.Type(f.Type);
        var status = ReportRules.PlanStatus(f.Status);
        (page, size) = ReportRules.Paging(f.Page, f.PageSize);
        return new PlansReportQuery(from, to, today, type, status, f.IncludeRenewed, (page - 1) * size, size);
    }

    private RemindersReportQuery RemindersQuery(RemindersReportFilter f, out int page, out int size)
    {
        var (from, to) = ReportRules.Range(f.From, f.To, clock.Today);
        var status = ReportRules.ReminderStatus(f.Status);
        (page, size) = ReportRules.Paging(f.Page, f.PageSize);
        return new RemindersReportQuery(from, to, status, (page - 1) * size, size);
    }

    private RenewalsReportQuery RenewalsQuery(RenewalsReportFilter f, out int page, out int size)
    {
        var (from, to) = ReportRules.Range(f.From, f.To, clock.Today);
        (page, size) = ReportRules.Paging(f.Page, f.PageSize);
        return new RenewalsReportQuery(from, to, (page - 1) * size, size);
    }

    // ---- Rows into items ----

    private static PlansReportItemDto PlanItem(PlanReportRow r, DateOnly today) => new(
        r.PlanId, r.CustomerName, r.Phone, r.Location, r.ProjectName, r.PlanType, Money(r.Amount),
        r.StartDate, r.EndDate, r.ReminderDate,
        // The same rules as everywhere else (and as PlanSpec in the query).
        PlanStatusCalculator.Resolve(today, r.ReminderDate, r.EndDate, r.IsRenewed));

    private static DueReportItemDto DueItem(PlanReportRow r, DateOnly asOf) => new(
        r.PlanId, r.CustomerName, r.Phone, r.Location, r.ProjectName, r.PlanType, Money(r.Amount),
        r.StartDate, r.EndDate, r.ReminderDate,
        PlanStatusCalculator.Resolve(asOf, r.ReminderDate, r.EndDate, r.IsRenewed),
        r.EndDate.DayNumber - asOf.DayNumber);

    private static RemindersReportItemDto ReminderItem(ReminderReportRow r) => new(
        r.ReminderId, r.PlanId, r.CustomerName, r.Phone, r.ScheduledDate, r.Status, r.Attempts, r.SentAt,
        ReportRules.Truncate(r.ErrorMessage, ReportRules.MaxErrorLength));

    private static RenewalsReportItemDto RenewalItem(RenewalReportRow r) => new(
        r.RenewedPlanId, r.PreviousPlanId, r.CustomerName, r.Phone, r.ProjectName, r.PlanType,
        r.PreviousEndDate, r.NewStartDate, r.NewEndDate, Money(r.Amount),
        r.PaidOn, r.PaymentMode, r.PaymentAmount is { } p ? Money(p) : null);

    private static IReadOnlyList<T> CapExport<T>(IReadOnlyList<T> rows) =>
        rows.Count > ReportRules.MaxExportRows ? throw ExportTooLarge() : rows;

    internal static AppException ExportTooLarge() => new(422, "export_too_large",
        $"This report has more than {ReportRules.MaxExportRows:N0} rows. Choose a shorter date range or add filters.");

    /// <summary>Each row's stored value with two decimals (12345.60, not 12345.6). Never summed.</summary>
    private static decimal Money(decimal value) => decimal.Round(value, 2) + 0.00m;

    private void Log(string report, int rows, long started) =>
        logger.LogInformation("Report {Report}: {Rows} rows in {Ms} ms", report, rows,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
