using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Services;
using FluentValidation;

namespace AmcTracker.Application.Services;

public class CustomerService(
    ICustomerRepository customers,
    IPlanRepository plans,
    IUnitOfWork uow,
    IClock clock,
    IValidator<CreateCustomerRequest> createValidator,
    IValidator<UpdateCustomerRequest> updateValidator) : ICustomerService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public async Task<PagedResult<CustomerDto>> SearchAsync(
        string? search, bool? active, int? page, int? pageSize, CancellationToken ct = default)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = pageSize is null or <= 0 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        var (items, total) = await customers.SearchAsync(search, active, p, size, ct);
        return new PagedResult<CustomerDto>(items.Select(c => c.ToDto()).ToList(), p, size, total);
    }

    public async Task<CustomerDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var c = await customers.GetByIdAsync(id, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        return c.ToDto();
    }

    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest req, CancellationToken ct = default)
    {
        Validation.ThrowIfInvalid(await createValidator.ValidateAsync(req, ct));
        var c = new Customer { CreatedAt = clock.UtcNow, IsActive = true };
        Apply(c, req);
        customers.Add(c);
        AddPlan(c); // the plan is what the reminders run on
        await uow.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task<CustomerDto> UpdateAsync(int id, UpdateCustomerRequest req, CancellationToken ct = default)
    {
        Validation.ThrowIfInvalid(await updateValidator.ValidateAsync(req, ct));
        var c = await customers.GetByIdAsync(id, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        var previousType = c.PlanType;
        Apply(c, req);
        c.UpdatedAt = clock.UtcNow;

        // The plan the customer form describes: their current plan of the type they had.
        var plan = await plans.GetCurrentForCustomerAsync(c.CustomerId, previousType, ct);
        if (plan is null) AddPlan(c); // customers created before plans existed
        else
        {
            // Switching AMC <-> Monthly changes the plan's length and reminder date.
            if (plan.PlanType != c.PlanType
                && await plans.HasOpenPlanAsync(c.CustomerId, c.PlanType, plan.PlanId, ct))
                throw PlanService.OverlappingPlan(c.PlanType);
            // The first plan follows the installation date; renewals keep their own start.
            var start = plan.PreviousPlanId is null ? c.InstallationDate : plan.StartDate;
            if (plan.PlanType != c.PlanType || plan.StartDate != start) plan.Reschedule(c.PlanType, start);
            CustomerPlanDates.Set(c, plan);
        }

        await uow.SaveChangesAsync(ct);
        return c.ToDto();
    }

    /// <summary>
    /// First plan for a customer: starts on the installation date. The amount is the project value
    /// (or 0) until open decision 1 in CLAUDE.md (what a Monthly amount means) is settled.
    /// </summary>
    private void AddPlan(Customer c)
    {
        var plan = Plan.Create(c.CustomerId, c.PlanType, c.ProjectValue ?? 0m, c.InstallationDate);
        plan.CreatedAt = clock.UtcNow;
        plan.Customer = c;
        plans.Add(plan);
        CustomerPlanDates.Set(c, plan);
    }

    /// <summary>Soft delete: sets IsActive = false. Rows with plans or reminders are never removed.</summary>
    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        var c = await customers.GetByIdAsync(id, includePlans: false, ct);
        if (c is null || !c.IsActive) throw AppException.NotFound("Customer");
        c.IsActive = false;
        c.UpdatedAt = clock.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    private static void Apply(Customer c, ICustomerFields r)
    {
        c.CustomerName = r.CustomerName!.Trim();
        c.Phone = PhoneNumber.Normalize(r.Phone);
        c.Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim();
        c.InstallationDate = r.InstallationDate!.Value;
        c.ProjectName = string.IsNullOrWhiteSpace(r.ProjectName) ? null : r.ProjectName.Trim();
        c.ProjectValue = r.ProjectValue;
        c.WhatsAppOptOut = r.WhatsAppOptOut ?? false;
        if (r.PlanType is { } type) c.PlanType = type; // omitted: AMC for new customers, unchanged for edits
    }
}
