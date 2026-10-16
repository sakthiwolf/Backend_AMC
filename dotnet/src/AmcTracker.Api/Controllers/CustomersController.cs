using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AmcTracker.Api.Controllers;

[ApiController]
[Route("api/customers")]
public class CustomersController(CustomerService customers, PlanService plans) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CustomerDto>> List(
        [FromQuery] string? search, [FromQuery] bool? active, CancellationToken ct) =>
        customers.ListAsync(search, active, ct);

    [HttpPost]
    public async Task<IActionResult> Create(CustomerRequest request, CancellationToken ct)
    {
        var created = await customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.CustomerId }, created);
    }

    [HttpGet("{id:int}")]
    public Task<CustomerDetailDto> Get(int id, CancellationToken ct) => customers.GetAsync(id, ct);

    [HttpPut("{id:int}")]
    public Task<CustomerDto> Update(int id, CustomerRequest request, CancellationToken ct) =>
        customers.UpdateAsync(id, request, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await customers.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/plans")]
    public async Task<IActionResult> CreatePlan(int id, CreatePlanRequest request, CancellationToken ct)
    {
        var plan = await plans.CreateAsync(id, request, ct);
        return CreatedAtAction(nameof(Get), new { id }, plan);
    }
}
