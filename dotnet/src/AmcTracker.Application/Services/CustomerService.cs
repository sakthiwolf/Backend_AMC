using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using FluentValidation;

namespace AmcTracker.Application.Services;

public class CustomerService(
    ICustomerRepository customers,
    IUnitOfWork uow,
    IClock clock,
    IValidator<CustomerRequest> validator)
{
    public async Task<IReadOnlyList<CustomerDto>> ListAsync(
        string? search, bool? active, CancellationToken ct = default)
    {
        var today = clock.Today;
        var list = await customers.ListAsync(search, active, ct);
        return list.Select(c => c.ToDto(today)).ToList();
    }

    public async Task<CustomerDetailDto> GetAsync(int id, CancellationToken ct = default)
    {
        var c = await customers.GetByIdAsync(id, includePlans: true, ct)
            ?? throw AppException.NotFound("Customer");
        var today = clock.Today;
        foreach (var p in c.Plans) p.Customer = c;
        var plans = c.Plans.OrderByDescending(p => p.StartDate).Select(p => p.ToDto(today)).ToList();
        return new CustomerDetailDto(c.ToDto(today), plans);
    }

    public async Task<CustomerDto> CreateAsync(CustomerRequest req, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        var c = new Customer { CreatedAt = clock.UtcNow, IsActive = true };
        Apply(c, req);
        customers.Add(c);
        await uow.SaveChangesAsync(ct);
        return c.ToDto(clock.Today);
    }

    public async Task<CustomerDto> UpdateAsync(int id, CustomerRequest req, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(req, ct);
        var c = await customers.GetByIdAsync(id, includePlans: true, ct)
            ?? throw AppException.NotFound("Customer");
        Apply(c, req);
        if (req.IsActive is { } active) c.IsActive = active;
        c.UpdatedAt = clock.UtcNow;
        await uow.SaveChangesAsync(ct);
        return c.ToDto(clock.Today);
    }

    /// <summary>Soft delete: sets IsActive = false. Rows with plans or reminders are never removed.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var c = await customers.GetByIdAsync(id, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        if (!c.IsActive) return;
        c.IsActive = false;
        c.UpdatedAt = clock.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    private static void Apply(Customer c, CustomerRequest r)
    {
        c.CustomerName = r.CustomerName!.Trim();
        c.Phone = r.Phone!;
        c.Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim();
        c.InstallationDate = r.InstallationDate!.Value;
        c.ProjectName = string.IsNullOrWhiteSpace(r.ProjectName) ? null : r.ProjectName.Trim();
        c.ProjectValue = r.ProjectValue;
        c.WhatsAppOptOut = r.WhatsAppOptOut ?? false;
    }
}
