using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AmcTracker.Application.Services;

/// <summary>
/// One pass of the daily WhatsApp reminder job (CLAUDE.md 4.5).
/// A reminder is open from its scheduled date until the date it is about (see <see cref="ReminderSchedule"/>):
/// yearly plans once, a month before the end; monthly plans every month, 7 days before the due date.
/// It stays open until a Sent row exists, so "retry on the next day's run" works: a Failed row is
/// retried once per run, never in a loop.
/// </summary>
public class ReminderService(
    IPlanRepository plans,
    IReminderRepository reminders,
    IWhatsAppSender sender,
    IUnitOfWork uow,
    IClock clock,
    ILogger<ReminderService> logger)
{
    private const int MaxErrorLength = 500;

    public async Task<ReminderRunResult> RunDailyAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        await plans.SyncStatusesAsync(today, ct);

        var due = await plans.ListPendingRemindersAsync(today, ct);
        int sent = 0, failed = 0, skipped = 0;

        foreach (var plan in due)
        {
            var customer = plan.Customer!;
            // Yearly: one month before the end. Monthly: 7 days before each monthly due date.
            if (ReminderSchedule.OpenOn(plan, today) is not { } open) { skipped++; continue; }
            var reminder = await reminders.GetAsync(plan.PlanId, open.ScheduledDate, ct);
            if (reminder?.Status == ReminderStatus.Sent) { skipped++; continue; } // idempotency

            if (reminder is null)
            {
                reminder = new Reminder
                {
                    PlanId = plan.PlanId,
                    ScheduledDate = open.ScheduledDate,
                    Channel = "WhatsApp",
                    Status = ReminderStatus.Pending,
                };
                reminders.Add(reminder);
                await uow.SaveChangesAsync(ct); // unique (PlanId, ScheduledDate) guards double inserts
            }

            WhatsAppSendResult result;
            try
            {
                result = await sender.SendRenewalReminderAsync(
                    new WhatsAppReminderMessage(
                        customer.Phone, customer.CustomerName, customer.ProjectName, open.DueOn), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new WhatsAppSendResult(false, ex.GetType().Name);
            }

            if (result.Success)
            {
                reminder.Status = ReminderStatus.Sent;
                reminder.SentAt = clock.UtcNow;
                reminder.ErrorMessage = null;
                sent++;
                logger.LogInformation("Reminder sent for plan {PlanId} to {Phone}",
                    plan.PlanId, PhoneNumber.Mask(customer.Phone));
            }
            else
            {
                reminder.Status = ReminderStatus.Failed;
                reminder.ErrorMessage = Truncate(result.Error ?? "Unknown error");
                failed++;
                logger.LogWarning("Reminder failed for plan {PlanId} to {Phone}: {Error}",
                    plan.PlanId, PhoneNumber.Mask(customer.Phone), reminder.ErrorMessage);
            }
            await uow.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Reminder run finished: {Processed} processed, {Sent} sent, {Failed} failed, {Skipped} skipped",
            due.Count, sent, failed, skipped);
        return new ReminderRunResult(due.Count, sent, failed, skipped);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];
}
