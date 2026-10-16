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
    [HttpGet("summary")]
    public Task<DashboardSummaryDto> Summary(CancellationToken ct) => dashboard.GetSummaryAsync(ct);

    [HttpGet("upcoming")]
    public Task<IReadOnlyList<PlanDto>> Upcoming([FromQuery] int days = 30, CancellationToken ct = default) =>
        dashboard.GetUpcomingAsync(days, ct);

    [HttpGet("monthly-renewals")]
    public Task<IReadOnlyList<MonthlyRenewalDto>> MonthlyRenewals(CancellationToken ct) =>
        dashboard.GetMonthlyRenewalsAsync(ct);
}

[ApiController]
[Route("api/reports")]
public class ReportsController(ReportService reports) : ControllerBase
{
    /// <summary>from/to filter the date chosen by <c>basis</c> (default: plan end date).</summary>
    [HttpGet("plans")]
    public Task<ReportResultDto> Plans(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] PlanType? type,
        [FromQuery] PlanStatus? status, [FromQuery] DateBasis basis = DateBasis.EndDate,
        CancellationToken ct = default) =>
        reports.GetPlansAsync(from, to, type, status, basis, ct);

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? format, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] PlanType? type, [FromQuery] PlanStatus? status,
        [FromQuery] DateBasis basis = DateBasis.EndDate, CancellationToken ct = default)
    {
        var file = await reports.ExportAsync(format, from, to, type, status, basis, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

[ApiController]
[Route("api/reminders")]
public class RemindersController(ReminderService reminders) : ControllerBase
{
    /// <summary>Runs the daily reminder pass now. Safe to repeat: sent reminders are never re-sent.</summary>
    [HttpPost("run")]
    public Task<ReminderRunResult> Run(CancellationToken ct) => reminders.RunDailyAsync(ct);
}

[ApiController]
public class HealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("api/health")]
    public object Health() => new { status = "ok" };
}
