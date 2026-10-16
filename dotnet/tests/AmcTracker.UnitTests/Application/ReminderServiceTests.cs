using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class ReminderServiceTests
{
    private static readonly DateOnly Today = new(2027, 9, 12);
    private readonly IPlanRepository _plans = Substitute.For<IPlanRepository>();
    private readonly IReminderRepository _reminders = Substitute.For<IReminderRepository>();
    private readonly IWhatsAppSender _sender = Substitute.For<IWhatsAppSender>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ReminderService _service;

    public ReminderServiceTests()
    {
        _clock.Today.Returns(Today);
        _clock.UtcNow.Returns(new DateTime(2027, 9, 12, 3, 30, 0, DateTimeKind.Utc));
        _service = new ReminderService(_plans, _reminders, _sender, _uow, _clock,
            NullLogger<ReminderService>.Instance);
    }

    private static Plan DuePlan(int id = 1) => new()
    {
        PlanId = id,
        CustomerId = 10,
        NotificationDate = Today,
        EndDate = Today.AddMonths(1),
        Customer = new Customer { CustomerId = 10, CustomerName = "Asha", Phone = "+919876543210", ProjectName = "CCTV" },
    };

    [Fact]
    public async Task Sends_and_records_Sent()
    {
        var plan = DuePlan();
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([plan]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true));
        Reminder? added = null;
        _reminders.When(r => r.Add(Arg.Any<Reminder>())).Do(c => added = c.Arg<Reminder>());

        var result = await _service.RunDailyAsync();

        Assert.Equal(1, result.Sent);
        Assert.NotNull(added);
        Assert.Equal(ReminderStatus.Sent, added!.Status);
        Assert.Equal(plan.NotificationDate, added.ScheduledDate);
        Assert.NotNull(added.SentAt);
        await _sender.Received(1).SendRenewalReminderAsync(
            Arg.Is<WhatsAppReminderMessage>(m => m.CustomerName == "Asha" && m.EndDate == plan.EndDate),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Never_sends_twice_when_a_Sent_reminder_already_exists()
    {
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([DuePlan()]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>())
            .Returns(new Reminder { PlanId = 1, ScheduledDate = Today, Status = ReminderStatus.Sent });

        var result = await _service.RunDailyAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Sent);
        await _sender.DidNotReceiveWithAnyArgs().SendRenewalReminderAsync(default!, default);
    }

    [Fact]
    public async Task Failure_is_recorded_with_the_error_and_not_retried_in_a_loop()
    {
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([DuePlan()]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(false, "HTTP 400: bad number"));
        Reminder? added = null;
        _reminders.When(r => r.Add(Arg.Any<Reminder>())).Do(c => added = c.Arg<Reminder>());

        var result = await _service.RunDailyAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(ReminderStatus.Failed, added!.Status);
        Assert.Equal("HTTP 400: bad number", added.ErrorMessage);
        await _sender.Received(1).SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>()); // one attempt only
    }

    [Fact]
    public async Task Failed_reminder_is_retried_on_the_same_row_next_run()
    {
        var existing = new Reminder
        {
            ReminderId = 5, PlanId = 1, ScheduledDate = Today,
            Status = ReminderStatus.Failed, ErrorMessage = "old error",
        };
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([DuePlan()]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns(existing);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true));

        await _service.RunDailyAsync();

        Assert.Equal(ReminderStatus.Sent, existing.Status);
        Assert.Null(existing.ErrorMessage);
        _reminders.DidNotReceiveWithAnyArgs().Add(default!); // no duplicate row
    }

    [Fact]
    public async Task Sender_exception_is_recorded_as_Failed_without_leaking_details()
    {
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([DuePlan()]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns<WhatsAppSendResult>(_ => throw new InvalidOperationException("secret+919876543210"));
        Reminder? added = null;
        _reminders.When(r => r.Add(Arg.Any<Reminder>())).Do(c => added = c.Arg<Reminder>());

        var result = await _service.RunDailyAsync();

        Assert.Equal(1, result.Failed);
        Assert.DoesNotContain("9198765", added!.ErrorMessage);
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest()
    {
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([DuePlan(1), DuePlan(2)]);
        _reminders.GetAsync(Arg.Any<int>(), Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(false, "x"), new WhatsAppSendResult(true));

        var result = await _service.RunDailyAsync();

        Assert.Equal((2, 1, 1), (result.Processed, result.Sent, result.Failed));
    }

    // ---- Monthly plans: a reminder 7 days before each monthly due date ----

    private static Plan MonthlyPlan(DateOnly start, int id = 1) => new()
    {
        PlanId = id, CustomerId = 10, PlanType = PlanType.Monthly,
        StartDate = start, EndDate = start.AddMonths(12), NotificationDate = start.AddMonths(11),
        Customer = new Customer { CustomerId = 10, CustomerName = "Asha", Phone = "+919876543210", ProjectName = "CCTV" },
    };

    [Fact]
    public async Task Monthly_plan_gets_a_reminder_seven_days_before_the_monthly_due_date()
    {
        // Due dates are the 19th of each month; Today is 12 Sep 2027, so 19 Sep is due and 12 Sep is the reminder day.
        var plan = MonthlyPlan(new DateOnly(2026, 10, 19));
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([plan]);
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true));
        Reminder? added = null;
        _reminders.When(r => r.Add(Arg.Any<Reminder>())).Do(c => added = c.Arg<Reminder>());

        var result = await _service.RunDailyAsync();

        Assert.Equal(1, result.Sent);
        Assert.Equal(Today, added!.ScheduledDate);
        await _sender.Received(1).SendRenewalReminderAsync(
            Arg.Is<WhatsAppReminderMessage>(m => m.EndDate == new DateOnly(2027, 9, 19)), // the monthly due date
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Monthly_plan_is_skipped_on_days_with_no_open_reminder()
    {
        // Due on the 25th: the reminder day is the 18th, so the 12th is quiet.
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>())
            .Returns([MonthlyPlan(new DateOnly(2026, 10, 25))]);

        var result = await _service.RunDailyAsync();

        Assert.Equal((1, 0, 0, 1), (result.Processed, result.Sent, result.Failed, result.Skipped));
        await _sender.DidNotReceiveWithAnyArgs().SendRenewalReminderAsync(default!, default);
    }

    [Fact]
    public async Task Monthly_reminder_for_this_month_is_sent_once_even_if_an_older_month_was_sent()
    {
        var plan = MonthlyPlan(new DateOnly(2026, 10, 19));
        _plans.ListPendingRemindersAsync(Today, Arg.Any<CancellationToken>()).Returns([plan]);
        // Last month's row exists and is Sent; this month's does not exist yet.
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>()).Returns((Reminder?)null);
        _sender.SendRenewalReminderAsync(Arg.Any<WhatsAppReminderMessage>(), Arg.Any<CancellationToken>())
            .Returns(new WhatsAppSendResult(true));

        var first = await _service.RunDailyAsync();
        Assert.Equal(1, first.Sent);

        // Second run the same day: this month's reminder is now Sent, so nothing goes out again.
        _reminders.GetAsync(1, Today, Arg.Any<CancellationToken>())
            .Returns(new Reminder { PlanId = 1, ScheduledDate = Today, Status = ReminderStatus.Sent });
        var second = await _service.RunDailyAsync();
        Assert.Equal((0, 1), (second.Sent, second.Skipped));
    }
}
