using System.Globalization;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Options;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AmcTracker.Application.Services;

/// <summary>
/// The daily WhatsApp reminder run (CLAUDE.md 4.5), the staff "Try again" action, and the admin test send.
/// <list type="number">
/// <item>Nothing happens while Reminders:Enabled is false.</item>
/// <item>Failed reminders from earlier runs are tried once more each (never in a loop), up to
/// <see cref="Reminder.MaxAttempts"/> attempts in all, while the plan is neither renewed nor ended. A failure the
/// provider says is not retryable uses up the attempts at once, so it is never tried again.</item>
/// <item>Plans whose ReminderDate is today or in the last <see cref="CatchUpDays"/> days get a Pending row
/// first. The unique (PlanId, ScheduledDate) key lets only one run insert it, and an existing row is never sent
/// again.</item>
/// </list>
/// Logs carry plan id, reminder id, HTTP status and duration only: never phone numbers, tokens or message text.
/// </summary>
public class ReminderService(
    IPlanRepository plans,
    IReminderRepository reminders,
    IWhatsAppSender sender,
    IUnitOfWork uow,
    IClock clock,
    IOptions<ReminderOptions> options,
    IOptions<WhatsAppTemplateOptions> templates,
    ILogger<ReminderService> logger)
{
    /// <summary>A reminder missed by up to this many days (the job did not run) is still sent.</summary>
    public const int CatchUpDays = 3;

    private readonly ReminderOptions _opt = options.Value;
    private readonly WhatsAppTemplateOptions _templates = templates.Value;

    public async Task<ReminderRunResult> RunDailyAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        if (!_opt.Enabled)
        {
            logger.LogInformation("Reminder run skipped: Reminders:Enabled is false.");
            return new ReminderRunResult(0, 0, 0, 0);
        }
        WarnIfDeprecatedKey();
        if (TestPhoneMissing)
        {
            logger.LogWarning("Reminder run skipped: Reminders:TestPhoneOnly is on and Reminders:TestPhone is not set.");
            return new ReminderRunResult(0, 0, 0, 0);
        }

        // Read both lists before sending anything, so a reminder that fails in this run is not retried in it.
        var retries = await reminders.ListRetryableAsync(today, ct);
        var due = await plans.ListRemindersDueAsync(today.AddDays(-CatchUpDays), today, ct);
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
                IsTest = _opt.TestPhoneOnly,
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
        if (TestPhoneMissing)
            throw AppException.Conflict("test_mode_no_phone",
                "Test mode is on but no test phone number is set, so nothing can be sent.");

        await SendAsync(reminder, plan, ct);
        return reminder.ToDto(today);
    }

    /// <summary>
    /// Admin check of the WhatsApp set-up: sends the template for <paramref name="planType"/> once, with sample
    /// values and callback data <see cref="TestCallbackData"/>, to <paramref name="phone"/> when given (E.164),
    /// otherwise to Reminders:TestPhone. Stores nothing. 422 for a bad number, 409 when there is no number at all.
    /// </summary>
    public async Task<TestSendResultDto> TestSendAsync(PlanType planType, string? phone = null, CancellationToken ct = default)
    {
        string to;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            to = PhoneNumber.Normalize(phone.Trim());
            // A plain 10-digit Indian mobile number (starts with 6 to 9) is taken as +91.
            if (to.Length == 10 && to[0] is >= '6' and <= '9' && to.All(char.IsAsciiDigit)) to = "+91" + to;
            if (!PhoneNumber.IsValidE164(to))
                throw AppException.ValidationFailed([new FieldError("phone",
                    "Use a 10-digit mobile number or the full number with country code, e.g. +919876543210.",
                    "invalid_phone")]);
        }
        else if (string.IsNullOrWhiteSpace(_opt.TestPhone))
            throw AppException.Conflict("test_phone_not_set",
                "Give a phone number, or set Reminders:TestPhone, so there is somewhere to send the test.");
        else
            to = _opt.TestPhone;
        if (sender.IsFake)
            throw AppException.Conflict("provider_not_configured",
                "WhatsApp:Provider is Fake, so a test send would not reach WhatsApp.");

        string[] parameters = ["Test Customer", "Test Project", FormatDate(clock.Today.AddDays(30))];
        var result = await SafeSendAsync(to, _templates.For(planType), parameters, TestCallbackData, ct);
        // Only the last four digits: never the country code or the full number.
        logger.LogInformation("Test send ({PlanType}) to a number ending {Last4}: {Outcome}, HTTP {Status}, {DurationMs} ms.",
            planType, to[^4..], result.Success ? "sent" : "failed", result.HttpStatus, Ms(result.Duration));
        return new TestSendResultDto(result.Success, result.ProviderMessageId, result.HttpStatus, result.ErrorMessage);
    }

    /// <summary>biz_opaque_callback_data of the admin test send (a real reminder sends its ReminderId).</summary>
    public const string TestCallbackData = "test";

    /// <summary>Template parameter 2 when the customer has no project name.</summary>
    public const string NoProjectName = "your project";

    /// <summary>The EndDate as the template shows it: "12 Sep 2027".</summary>
    public static string FormatDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private bool TestPhoneMissing => _opt.TestPhoneOnly && string.IsNullOrWhiteSpace(_opt.TestPhone);

    private void WarnIfDeprecatedKey()
    {
        if (_opt.UsedDeprecatedTestMode)
            logger.LogWarning("Reminders:TestMode is deprecated and will be removed; use Reminders:TestPhoneOnly.");
    }

    /// <summary>One attempt: send, record the outcome on the row, save. True when sent.</summary>
    private async Task<bool> SendAsync(Reminder reminder, Plan plan, CancellationToken ct)
    {
        var customer = plan.Customer!;
        // Test phone only: never a customer. The row still belongs to the real plan.
        var toTestPhone = _opt.TestPhoneOnly;
        var to = toTestPhone ? _opt.TestPhone! : customer.Phone;
        string[] parameters =
        [
            customer.CustomerName,
            string.IsNullOrWhiteSpace(customer.ProjectName) ? NoProjectName : customer.ProjectName,
            FormatDate(plan.EndDate),
        ];

        // The row is saved before sending, so its id exists; only the id goes to the provider.
        var result = await SafeSendAsync(to, _templates.For(plan.PlanType), parameters,
            reminder.ReminderId.ToString(CultureInfo.InvariantCulture), ct);

        reminder.Attempts++;
        reminder.IsTest = toTestPhone;
        if (result.Success)
        {
            reminder.Status = ReminderStatus.Sent;
            reminder.SentAt = clock.UtcNow;
            reminder.ErrorMessage = null;
            reminder.ProviderMessageId = result.ProviderMessageId;
            logger.LogInformation(
                "Reminder {ReminderId} for plan {PlanId} sent on attempt {Attempt}: HTTP {Status}, {DurationMs} ms.",
                reminder.ReminderId, plan.PlanId, reminder.Attempts, result.HttpStatus, Ms(result.Duration));
            if (result.ProviderMessageId is null)
                logger.LogWarning("WhatsApp accepted the reminder for plan {PlanId} but returned no message id.", plan.PlanId);
        }
        else
        {
            reminder.Status = ReminderStatus.Failed;
            reminder.ErrorMessage = Truncate(result.ErrorMessage ?? "Unknown error.");
            // Refused by the provider (bad number, unknown template...): trying again would fail the same way.
            if (!result.IsRetryable) reminder.Attempts = Reminder.MaxAttempts;
            logger.LogWarning(
                "Reminder {ReminderId} for plan {PlanId} failed on attempt {Attempt} of {Max}: HTTP {Status}, " +
                "{DurationMs} ms, retryable {Retryable}.",
                reminder.ReminderId, plan.PlanId, reminder.Attempts, Reminder.MaxAttempts, result.HttpStatus,
                Ms(result.Duration), result.IsRetryable);
        }
        await uow.SaveChangesAsync(ct);
        return result.Success;
    }

    private async Task<WhatsAppSendResult> SafeSendAsync(
        string to, string template, IReadOnlyList<string> parameters, string callbackData, CancellationToken ct)
    {
        try
        {
            return await sender.SendTemplateAsync(to, template, parameters, callbackData, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WhatsAppSendResult.Failed($"Unexpected error ({ex.GetType().Name}).", retryable: true);
        }
    }

    private static long? Ms(TimeSpan? d) => d is { } t ? (long)t.TotalMilliseconds : null;

    private static string Truncate(string s) =>
        s.Length <= WhatsAppSendResult.MaxErrorLength ? s : s[..WhatsAppSendResult.MaxErrorLength];
}
