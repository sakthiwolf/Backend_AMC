using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using FluentValidation;

namespace AmcTracker.Application.Services;

/// <summary>
/// Renewal (CLAUDE.md 4.4): a new plan of the same type, linked through PreviousPlanId, plus the payment that
/// paid for the old plan. Allowed only while the plan is DueForRenewal or Expired. The old plan is never
/// changed or deleted; it reads as Renewed because the new plan points to it. A plan can be renewed once:
/// checked here, and enforced by the unique index on PreviousPlanId if two renewals race.
/// The renewal and its payment go in one SaveChanges, which is one database transaction: both or neither.
/// </summary>
public class RenewalService(
    IPlanRepository plans,
    IPaymentRepository payments,
    IUnitOfWork uow,
    IClock clock,
    IValidator<RenewPlanRequest> validator)
{
    public const string RenewalIndex = "IX_Plans_PreviousPlanId";

    public async Task<RenewalResultDto> RenewAsync(int planId, RenewPlanRequest req, CancellationToken ct = default)
    {
        Validation.ThrowIfInvalid(await validator.ValidateAsync(req, ct));
        var plan = await plans.GetByIdAsync(planId, ct) ?? throw AppException.NotFound("Plan");
        var today = clock.Today;

        if (plan.IsRenewed) throw AlreadyRenewed();
        if (plan.StatusOn(today) == PlanStatus.Active) throw NotDue(plan.PlanType);
        var customer = plan.Customer!;
        if (!customer.IsActive)
            throw AppException.Conflict("customer_inactive", "This customer has been deleted.");

        // The later of (previous end + 1 day) and the payment date. Staff may move it later, never earlier.
        var paidOn = req.PaidOn!.Value;
        var defaultStart = PlanScheduleCalculator.RenewalStart(plan.EndDate, paidOn);
        if (req.StartDate is { } chosen && chosen < defaultStart) throw StartBeforeDefault(defaultStart);

        var renewal = plan.Renew(req.StartDate ?? defaultStart, req.PlanAmount);
        renewal.CreatedAt = clock.UtcNow;
        renewal.Customer = customer;
        plans.Add(renewal);

        var payment = new Payment
        {
            Plan = plan, // the plan that was paid for
            PaidOn = paidOn,
            Amount = req.Amount!.Value,
            Mode = req.Mode!.Value,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            CreatedAt = clock.UtcNow,
        };
        payments.Add(payment);

        if (renewal.PlanType == customer.PlanType) CustomerPlanDates.Set(customer, renewal);

        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException e) when (e.IsFor(RenewalIndex))
        {
            throw AlreadyRenewed(); // another renewal of this plan was saved first
        }
        return new RenewalResultDto(renewal.ToDto(today), payment.ToDto());
    }

    private static AppException AlreadyRenewed() =>
        AppException.Conflict("plan_already_renewed", "This plan has already been renewed.");

    private static AppException NotDue(PlanType type) => AppException.Conflict(
        "not_due_for_renewal",
        "This plan is still active. It can be renewed from " + (type == PlanType.AMC
            ? $"{PlanScheduleCalculator.AmcReminderLeadMonths} month"
            : $"{PlanScheduleCalculator.MonthlyReminderLeadDays} days") + " before it ends.");

    private static AppException StartBeforeDefault(DateOnly defaultStart)
    {
        var message = $"The new cycle cannot start before {defaultStart:dd MMM yyyy}: " +
                      "the later of the day after the plan ends and the payment date.";
        return new AppException(422, "start_before_default", message)
        {
            Details = [new FieldError("startDate", message, "start_before_default")],
        };
    }
}
