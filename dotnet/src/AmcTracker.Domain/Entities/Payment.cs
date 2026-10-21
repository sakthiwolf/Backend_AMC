using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Entities;

/// <summary>
/// A payment recorded when a plan is renewed. It belongs to the plan that was paid for (the plan being
/// renewed). The API only adds and reads payments: a correction is a new row or a manual database change.
/// </summary>
public class Payment
{
    public int PaymentId { get; set; }
    public int PlanId { get; set; }
    public DateOnly PaidOn { get; set; }
    public decimal Amount { get; set; }
    public PaymentMode Mode { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public Plan? Plan { get; set; }
}
