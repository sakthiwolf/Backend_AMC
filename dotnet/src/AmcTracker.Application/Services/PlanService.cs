using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using FluentValidation;

namespace AmcTracker.Application.Services;

/// <summary>Creating and listing plans. Dates come from the plan type (PlanScheduleCalculator), never from callers.</summary>
public class PlanService(
    IPlanRepository plans,
    ICustomerRepository customers,
    IReminderRepository reminders,
    IPaymentRepository payments,
    IUnitOfWork uow,
    IClock clock,
    IValidator<CreatePlanRequest> createValidator)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public async Task<PagedResult<PlanDto>> ListAsync(
        PlanStatus? status, PlanType? type, int? page, int? pageSize, int? endingWithinDays = null,
        CancellationToken ct = default)
    {
        if (endingWithinDays is < 1 or > 366)
            throw new AppException(422, "validation_error", "Invalid or missing fields: endingWithinDays (1 to 366).");
        var p = Math.Max(page ?? 1, 1);
        var size = pageSize is null or <= 0 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        var today = clock.Today;
        var (items, total) = await plans.PageAsync(new PlanFilter(status, type, EndingWithinDays: endingWithinDays), today, p, size, ct);
        return new PagedResult<PlanDto>(items.Select(x => x.ToDto(today)).ToList(), p, size, total);
    }

    /// <summary>All of a customer's plans, newest first (the current plan, then its history).</summary>
    public async Task<IReadOnlyList<PlanDto>> ListForCustomerAsync(int customerId, CancellationToken ct = default)
    {
        if (await customers.GetByIdAsync(customerId, includePlans: false, ct) is null)
            throw AppException.NotFound("Customer");
        var today = clock.Today;
        return (await plans.ListForCustomerAsync(customerId, ct)).Select(p => p.ToDto(today)).ToList();
    }

    /// <summary>
    /// A new plan for a customer. Start defaults to the installation date. A customer may hold plans of both
    /// types, but only one non-renewed plan of each type: another one is a 409 "overlapping_plan".
    /// </summary>
    public async Task<PlanDto> CreateAsync(int customerId, CreatePlanRequest req, CancellationToken ct = default)
    {
        Validation.ThrowIfInvalid(await createValidator.ValidateAsync(req, ct));
        var customer = await customers.GetByIdAsync(customerId, includePlans: false, ct)
            ?? throw AppException.NotFound("Customer");
        if (!customer.IsActive)
            throw AppException.Conflict("customer_inactive", "This customer has been deleted.");

        var type = req.PlanType!.Value;
        if (await plans.HasOpenPlanAsync(customerId, type, exceptPlanId: null, ct))
            throw OverlappingPlan(type);

        var plan = Plan.Create(customer.CustomerId, type, req.Amount!.Value, req.StartDate ?? customer.InstallationDate);
        plan.CreatedAt = clock.UtcNow;
        plan.Customer = customer;
        plans.Add(plan);
        if (type == customer.PlanType) CustomerPlanDates.Set(customer, plan);
        await uow.SaveChangesAsync(ct);
        return plan.ToDto(clock.Today);
    }

    public async Task<IReadOnlyList<ReminderDto>> GetRemindersAsync(int planId, CancellationToken ct = default)
    {
        if (await plans.GetByIdAsync(planId, ct) is null) throw AppException.NotFound("Plan");
        var today = clock.Today;
        return (await reminders.ListForPlanAsync(planId, ct)).Select(r => r.ToDto(today)).ToList();
    }

    /// <summary>The payments made for one plan (recorded when it was renewed), newest first. Read-only.</summary>
    public async Task<IReadOnlyList<PaymentDto>> GetPaymentsAsync(int planId, CancellationToken ct = default)
    {
        if (await plans.GetByIdAsync(planId, ct) is null) throw AppException.NotFound("Plan");
        return (await payments.ListForPlanAsync(planId, ct)).Select(p => p.ToDto()).ToList();
    }

    internal static AppException OverlappingPlan(PlanType type) => AppException.Conflict(
        "overlapping_plan",
        $"This customer already has an open {type} plan. Renew that plan instead of adding another.");
}

/// <summary>The end and reminder dates shown on the customer come from their current plan.</summary>
internal static class CustomerPlanDates
{
    public static void Set(Customer customer, Plan plan)
    {
        customer.PlanEndDate = plan.EndDate;
        customer.ReminderDate = plan.ReminderDate;
    }
}
