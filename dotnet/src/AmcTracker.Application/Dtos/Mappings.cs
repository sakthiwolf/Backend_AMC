using AmcTracker.Domain.Entities;

namespace AmcTracker.Application.Dtos;

public static class Mappings
{
    public static PlanDto ToDto(this Plan p, DateOnly today) => new(
        p.PlanId, p.CustomerId,
        p.Customer?.CustomerName ?? "", p.Customer?.Phone ?? "", p.Customer?.ProjectName,
        p.PlanType, p.Amount, p.StartDate, p.EndDate, p.NotificationDate,
        p.EffectiveStatus(today), p.PreviousPlanId, p.CreatedAt);

    public static CustomerDto ToDto(this Customer c, DateOnly today)
    {
        var latest = c.Plans.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.PlanId).FirstOrDefault();
        return new CustomerDto(
            c.CustomerId, c.CustomerName, c.Phone, c.Location, c.InstallationDate,
            c.ProjectName, c.ProjectValue, c.WhatsAppOptOut, c.IsActive, c.CreatedAt, c.UpdatedAt,
            latest?.EffectiveStatus(today));
    }

    public static ReminderDto ToDto(this Reminder r) => new(
        r.ReminderId, r.PlanId, r.ScheduledDate, r.SentAt, r.Channel, r.Status, r.ErrorMessage);
}
