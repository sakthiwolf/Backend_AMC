namespace AmcTracker.Domain.Entities;

/// <summary>Phase 2: table only, no endpoints yet.</summary>
public class Payment
{
    public int PaymentId { get; set; }
    public int PlanId { get; set; }
    public DateOnly PaidOn { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Cash, UPI, Bank, etc.</summary>
    public string? Mode { get; set; }

    public Plan? Plan { get; set; }
}
