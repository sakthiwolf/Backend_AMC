using AmcTracker.Domain.Entities;

namespace AmcTracker.Application.Dtos;

public static class Mappings
{
    /// <summary>The plan's status is worked out here, from <paramref name="today"/>; it is never stored.</summary>
    public static PlanDto ToDto(this Plan p, DateOnly today) => new(
        p.PlanId, p.CustomerId,
        p.Customer?.CustomerName ?? "", p.Customer?.Phone ?? "", p.Customer?.ProjectName,
        p.PlanType, Money(p.Amount), p.StartDate, p.EndDate, p.ReminderDate,
        p.StatusOn(today), p.PreviousPlanId, p.Renewal?.PlanId, p.CreatedAt);

    public static CustomerDto ToDto(this Customer c) => new(
        c.CustomerId, c.CustomerName, c.Phone, c.Location, c.InstallationDate,
        c.ProjectName, c.ProjectValue is { } v ? Money(v) : null,
        c.WhatsAppOptOut, c.IsActive, c.CreatedAt, c.UpdatedAt, c.PlanType, c.PlanEndDate, c.ReminderDate);

    /// <summary>Needs the reminder's plan (with its renewal) loaded for <see cref="ReminderDto.CanRetry"/>.</summary>
    public static ReminderDto ToDto(this Reminder r, DateOnly today) => new(
        r.ReminderId, r.PlanId, r.ScheduledDate, r.SentAt, r.Channel, r.Status, r.Attempts, r.ErrorMessage,
        r.CanRetry(today));

    public static PaymentDto ToDto(this Payment p) => new(
        p.PaymentId, p.PlanId, p.PaidOn, Money(p.Amount), p.Mode, p.Note, p.CreatedAt);

    /// <summary>"+ 0.00m" keeps two decimals in the JSON (12345.60, not 12345.6).</summary>
    private static decimal Money(decimal value) => decimal.Round(value, 2) + 0.00m;
}
