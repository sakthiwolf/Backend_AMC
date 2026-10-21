using System.Globalization;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Options;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AmcTracker.Application.Services;

/// <summary>
/// The daily WhatsApp reminder run (CLAUDE.md 4.5) and the staff "Try again" action.
/// <list type="number">
/// <item>Failed reminders from earlier runs are tried once more each (never in a loop), up to
/// <see cref="Reminder.MaxAttempts"/> attempts in all, while the plan is neither renewed nor ended.</item>
/// <item>Plans whose ReminderDate is today get a Pending row first. The unique (PlanId, ScheduledDate) key
/// lets only one run insert it, and an existing row is never sent again.</item>
/// </list>
/// Logs carry plan and reminder ids only: never phone numbers or message text.
/// </summary>
public class ReminderService(
    IPlanRepository plans,
    IReminderRepository reminders,
    IWhatsAppSender sender,
    IUnitOfWork uow,
    IClock clock,
    IOptions<ReminderOptions> options,
    ILogger<ReminderService> logger)
{
    private const int MaxErrorLength = 500;
    private readonly ReminderOptions _opt = options.Value;

    public async Task<ReminderRunResult> RunDailyAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        if (TestModeWithoutPhone)
        {
            logger.LogWarning("Reminder run skipped: test mode is on and Reminders:TestPhone is not set.");
            return new ReminderRunResult(0, 0, 0, 0);
        }

        // Read both lists before sending anything, so a reminder that fails in this run is not retried in it.
        var retries = await reminders.ListRetryableAsync(today, ct);
        var due = await plans.ListRemindersDueAsync(today, ct);
        int sent = 0, failed = 0, skipped = 0;

        foreach (var reminder in retries)
        {
            if (await SendAsync(reminder, reminder.Plan!, ct)) sent++;
            else failed++;
        }

        foreach (var plan in due)
        {
            // Sent: never twice. Failed: retried above, or out of attempts. Pending: an earlier run stopped
            // mid-send and the message may have gone out, so it is not sent again.
            if (await reminders.GetAsync(plan.PlanId, plan.ReminderDate, ct) is not null) { skipped++; continue; }

            var reminder = new Reminder
            {
                PlanId = plan.PlanId,
                ScheduledDate = plan.ReminderDate,
                Channel = "WhatsApp",
                Status = ReminderStatus.Pending,
            };
            if (!await reminders.TryAddAsync(reminder, ct)) { skipped++; continue; } // another run got there first

            if (await SendAsync(reminder, plan, ct)) sent++;
            else failed++;
        }

        var processed = retries.Count + due.Count;
        logger.LogInformation(
            "Reminder run for {Today}: {Processed} processed, {Sent} sent, {Failed} failed, {Skipped} skipped.",
            today, processed, sent, failed, skipped);
        return new ReminderRunResult(processed, sent, failed, skipped);
    }

    /// <summary>Staff "Try again" on one failed reminder. Same rules as the daily retry.</summary>
    public async Task<ReminderDto> RetryAsync(int reminderId, CancellationToken ct = default)
    {
        var today = clock.Today;
        var reminder = await reminders.GetByIdAsync(reminderId, ct) ?? throw AppException.NotFound("Reminder");
        var plan = reminder.Plan!;
        var customer = plan.Customer!;

        if (reminder.Status == ReminderStatus.Sent)
            throw AppException.Conflict("reminder_already_sent", "This reminder has already been sent.");
        if (reminder.Status != ReminderStatus.Failed)
            throw AppException.Conflict("reminder_in_progress", "This reminder is still being sent.");
        if (reminder.Attempts >= Reminder.MaxAttempts)
            throw AppException.Conflict("reminder_out_of_attempts",
                $"This reminder has already been tried {Reminder.MaxAttempts} times.");
        if (!reminder.CanRetry(today))
            throw AppException.Conflict("plan_not_current", "The plan has been renewed or has ended.");
        if (!customer.IsActive)
            throw AppException.Conflict("customer_inactive", "This customer has been deleted.");
        if (customer.WhatsAppOptOut)
            throw AppException.Conflict("customer_opted_out", "This customer does not get WhatsApp reminders.");
        if (TestModeWithoutPhone)
            throw AppException.Conflict("test_mode_no_phone",
                "Test mode is on but no test phone number is set, so nothing can be sent.");

        await SendAsync(reminder, plan, ct);
        return reminder.ToDto(today);
    }

    private bool TestModeWithoutPhone => _opt.TestMode && string.IsNullOrWhiteSpace(_opt.TestPhone);

    /// <summary>One attempt: send, record the outcome on the row, save. True when sent.</summary>
    private async Task<bool> SendAsync(Reminder reminder, Plan plan, CancellationToken ct)
    {
        var customer = plan.Customer!;
        // Test mode: only ever the configured test number, never a customer.
        var to = _opt.TestMode ? _opt.TestPhone! : customer.Phone;
        string[] parameters =
        [
            customer.CustomerName,
            string.IsNullOrWhiteSpace(customer.ProjectName) ? "-" : customer.ProjectName,
            plan.EndDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
        ];

        WhatsAppSendResult result;
        try
        {
            result = await sender.SendTemplateAsync(to, _opt.TemplateName, parameters, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new WhatsAppSendResult(false, $"Unexpected error ({ex.GetType().Name}).");
        }

        reminder.Attempts++;
        if (result.Success)
        {
            reminder.Status = ReminderStatus.Sent;
            reminder.SentAt = clock.UtcNow;
            reminder.ErrorMessage = null;
            logger.LogInformation("Reminder {ReminderId} for plan {PlanId} sent on attempt {Attempt}.",
                reminder.ReminderId, plan.PlanId, reminder.Attempts);
        }
        else
        {
            reminder.Status = ReminderStatus.Failed;
            reminder.ErrorMessage = Truncate(result.Error ?? "Unknown error.");
            logger.LogWarning("Reminder {ReminderId} for plan {PlanId} failed on attempt {Attempt} of {Max}.",
                reminder.ReminderId, plan.PlanId, reminder.Attempts, Reminder.MaxAttempts);
        }
        await uow.SaveChangesAsync(ct);
        return result.Success;
    }

    private static string Truncate(string s) => s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];
}
