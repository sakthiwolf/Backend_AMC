using AmcTracker.Domain.Entities;

namespace AmcTracker.Application.Dtos;

public static class Mappings
{
    public static PlanDto ToDto(this Plan p, DateOnly today) => new(
        p.PlanId, p.CustomerId,
        p.Customer?.CustomerName ?? "", p.Customer?.Phone ?? "", p.Customer?.ProjectName,
        p.PlanType, p.Amount, p.StartDate, p.EndDate, p.NotificationDate,
        p.EffectiveStatus(today), p.PreviousPlanId, p.CreatedAt);

    public static CustomerDto ToDto(this Customer c) => new(
        c.CustomerId, c.CustomerName, c.Phone, c.Location, c.InstallationDate,
        // "+ 0.00m" keeps two decimals in the JSON (12345.60, not 12345.6).
        c.ProjectName, c.ProjectValue is { } v ? decimal.Round(v, 2) + 0.00m : null,
        c.WhatsAppOptOut, c.IsActive, c.CreatedAt, c.UpdatedAt, c.PlanType, c.PlanEndDate, c.ReminderDate);

    public static ReminderDto ToDto(this Reminder r) => new(
        r.ReminderId, r.PlanId, r.ScheduledDate, r.SentAt, r.Channel, r.Status, r.ErrorMessage);
}
