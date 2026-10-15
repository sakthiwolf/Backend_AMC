using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using FluentValidation;

namespace AmcTracker.Application.Services;

public class PlanService(
    IPlanRepository plans,
    ICustomerRepository customers,
    IReminderRepository reminders,
    IUnitOfWork uow,
    IClock clock,
    IValidator<CreatePlanRequest> createValidator,
    IValidator<RenewPlanRequest> renewValidator)
{
    public async Task<IReadOnlyList<PlanDto>> ListAsync(
        PlanStatus? status, PlanType? type, CancellationToken ct = default)
    {
        var today = clock.Today;
        var list = await plans.ListAsync(new PlanFilter(Status: status, Type: type), today, ct);
        return list.Select(p => p.ToDto(today)).ToList();
    }

    public async Task<PlanDto> CreateAsync(int customerId, CreatePlanRequest req, CancellationToken ct = default)
    {
        await createValidator.ValidateAndThrowAsync(req, ct);
        var customer = await customers.GetByIdAsync(customerId, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        if (!customer.IsActive)
            throw AppException.Conflict("customer_inactive", "This customer has been deleted.");

        var today = clock.Today;
        // StartDate = InstallationDate unless staff supply one.
        var plan = Plan.Create(
            customer.CustomerId, req.PlanType!.Value, req.Amount!.Value,
            req.StartDate ?? customer.InstallationDate, today);
        plan.CreatedAt = clock.UtcNow;
        plan.Customer = customer;
        plans.Add(plan);
        await uow.SaveChangesAsync(ct);
        return plan.ToDto(today);
    }

    public async Task<PlanDto> RenewAsync(int planId, RenewPlanRequest req, CancellationToken ct = default)
    {
        await renewValidator.ValidateAndThrowAsync(req, ct);
        var plan = await plans.GetByIdAsync(planId, ct) ?? throw AppException.NotFound("Plan");
        if (plan.IsRenewed)
            throw AppException.Conflict("already_renewed", "This plan has already been renewed.");

        var today = clock.Today;
        var renewal = plan.Renew(today, req.StartDate, req.Amount);
        renewal.CreatedAt = clock.UtcNow;
        renewal.Customer = plan.Customer;
        plans.Add(renewal);
        await uow.SaveChangesAsync(ct);
        return renewal.ToDto(today);
    }

    public async Task<IReadOnlyList<ReminderDto>> GetRemindersAsync(int planId, CancellationToken ct = default)
    {
        if (await plans.GetByIdAsync(planId, ct) is null) throw AppException.NotFound("Plan");
        var list = await reminders.ListForPlanAsync(planId, ct);
        return list.Select(r => r.ToDto()).ToList();
    }
}
