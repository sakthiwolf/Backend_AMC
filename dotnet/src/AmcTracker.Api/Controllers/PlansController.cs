using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace AmcTracker.Api.Controllers;

[ApiController]
[Route("api/plans")]
public class PlansController(PlanService plans, RenewalService renewals) : ControllerBase
{
    /// <summary>
    /// Plans ordered by end date. Status is calculated on read: Active, DueForRenewal, Expired, Renewed.
    /// <c>endingWithinDays</c> keeps only not-renewed plans ending from today through today + that many days
    /// (the list behind the dashboard's "Ending in 30 days" count).
    /// </summary>
    [HttpGet]
    public Task<PagedResult<PlanDto>> List(
        [FromQuery] PlanStatus? status, [FromQuery] PlanType? type,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] int? endingWithinDays,
        CancellationToken ct) =>
        plans.ListAsync(status, type, page, pageSize, endingWithinDays, ct);

    /// <summary>
    /// Renews a Due or Expired plan and records the payment, together. The body must carry the payment
    /// (paidOn, amount, mode). 409 "not_due_for_renewal" for an Active plan, 409 "plan_already_renewed",
    /// 422 "start_before_default". Returns the new plan and the payment.
    /// </summary>
    [HttpPost("{id:int}/renew")]
    public async Task<IActionResult> Renew(int id, RenewPlanRequest request, CancellationToken ct)
    {
        var renewal = await renewals.RenewAsync(id, request, ct);
        return StatusCode(StatusCodes.Status201Created, renewal);
    }

    /// <summary>The payments made for this plan, newest first. Read-only.</summary>
    [HttpGet("{id:int}/payments")]
    public Task<IReadOnlyList<PaymentDto>> Payments(int id, CancellationToken ct) =>
        plans.GetPaymentsAsync(id, ct);

    [HttpGet("{id:int}/reminders")]
    public Task<IReadOnlyList<ReminderDto>> Reminders(int id, CancellationToken ct) =>
        plans.GetRemindersAsync(id, ct);
}
