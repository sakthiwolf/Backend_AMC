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
    private readonly ReminderOptions _options = new();

    public ReminderServiceTests()
    {
        _clock.Today.Returns(Today);
        _clock.UtcNow.Returns(new DateTime(2027, 9, 12, 3, 30, 0, DateTimeKind.Utc));
        _reminders.ListRetryableAsync(Today, Arg.Any<CancellationToken>()).Returns([]);
        _reminders.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()).Returns(true);
        Succeeds();
    }

    private ReminderService Service() => new(_plans, _reminders, _sender, _uow, _clock,
        Microsoft.Extensions.Options.Options.Create(_options), _log);

    /// <summary>An AMC plan that started 12 Oct 2026: ends 12 Oct 2027, reminder 12 Sep 2027 (today).</summary>
    private static Plan DuePlan(int id = 1)
    {
        var plan = Plan.Create(10, PlanType.AMC, 12000m, new DateOnly(2026, 10, 12));
        plan.PlanId = id;
        plan.Customer = new Customer { CustomerId = 10, CustomerName = "Asha", Phone = CustomerPhone, ProjectName = "CCTV", IsActive = true };
        return plan;
    }

    private void Succeeds() => _sender
        .SendTemplateAsync(default!, default!, default!, default)
        .ReturnsForAnyArgs(new WhatsAppSendResult(true));

    private void Fails(string error = "HTTP 400: bad number") => _sender
        .SendTemplateAsync(default!, default!, default!, default)
        .ReturnsForAnyArgs(new WhatsAppSendResult(false, error));

    private void Due(params Plan[] plans) =>
        _plans.ListRemindersDueAsync(Today, Arg.Any<CancellationToken>()).Returns([.. plans]);

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
        await _sender.Received(1).SendTemplateAsync(CustomerPhone, "amc_renewal_reminder",
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "Asha", "CCTV", "12 Oct 2027" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Template_name_comes_from_configuration()
    {
        _options.TemplateName = "renewal_v2";
        Due(DuePlan());

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync(Arg.Any<string>(), "renewal_v2",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_existing_row_for_the_plan_and_date_is_never_sent_again()
    {
        Due(DuePlan());
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>())
            .Returns(new Reminder { PlanId = 1, ScheduledDate = Today, Status = ReminderStatus.Sent, Attempts = 1 });

        var result = await Service().RunDailyAsync();

        Assert.Equal((1, 0), (result.Skipped, result.Sent));
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default);
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task When_the_unique_key_blocks_the_insert_nothing_is_sent()
    {
        Due(DuePlan());
        _reminders.TryAddAsync(Arg.Any<Reminder>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Service().RunDailyAsync();

        Assert.Equal(1, result.Skipped);
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default);
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
        Assert.Equal("HTTP 400: bad number", added.ErrorMessage);
        Assert.Null(added.SentAt);
        await _sender.ReceivedWithAnyArgs(1).SendTemplateAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_sender_exception_counts_as_a_failure()
    {
        Due(DuePlan());
        _sender.SendTemplateAsync(default!, default!, default!, default)
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
        _options.TestMode = true;
        _options.TestPhone = "+911111111111";
        Due(DuePlan());

        await Service().RunDailyAsync();

        await _sender.Received(1).SendTemplateAsync("+911111111111", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _sender.DidNotReceive().SendTemplateAsync(CustomerPhone, Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_mode_without_a_test_phone_sends_nothing_and_writes_nothing()
    {
        _options.TestMode = true;
        Due(DuePlan());

        var result = await Service().RunDailyAsync();

        Assert.Equal(0, result.Processed);
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default);
        await _reminders.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task Logs_carry_ids_only_never_the_phone_number_or_message()
    {
        Due(DuePlan(1), DuePlan(2));
        _sender.SendTemplateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true), new WhatsAppSendResult(false, $"HTTP 400: invalid {CustomerPhone}"));

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
        await _sender.DidNotReceiveWithAnyArgs().SendTemplateAsync(default!, default!, default!, default);
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
}
