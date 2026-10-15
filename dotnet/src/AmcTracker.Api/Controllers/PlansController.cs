using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace AmcTracker.Api.Controllers;

[ApiController]
[Route("api/plans")]
public class PlansController(PlanService plans) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PlanDto>> List(
        [FromQuery] PlanStatus? status, [FromQuery] PlanType? type, CancellationToken ct) =>
        plans.ListAsync(status, type, ct);

    [HttpPost("{id:int}/renew")]
    public async Task<IActionResult> Renew(int id, RenewPlanRequest? request, CancellationToken ct)
    {
        var renewal = await plans.RenewAsync(id, request ?? new RenewPlanRequest(null, null), ct);
        return StatusCode(StatusCodes.Status201Created, renewal);
    }

    [HttpGet("{id:int}/reminders")]
    public Task<IReadOnlyList<ReminderDto>> Reminders(int id, CancellationToken ct) =>
        plans.GetRemindersAsync(id, ct);
}
