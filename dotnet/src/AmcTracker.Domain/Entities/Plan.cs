using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;

namespace AmcTracker.Domain.Entities;

public class Plan
{
    public int PlanId { get; set; }
    public int CustomerId { get; set; }
    public PlanType PlanType { get; set; }
    // TODO(open decision 1, CLAUDE.md section 9): for Monthly plans this is stored as given.
    // Whether it is the monthly rate (total = Amount x 12) or the contract total is unconfirmed.
    public decimal Amount { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateOnly NotificationDate { get; set; }
    /// <summary>
    /// Only <see cref="PlanStatus.Renewed"/> is authoritative here. The other values are
    /// derived from dates (see <see cref="PlanStatusCalculator"/>) and synced by the daily job.
    /// </summary>
    public PlanStatus Status { get; set; }
    public int? PreviousPlanId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Customer? Customer { get; set; }
    public Plan? PreviousPlan { get; set; }
    public List<Reminder> Reminders { get; set; } = [];

    public bool IsRenewed => Status == PlanStatus.Renewed;

    /// <summary>Builds a plan with its calculated end and notification dates.</summary>
    public static Plan Create(
        int customerId, PlanType type, decimal amount, DateOnly startDate,
        DateOnly today, int? previousPlanId = null)
    {
        var endDate = PlanDateCalculator.EndDate(startDate);
        var notificationDate = PlanDateCalculator.NotificationDate(startDate);
        return new Plan
        {
            CustomerId = customerId,
            PlanType = type,
            Amount = amount,
            StartDate = startDate,
            EndDate = endDate,
            NotificationDate = notificationDate,
            Status = PlanStatusCalculator.Resolve(today, notificationDate, endDate, isRenewed: false),
            PreviousPlanId = previousPlanId,
        };
    }

    /// <summary>Moves a plan to a new start date and recalculates its end and notification dates.</summary>
    public void Reschedule(DateOnly startDate, DateOnly today)
    {
        StartDate = startDate;
        EndDate = PlanDateCalculator.EndDate(startDate);
        NotificationDate = PlanDateCalculator.NotificationDate(startDate);
        if (!IsRenewed) Status = PlanStatusCalculator.Resolve(today, NotificationDate, EndDate, isRenewed: false);
    }

    public PlanStatus EffectiveStatus(DateOnly today) =>
        PlanStatusCalculator.Resolve(today, NotificationDate, EndDate, IsRenewed);

    /// <summary>
    /// Creates the next-cycle plan and marks this one Renewed (CLAUDE.md 4.4).
    /// Start defaults to this plan's EndDate + 1 day; staff may override it.
    /// </summary>
    public Plan Renew(DateOnly today, DateOnly? startDate, decimal? amount)
    {
        if (IsRenewed)
            throw new InvalidOperationException("Plan has already been renewed.");
        var renewal = Create(
            CustomerId, PlanType,
            amount ?? Amount, // TODO(open decision 2): editable on renewal, or always copied?
            startDate ?? PlanDateCalculator.DefaultRenewalStart(EndDate),
            today, PlanId);
        Status = PlanStatus.Renewed;
        return renewal;
    }
}
