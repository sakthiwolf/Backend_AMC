using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Entities;

/// <summary>
/// One row per plan and ScheduledDate (unique). ScheduledDate is the plan's NotificationDate,
/// so a failed send is retried by updating the same row on a later day.
/// </summary>
public class Reminder
{
    public int ReminderId { get; set; }
    public int PlanId { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public DateTime? SentAt { get; set; }
    public string Channel { get; set; } = "WhatsApp";
    public ReminderStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    public Plan? Plan { get; set; }
}
