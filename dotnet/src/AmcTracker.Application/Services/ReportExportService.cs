using System.Diagnostics;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using Microsoft.Extensions.Logging;

namespace AmcTracker.Application.Services;

/// <summary>
/// Builds the Excel or PDF file for a report: every row in the range (at most 20,000), the same columns as the
/// screen, no totals row. The file is returned and never stored. The builders (Infrastructure) only draw the table.
/// Logs carry the report name, format, row count and duration only.
/// </summary>
public class ReportExportService(
    ReportService reports,
    IEnumerable<IReportFileBuilder> builders,
    IClock clock,
    ILogger<ReportExportService> logger)
{
    private static readonly string[] ReportNames = ["plans", "due", "reminders", "renewals"];

    public async Task<ExportFile> ExportAsync(ReportExportRequest req, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var report = (req.Report ?? "").Trim().ToLowerInvariant();
        if (!ReportNames.Contains(report))
            throw new AppException(422, "validation_error", "Invalid or missing fields: report (plans, due, reminders or renewals).");
        var builder = builders.FirstOrDefault(b => b.Format.Equals((req.Format ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException(422, "validation_error", "Invalid or missing fields: format (xlsx or pdf).");

        var table = report switch
        {
            "plans" => await PlansAsync(req, ct),
            "due" => await DueAsync(req, ct),
            "reminders" => await RemindersAsync(req, ct),
            _ => await RenewalsAsync(req, ct),
        };

        var generatedAtIst = ReportTables.ToIst(clock.UtcNow);
        var bytes = builder.Build(table, generatedAtIst);
        var fileName = $"{report}-{clock.Today:yyyyMMdd}.{builder.Extension}"; // the IST date
        logger.LogInformation("Report export {Report} {Format}: {Rows} rows, {Bytes} bytes in {Ms} ms",
            report, builder.Format, table.Rows.Count, bytes.Length, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new ExportFile(bytes, builder.ContentType, fileName);
    }

    private async Task<ReportTable> PlansAsync(ReportExportRequest req, CancellationToken ct)
    {
        var filter = new PlansReportFilter(req.From, req.To, req.Type, req.Status, req.IncludeRenewed);
        var items = await reports.GetPlansForExportAsync(filter, ct);
        var (from, to) = ReportRules.Range(req.From, req.To, clock.Today);
        return ReportTables.Plans(items, from, to, PlanFilterText(req));
    }

    private async Task<ReportTable> DueAsync(ReportExportRequest req, CancellationToken ct)
    {
        var (items, asOf) = await reports.GetDueForExportAsync(new DueReportFilter(req.AsOf, req.Type), ct);
        return ReportTables.Due(items, asOf, TypeText(req.Type));
    }

    private async Task<ReportTable> RemindersAsync(ReportExportRequest req, CancellationToken ct)
    {
        var items = await reports.GetRemindersForExportAsync(new RemindersReportFilter(req.From, req.To, req.Status), ct);
        var (from, to) = ReportRules.Range(req.From, req.To, clock.Today);
        return ReportTables.Reminders(items, from, to, StatusText(req.Status));
    }

    private async Task<ReportTable> RenewalsAsync(ReportExportRequest req, CancellationToken ct)
    {
        var items = await reports.GetRenewalsForExportAsync(new RenewalsReportFilter(req.From, req.To), ct);
        var (from, to) = ReportRules.Range(req.From, req.To, clock.Today);
        return ReportTables.Renewals(items, from, to);
    }

    // The filters used, in words, for the file header (the values were validated by the report query).
    private static string? PlanFilterText(ReportExportRequest req)
    {
        var parts = new List<string>();
        if (TypeText(req.Type) is { } type) parts.Add(type);
        if (ReportRules.PlanStatus(req.Status) is { } status) parts.Add($"Status: {ReportTables.Status(status)}");
        if (!req.IncludeRenewed) parts.Add("Renewed plans not included");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? TypeText(string? type) =>
        ReportRules.Type(type) is { } t ? $"Plan type: {t}" : null;

    private static string? StatusText(string? status) =>
        ReportRules.ReminderStatus(status) is { } s ? $"Status: {s}" : null;
}
