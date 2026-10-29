using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Options;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

/// <summary>Keeps every formatted log line, so tests can check nothing personal is logged.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
}

public class ReminderServiceTests
{
    private const string CustomerPhone = "+919876543210";
    private static readonly DateOnly Today = new(2027, 9, 12);
    private readonly IPlanRepository _plans = Substitute.For<IPlanRepository>();
    private readonly IReminderRepository _reminders = Substitute.For<IReminderRepository>();
    private readonly IWhatsAppSender _sender = Substitute.For<IWhatsAppSender>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ListLogger<ReminderService> _log = new();
    private readonly ReminderOptions _options = new() { Enabled = true, TestPhoneOnly = false };
    private readonly WhatsAppTemplateOptions _templates = new();

    public ReminderServiceTests()
    {
        _clock.Today.Returns(Today);
        _clock.UtcNow.Returns(new DateTime(2027, 9, 12, 3, 30, 0, DateTimeKind.Utc));
        _reminders.ListRetryableAsync(Today, Arg.Any<CancellationToken>()).Returns([]);
        _reminders.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()).Returns(true);
        Succeeds();
    }

    private ReminderService Service() => new(_plans, _reminders, _sender, _uow, _clock,
        Microsoft.Extensions.Options.Options.Create(_options), Microsoft.Extensions.Options.Options.Create(_templates), _log);

    /// <summary>An AMC plan that started 12 Oct 2026: ends 12 Oct 2027, reminder 12 Sep 2027 (today).</summary>
    private static Plan DuePlan(int id = 1)
    {
        var plan = Plan.Create(10, PlanType.AMC, 12000m, new DateOnly(2026, 10, 12));
        plan.PlanId = id;
        plan.Customer = new Customer { CustomerId = 10, CustomerName = "Asha", Phone = CustomerPhone, ProjectName = "CCTV", IsActive = true };
        return plan;
    }

    private void Succeeds() => _sender
        .SendTemplateAsync(default!, default!, default!, default!, default)
        .ReturnsForAnyArgs(new WhatsAppSendResult(true, "wamid.1"));

    private void Fails(string error = "HTTP 500: down", bool retryable = true) => _sender
        .SendTemplateAsync(default!, default!, default!, default!, default)
        .ReturnsForAnyArgs(WhatsAppSendResult.Failed(error, retryable));

    private void Due(params Plan[] plans) =>
        _plans.ListRemindersDueAsync(Today.AddDays(-ReminderService.CatchUpDays), Today, Arg.Any<CancellationToken>())
            .Returns([.. plans]);

    [Fact]
    public async Task Inserts_Pending_then_sends_the_template_and_records_Sent()
    {
        Due(DuePlan());
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c =>
            {
                added = c.Arg<Reminder>();
                Assert.Equal(ReminderStatus.Pending, added.Status); // inserted before the send
            });

        var result = await Service().RunDailyAsync();

        Assert.Equal((1, 1, 0, 0), (result.Processed, result.Sent, result.Failed, result.Skipped));
        Assert.NotNull(added);
        Assert.Equal(ReminderStatus.Sent, added!.Status);
        Assert.Equal(Today, added.ScheduledDate);
        Assert.Equal(1, added.Attempts);
        Assert.NotNull(added.SentAt);
        await _sender.Received(1).SendTemplateAsync(CustomerPhone, "amc_template",
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "Asha", "CCTV", "12 Oct 2027" })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_plan_type_uses_its_own_template_from_configuration()
    {
        var monthly = Plan.Create(10, PlanType.Monthly, 500m, new DateOnly(2027, 8, 19)); // reminder 12 Sep 2027
        monthly.PlanId = 2;
        monthly.Customer = DuePlan().Customer;
        Due(DuePlan(1), monthly);

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync(Arg.Any<string>(), "amc_template",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sender.Received(1).SendTemplateAsync(Arg.Any<string>(), "monthly_template",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _templates.AMC = "yearly_v2";
        _templates.Monthly = "monthly_v2";
        _sender.ClearReceivedCalls();
        _reminders.GetAsync(default, default, default).ReturnsForAnyArgs((Reminder?)null);
        await Service().RunDailyAsync();
        await _sender.Received(1).SendTemplateAsync(Arg.Any<string>(), "yearly_v2",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sender.Received(1).SendTemplateAsync(Arg.Any<string>(), "monthly_v2",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_existing_row_for_the_plan_and_date_is_never_sent_again()
    {
        Due(DuePlan());
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>())
            .Returns(new Reminder { PlanId = 1, ScheduledDate = Today, Status = ReminderStatus.Sent, Attempts = 1 });

        var result = await Service().RunDailyAsync();

        Assert.Equal((1, 0), (result.Skipped, result.Sent));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task When_the_unique_key_blocks_the_insert_nothing_is_sent()
    {
        Due(DuePlan());
        _reminders.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Service().RunDailyAsync();

        Assert.Equal(1, result.Skipped);
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_failure_records_the_error_and_one_attempt_and_is_not_retried_in_the_same_run()
    {
        Due(DuePlan());
        Fails();
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        var result = await Service().RunDailyAsync();

        Assert.Equal((1, 0), (result.Failed, result.Sent));
        Assert.Equal(ReminderStatus.Failed, added!.Status);
        Assert.Equal(1, added.Attempts);
        Assert.Equal("HTTP 500: down", added.ErrorMessage);
        Assert.Null(added.SentAt);
        await _sender.ReceivedWithAnyArgs(1).SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_sender_exception_counts_as_a_failure()
    {
        Due(DuePlan());
        _sender.SendTemplateAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs<WhatsAppSendResult>(_ => throw new HttpRequestException("boom"));
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        var result = await Service().RunDailyAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal("Unexpected error (HttpRequestException).", added!.ErrorMessage);
    }

    [Fact]
    public async Task Failed_reminders_from_earlier_days_are_tried_once_each()
    {
        var plan = DuePlan();
        var failed = new Reminder
        {
            ReminderId = 5, PlanId = 1, ScheduledDate = Today.AddDays(-1),
            Status = ReminderStatus.Failed, Attempts = 2, ErrorMessage = "HTTP 500", Plan = plan,
        };
        _reminders.ListRetryableAsync(Today, Arg.Any<CancellationToken>()).Returns([failed]);
        Due();

        var result = await Service().RunDailyAsync();

        Assert.Equal((1, 1), (result.Processed, result.Sent));
        Assert.Equal(ReminderStatus.Sent, failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Null(failed.ErrorMessage);
    }

    [Fact]
    public async Task Test_mode_sends_only_to_the_test_phone()
    {
        _options.TestPhoneOnly = true;
        _options.TestPhone = "+911111111111";
        Due(DuePlan());

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync("+911111111111", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sender.DidNotReceive().SendTemplateAsync(CustomerPhone, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_mode_without_a_test_phone_sends_nothing_and_writes_nothing()
    {
        _options.TestPhoneOnly = true;
        Due(DuePlan());

        var result = await Service().RunDailyAsync();

        Assert.Equal(0, result.Processed);
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task Logs_carry_ids_only_never_the_phone_number_or_message()
    {
        Due(DuePlan(1), DuePlan(2));
        _sender.SendTemplateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true, "wamid.1", HttpStatus: 200),
                WhatsAppSendResult.Failed($"HTTP 400: invalid {CustomerPhone}", false, httpStatus: 400));

        await Service().RunDailyAsync();

        Assert.NotEmpty(_log.Lines);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("9876543210") || l.Contains("Asha") || l.Contains("CCTV"));
        Assert.Contains(_log.Lines, l => l.Contains("plan 1"));
    }

    // ---- Staff "Try again" ----

    private Reminder FailedReminder(int attempts = 1)
    {
        var reminder = new Reminder
        {
            ReminderId = 9, PlanId = 1, ScheduledDate = Today, Status = ReminderStatus.Failed,
            Attempts = attempts, ErrorMessage = "HTTP 400", Plan = DuePlan(),
        };
        _reminders.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(reminder);
        return reminder;
    }

    [Fact]
    public async Task Retry_sends_a_failed_reminder_and_returns_it_as_Sent()
    {
        FailedReminder(attempts: 2);

        var dto = await Service().RetryAsync(9);

        Assert.Equal((ReminderStatus.Sent, 3, false), (dto.Status, dto.Attempts, dto.CanRetry));
    }

    [Theory]
    [InlineData(5, "reminder_out_of_attempts")]
    public async Task Retry_is_refused_after_five_attempts(int attempts, string code)
    {
        FailedReminder(attempts);
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().RetryAsync(9));
        Assert.Equal((409, code), (ex.StatusCode, ex.Code));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Retry_is_refused_for_a_sent_reminder_and_an_opted_out_customer()
    {
        var reminder = FailedReminder();
        reminder.Status = ReminderStatus.Sent;
        Assert.Equal("reminder_already_sent", (await Assert.ThrowsAsync<AppException>(() => Service().RetryAsync(9))).Code);

        reminder.Status = ReminderStatus.Failed;
        reminder.Plan!.Customer!.WhatsAppOptOut = true;
        Assert.Equal("customer_opted_out", (await Assert.ThrowsAsync<AppException>(() => Service().RetryAsync(9))).Code);
    }

    [Fact]
    public async Task Retry_of_an_unknown_reminder_is_404()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().RetryAsync(404));
        Assert.Equal(404, ex.StatusCode);
    }

    // ---- Wacto wiring: enabled switch, catch-up, message id, test phone, non-retryable failures, test send ----

    [Fact]
    public void Reminders_are_off_and_test_phone_only_is_on_by_default()
    {
        var defaults = new ReminderOptions();
        Assert.False(defaults.Enabled);
        Assert.True(defaults.TestPhoneOnly);
        Assert.Equal(("amc_template", "monthly_template"),
            (new WhatsAppTemplateOptions().AMC, new WhatsAppTemplateOptions().Monthly));
    }

    [Fact]
    public async Task When_disabled_the_run_does_nothing_and_logs_one_line()
    {
        _options.Enabled = false;
        Due(DuePlan());

        var result = await Service().RunDailyAsync();

        Assert.Equal(0, result.Processed);
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
        await _plans.DidNotReceiveWithAnyArgs().ListRemindersDueAsync(default, default, default);
        await _reminders.DidNotReceiveWithAnyArgs().ListRetryableAsync(default, default);
        Assert.Equal("Reminder run skipped: Reminders:Enabled is false.", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task The_catch_up_window_is_the_last_three_days_and_today()
    {
        Due();
        await Service().RunDailyAsync();
        await _plans.Received(1).ListRemindersDueAsync(Today.AddDays(-3), Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_sent_reminder_keeps_the_provider_message_id_and_the_date_reads_like_12_Oct_2027()
    {
        Due(DuePlan());
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        await Service().RunDailyAsync();

        Assert.Equal(("wamid.1", false), (added!.ProviderMessageId, added.IsTest));
        await _sender.Received(1).SendTemplateAsync(CustomerPhone, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "Asha", "CCTV", "12 Oct 2027" })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("5 Nov 2026", ReminderService.FormatDate(new DateOnly(2026, 11, 5)));
        Assert.Equal("12 Sep 2027", ReminderService.FormatDate(new DateOnly(2027, 9, 12)));
    }

    [Fact]
    public async Task A_success_without_a_message_id_is_Sent_and_logs_a_warning_with_the_plan_id_only()
    {
        Due(DuePlan(7));
        _sender.SendTemplateAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new WhatsAppSendResult(true, null, HttpStatus: 200));
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        await Service().RunDailyAsync();

        Assert.Equal((ReminderStatus.Sent, (string?)null), (added!.Status, added.ProviderMessageId));
        Assert.Contains(_log.Lines, l => l == "WhatsApp accepted the reminder for plan 7 but returned no message id.");
    }

    [Fact]
    public async Task Test_phone_only_marks_the_row_as_a_test_and_keeps_the_real_plan()
    {
        _options.TestPhoneOnly = true;
        _options.TestPhone = "+910000000999";
        Due(DuePlan(3));
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        await Service().RunDailyAsync();

        Assert.Equal((3, true, ReminderStatus.Sent), (added!.PlanId, added.IsTest, added.Status));
        await _sender.Received(1).SendTemplateAsync("+910000000999", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_retryable_failure_uses_up_the_attempts_keeps_the_error_and_Try_again_refuses_it()
    {
        Due(DuePlan());
        Fails("HTTP 400: (#131026) Message undeliverable", retryable: false);
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        await Service().RunDailyAsync();

        Assert.Equal((ReminderStatus.Failed, Reminder.MaxAttempts), (added!.Status, added.Attempts));
        Assert.Equal("HTTP 400: (#131026) Message undeliverable", added.ErrorMessage);
        added.Plan = DuePlan();
        Assert.False(added.CanRetry(Today));

        _reminders.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(added);
        _sender.ClearReceivedCalls();
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().RetryAsync(9));
        Assert.Equal((409, "reminder_out_of_attempts"), (ex.StatusCode, ex.Code));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_retryable_failure_counts_one_attempt()
    {
        Due(DuePlan());
        Fails("HTTP 429: slow down", retryable: true);
        Reminder? added = null;
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => added = c.Arg<Reminder>());

        await Service().RunDailyAsync();

        Assert.Equal(1, added!.Attempts);
        added.Plan = DuePlan();
        Assert.True(added.CanRetry(Today));
    }

    [Fact]
    public async Task Test_send_without_a_test_phone_is_409_test_phone_not_set()
    {
        _options.TestPhone = "";
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().TestSendAsync(PlanType.AMC));
        Assert.Equal((409, "test_phone_not_set"), (ex.StatusCode, ex.Code));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Theory]
    [InlineData(PlanType.AMC, "amc_template")]
    [InlineData(PlanType.Monthly, "monthly_template")]
    public async Task Test_send_goes_to_the_test_phone_with_that_plan_types_template_and_stores_nothing(
        PlanType type, string template)
    {
        _options.TestPhone = "+910000000999";
        _options.Enabled = false; // a test send works even while the daily run is off

        var result = await Service().TestSendAsync(type);

        Assert.Equal((true, "wamid.1", (string?)null), (result.Success, result.ProviderMessageId, result.ErrorMessage));
        await _sender.Received(1).SendTemplateAsync("+910000000999", template,
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "Test Customer", "Test Project", "12 Oct 2027" })),
            "test", Arg.Any<CancellationToken>()); // today + 30 days; callback data "test"
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Test_send_reports_a_failure_with_its_message()
    {
        _options.TestPhone = "+910000000999";
        Fails("HTTP 401: invalid token", retryable: false);
        var result = await Service().TestSendAsync(PlanType.AMC);
        Assert.Equal((false, (string?)null, "HTTP 401: invalid token"),
            (result.Success, result.ProviderMessageId, result.ErrorMessage));
    }

    [Fact]
    public async Task Test_send_to_a_given_number_goes_there_even_without_a_test_phone_and_logs_it_masked()
    {
        _options.TestPhone = "";

        var result = await Service().TestSendAsync(PlanType.Monthly, " +91 90000-12345 ");

        Assert.True(result.Success);
        await _sender.Received(1).SendTemplateAsync("+919000012345", "monthly_template",
            Arg.Any<IReadOnlyList<string>>(), "test", Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("9000012345"));
        Assert.Contains(_log.Lines, l => l.Contains("ending 2345")); // last four digits only
        Assert.DoesNotContain(_log.Lines, l => l.Contains("+91"));
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Theory]
    [InlineData(" 90000 12345 ")]
    [InlineData("9000012345")]
    [InlineData("+919000012345")]
    public async Task Test_send_takes_a_10_digit_indian_mobile_as_plus_91(string phone)
    {
        await Service().TestSendAsync(PlanType.AMC, phone);
        await _sender.Received(1).SendTemplateAsync("+919000012345", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), "test", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("12345")]           // too short
    [InlineData("1234567890")]      // 10 digits but not a mobile number (starts with 1)
    [InlineData("+91 abc")]
    [InlineData("+0919000012345")]
    public async Task Test_send_to_a_bad_number_is_422_invalid_phone_and_sends_nothing(string phone)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().TestSendAsync(PlanType.AMC, phone));

        Assert.Equal((422, "phone", "invalid_phone"), (ex.StatusCode, ex.Details![0].Field, ex.Details[0].Code));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Test_send_returns_the_http_status()
    {
        _options.TestPhone = "+910000000999";
        _sender.SendTemplateAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new WhatsAppSendResult(true, "wamid.1", HttpStatus: 200));
        Assert.Equal(200, (await Service().TestSendAsync(PlanType.AMC)).HttpStatus);
    }

    [Fact]
    public async Task Test_send_with_the_fake_provider_is_409_provider_not_configured()
    {
        _options.TestPhone = "+910000000999";
        _sender.IsFake.Returns(true);

        var ex = await Assert.ThrowsAsync<AppException>(() => Service().TestSendAsync(PlanType.AMC));

        Assert.Equal((409, "provider_not_configured"), (ex.StatusCode, ex.Code));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task The_callback_data_is_the_reminder_id_only()
    {
        Due(DuePlan());
        _reminders.When(r => r.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()))
            .Do(c => c.Arg<Reminder>().ReminderId = 321); // the id the database gives the Pending row

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync(CustomerPhone, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), "321", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_project_name_is_sent_as_your_project(string? projectName)
    {
        var plan = DuePlan();
        plan.Customer!.ProjectName = projectName;
        Due(plan);

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync(CustomerPhone, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "Asha", "your project", "12 Oct 2027" })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_deprecated_TestMode_key_logs_a_warning_on_each_run()
    {
        _options.UsedDeprecatedTestMode = true;
        Due();
        await Service().RunDailyAsync();
        Assert.Contains(_log.Lines, l => l.Contains("Reminders:TestMode is deprecated"));
    }
}
