using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Services;
using FluentValidation;
using FluentValidation.Results;

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
        Check(await createValidator.ValidateAsync(req, ct));
        var c = new Customer { CreatedAt = clock.UtcNow, IsActive = true };
        Apply(c, req);
        customers.Add(c);
        AddPlan(c); // the plan is what the reminders run on
        await uow.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task<CustomerDto> UpdateAsync(int id, UpdateCustomerRequest req, CancellationToken ct = default)
    {
        Check(await updateValidator.ValidateAsync(req, ct));
        var c = await customers.GetByIdAsync(id, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        Apply(c, req);
        c.UpdatedAt = clock.UtcNow;

        // Switching Monthly <-> Yearly changes the plan the reminders run on.
        var plan = await plans.GetLatestOpenForCustomerAsync(c.CustomerId, ct);
        if (plan is null) AddPlan(c); // customers created before plans existed
        else if (plan.PlanType != c.PlanType) plan.PlanType = c.PlanType;

        await uow.SaveChangesAsync(ct);
        return c.ToDto();
    }

    /// <summary>
    /// First plan for a customer: starts on the installation date. The amount is the project value
    /// (or 0) until open decision 1 in CLAUDE.md (what a Monthly amount means) is settled.
    /// </summary>
    private void AddPlan(Customer c)
    {
        var plan = Plan.Create(
            c.CustomerId, c.PlanType, c.ProjectValue ?? 0m, c.InstallationDate, clock.Today);
        plan.CreatedAt = clock.UtcNow;
        plan.Customer = c;
        plans.Add(plan);
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

    /// <summary>Turns validation failures into the 422 "validation_failed" error with per-field details.</summary>
    private static void Check(ValidationResult result)
    {
        if (result.IsValid) return;
        throw AppException.ValidationFailed(result.Errors
            .Select(e => new FieldError(
                char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..],
                e.ErrorMessage,
                e.ErrorCode is "PredicateValidator" or null ? "invalid" : e.ErrorCode))
            .ToList());
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
