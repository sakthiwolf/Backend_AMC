using AmcTracker.Api.Infrastructure;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AmcTracker.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController(DashboardService dashboard) : ControllerBase
{
    /// <summary>Counts only: active, due, expired, ending in 30 days, active customers, and the date they are for.</summary>
    [HttpGet("summary")]
    public Task<DashboardSummaryDto> Summary(CancellationToken ct) => dashboard.GetSummaryAsync(ct);

    /// <summary>Plans that need action, soonest end date first. <c>days</c> is 1 to 90 (default 7), else 422.</summary>
    [HttpGet("upcoming")]
    public Task<UpcomingResponse> Upcoming([FromQuery] int days = DashboardService.DefaultDays, CancellationToken ct = default) =>
        dashboard.GetUpcomingAsync(days, ct);

    /// <summary>One entry per calendar month from this month. <c>months</c> is 1 to 24 (default 12), else 422.</summary>
    [HttpGet("monthly-renewals")]
    public Task<MonthlyRenewalsResponse> MonthlyRenewals(
        [FromQuery] int months = DashboardService.DefaultMonths, CancellationToken ct = default) =>
        dashboard.GetMonthlyRenewalsAsync(months, ct);
}

/// <summary>
/// The four reports and their export. Stored values and counts only (no sums, averages or totals). Inactive customers
/// never appear. Errors are 422: invalid_range, range_too_large, export_too_large, validation_error.
/// </summary>
[ApiController]
[Route("api/reports")]
public class ReportsController(ReportService reports, ReportExportService exports) : ControllerBase
{
    /// <summary>Plans whose period overlaps from..to (default: this month). Paged: 50 a page, 1 to 200.</summary>
    [HttpGet("plans")]
    public Task<PagedResult<PlansReportItemDto>> Plans(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? type, [FromQuery] string? status,
        [FromQuery] bool includeRenewed = true, [FromQuery] int? page = null, [FromQuery] int? pageSize = null,
        CancellationToken ct = default) =>
        reports.GetPlansAsync(new PlansReportFilter(from, to, type, status, includeRenewed, page, pageSize), ct);

    /// <summary>Plans DueForRenewal or Expired as of a date (default today), soonest end date first.</summary>
    [HttpGet("due")]
    public Task<DueReportDto> Due(
        [FromQuery] DateOnly? asOf, [FromQuery] string? type, CancellationToken ct = default) =>
        reports.GetDueAndExpiringAsync(new DueReportFilter(asOf, type), ct);

    /// <summary>Reminder rows scheduled from..to, by status. <c>errorMessage</c> is cut to 200 characters.</summary>
    [HttpGet("reminders")]
    public Task<PagedResult<RemindersReportItemDto>> Reminders(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? status,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken ct = default) =>
        reports.GetRemindersAsync(new RemindersReportFilter(from, to, status, page, pageSize), ct);

    /// <summary>Renewals whose new plan starts from..to, with the previous plan and its payment.</summary>
    [HttpGet("renewals")]
    public Task<PagedResult<RenewalsReportItemDto>> Renewals(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken ct = default) =>
        reports.GetRenewalsAsync(new RenewalsReportFilter(from, to, page, pageSize), ct);

    /// <summary>Every row in the range as an Excel or PDF file, named {report}-{yyyyMMdd}.{ext} (IST date).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? report, [FromQuery] string? format, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? type, [FromQuery] string? status, [FromQuery] bool includeRenewed = true,
        [FromQuery] DateOnly? asOf = null, CancellationToken ct = default)
    {
        var file = await exports.ExportAsync(
            new ReportExportRequest(report, format, from, to, type, status, includeRenewed, asOf), ct);
        Response.Headers.CacheControl = "no-store"; // exports are never cached
        return File(file.Content, file.ContentType, file.FileName); // Content-Disposition: attachment
    }
}

[ApiController]
[Route("api/reminders")]
public class RemindersController(ReminderService reminders) : ControllerBase
{
    /// <summary>Runs the daily reminder pass now (admin only). Safe to repeat: sent reminders are never re-sent.</summary>
    [HttpPost("run")]
    [Authorize(Policy = AdminRequirement.Policy)]
    public Task<ReminderRunResult> Run(CancellationToken ct) => reminders.RunDailyAsync(ct);

    /// <summary>
    /// Sends the template for <paramref name="planType"/> (default AMC) once to Reminders:TestPhone with sample
    /// values (admin only). Stores nothing. 409 test_phone_not_set when no test phone is configured.
    /// </summary>
    [HttpPost("test-send")]
    [Authorize(Policy = AdminRequirement.Policy)]
    public Task<TestSendResultDto> TestSend([FromQuery] PlanType? planType, CancellationToken ct) =>
        reminders.TestSendAsync(planType ?? PlanType.AMC, ct);

    /// <summary>"Try again" on one failed reminder. 409 when it cannot be retried (sent, out of attempts, plan over).</summary>
    [HttpPost("{id:int}/retry")]
    public Task<ReminderDto> Retry(int id, CancellationToken ct) => reminders.RetryAsync(id, ct);
}

[ApiController]
public class HealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("api/health")]
    public object Health() => new { status = "ok" };
}
