using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Entities;

public class Customer
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    /// <summary>E.164, e.g. +919876543210.</summary>
    public string Phone { get; set; } = "";
    public string? Location { get; set; }
    /// <summary>Becomes the first plan's start date.</summary>
    public DateOnly InstallationDate { get; set; }
    public string? ProjectName { get; set; }
    public decimal? ProjectValue { get; set; }
    /// <summary>Yearly (AMC) or Monthly. Decides when WhatsApp reminders go out (see ReminderSchedule).</summary>
    public PlanType PlanType { get; set; } = PlanType.AMC;
    /// <summary>When the current plan ends (InstallationDate + 12 months for the first plan). Calculated, never typed.</summary>
    public DateOnly? PlanEndDate { get; set; }
    /// <summary>The next WhatsApp reminder date. Calculated; the daily job rolls it forward each month.</summary>
    public DateOnly? ReminderDate { get; set; }
    public bool WhatsAppOptOut { get; set; }
    /// <summary>Soft delete only: false means deleted.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<Plan> Plans { get; set; } = [];
}
