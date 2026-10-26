using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Entities;

/// <summary>
/// One row per plan and ScheduledDate (unique: the idempotency guarantee). ScheduledDate is the plan's
/// ReminderDate, so a failed send is retried by updating the same row on a later day.
/// </summary>
public class Reminder
{
    /// <summary>Total send attempts allowed for one reminder, the first one included.</summary>
    public const int MaxAttempts = 5;

    public int ReminderId { get; set; }
    public int PlanId { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public DateTime? SentAt { get; set; }
    public string Channel { get; set; } = "WhatsApp";
    public ReminderStatus Status { get; set; }
    /// <summary>How many sends were tried.</summary>
    public int Attempts { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>The provider's message id (e.g. "wamid...") once sent; null when the provider gave none.</summary>
    public string? ProviderMessageId { get; set; }
    /// <summary>True when the message went to the configured test phone instead of the customer.</summary>
    public bool IsTest { get; set; }

    public Plan? Plan { get; set; }

    /// <summary>
    /// A failed send may be tried again (by the next daily run or by staff) while attempts remain and the plan
    /// is still current: not renewed and not past its end date. Needs <see cref="Plan"/> and its renewal loaded.
    /// </summary>
    public bool CanRetry(DateOnly today) =>
        Status == ReminderStatus.Failed && Attempts < MaxAttempts
        && Plan is { IsRenewed: false } plan && today <= plan.EndDate;
}
