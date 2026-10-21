using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.Domain.Entities;

public class Plan
{
    public int PlanId { get; set; }
    public int CustomerId { get; set; }
    public PlanType PlanType { get; set; }
    // TODO(open decision 1, CLAUDE.md section 9): stored exactly as entered. Whether a Monthly amount is
    // the monthly rate or the contract total is unconfirmed, so no total is ever derived from it.
    public decimal Amount { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateOnly ReminderDate { get; set; }
    /// <summary>The plan this one renews, or null for a first plan.</summary>
    public int? PreviousPlanId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Customer? Customer { get; set; }
    public Plan? PreviousPlan { get; set; }
    /// <summary>The plan that renews this one, if any. Load it (Include) before reading <see cref="IsRenewed"/>.</summary>
    public Plan? Renewal { get; set; }
    public List<Reminder> Reminders { get; set; } = [];

    /// <summary>A renewal exists. Status is never stored; this is what "Renewed" means.</summary>
    public bool IsRenewed => Renewal is not null;

    /// <summary>Builds a plan with its end and reminder dates from <see cref="PlanScheduleCalculator"/>.</summary>
    public static Plan Create(
        int customerId, PlanType type, decimal amount, DateOnly startDate, int? previousPlanId = null)
    {
        var schedule = PlanScheduleCalculator.Calculate(type, startDate);
        return new Plan
        {
            CustomerId = customerId,
            PlanType = type,
            Amount = amount,
            StartDate = startDate,
            EndDate = schedule.EndDate,
            ReminderDate = schedule.ReminderDate,
            PreviousPlanId = previousPlanId,
        };
    }

    /// <summary>Moves a plan to a new type and/or start date and recalculates its dates.</summary>
    public void Reschedule(PlanType type, DateOnly startDate)
    {
        var schedule = PlanScheduleCalculator.Calculate(type, startDate);
        PlanType = type;
        StartDate = startDate;
        EndDate = schedule.EndDate;
        ReminderDate = schedule.ReminderDate;
    }

    public PlanStatus StatusOn(DateOnly today) =>
        PlanStatusCalculator.Resolve(today, ReminderDate, EndDate, IsRenewed);

    /// <summary>
    /// The next plan of the same type (CLAUDE.md 4.4), starting on <paramref name="startDate"/> (the caller works
    /// it out with <see cref="PlanScheduleCalculator.RenewalStart"/>). The amount is this plan's amount unless
    /// staff give another. This plan is never changed or deleted: it shows as Renewed because the new plan
    /// points back to it.
    /// </summary>
    public Plan Renew(DateOnly startDate, decimal? amount = null)
    {
        if (IsRenewed) throw new InvalidOperationException("Plan has already been renewed.");
        var renewal = Create(CustomerId, PlanType, amount ?? Amount, startDate, PlanId);
        renewal.PreviousPlan = this;
        Renewal = renewal;
        return renewal;
    }
}
