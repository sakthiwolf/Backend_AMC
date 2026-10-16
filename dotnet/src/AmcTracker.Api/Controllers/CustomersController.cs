using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AmcTracker.Api.Controllers;

/// <summary>Customer Master. Controllers only bind input, call the service and return the result.</summary>
[ApiController]
[Route("api/customers")]
public class CustomersController(ICustomerService customers, PlanService plans) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<CustomerDto>> List(
        [FromQuery] string? search, [FromQuery] bool? active,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        customers.SearchAsync(search, active, page, pageSize, ct);

    [HttpPost]
    public async Task<IActionResult> Create(CreateCustomerRequest request, CancellationToken ct)
    {
        var created = await customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.CustomerId }, created);
    }

    [HttpGet("{id:int}")]
    public Task<CustomerDto> Get(int id, CancellationToken ct) => customers.GetByIdAsync(id, ct);

    [HttpPut("{id:int}")]
    public Task<CustomerDto> Update(int id, UpdateCustomerRequest request, CancellationToken ct) =>
        customers.UpdateAsync(id, request, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await customers.SoftDeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/plans")]
    public async Task<IActionResult> CreatePlan(int id, CreatePlanRequest request, CancellationToken ct)
    {
        var plan = await plans.CreateAsync(id, request, ct);
        return CreatedAtAction(nameof(Get), new { id }, plan);
    }
}
