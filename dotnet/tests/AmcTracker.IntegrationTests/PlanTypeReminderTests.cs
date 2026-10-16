using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>Monthly / Yearly choice: stored on the customer, creates the plan, drives the reminder dates.</summary>
[Collection("Api")]
public class PlanTypeReminderTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public PlanTypeReminderTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static object Body(DateOnly installed, string? planType, string name = "Asha Traders",
        string phone = "+919876543210", decimal? value = 5000m) => new
    {
        customerName = name, phone, installationDate = installed.ToString("yyyy-MM-dd"),
        projectName = "CCTV", projectValue = value, planType,
    };

    private async Task<JsonElement> Create(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/customers", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Json();
    }

    private async Task<JsonElement> Run(HttpClient c) => await (await c.PostAsync("/api/reminders/run", null)).Json();

    /// <summary>An installation date whose first monthly reminder (7 days before the due date) is today.</summary>
    private DateOnly MonthlyStartRemindingToday()
    {
        for (var start = _api.Today.AddDays(7).AddMonths(-1).AddDays(-3);
             start <= _api.Today.AddDays(7).AddMonths(-1).AddDays(3); start = start.AddDays(1))
        {
            if (ReminderSchedule.MonthlyReminderDate(start.AddMonths(1)) == _api.Today) return start;
        }
        throw new InvalidOperationException("no start date found");
    }

    [Fact]
    public async Task Plan_type_is_returned_and_defaults_to_Yearly()
    {
        var c = await _api.SignedInAsync();
        var monthly = await Create(c, Body(_api.Today, "Monthly", "Monthly Co", "+919000000001"));
        var yearly = await Create(c, Body(_api.Today, "AMC", "Yearly Co", "+919000000002"));
        var omitted = await Create(c, Body(_api.Today, null, "Default Co", "+919000000003"));

        Assert.Equal("Monthly", monthly.GetProperty("planType").GetString());
        Assert.Equal("AMC", yearly.GetProperty("planType").GetString());
        Assert.Equal("AMC", omitted.GetProperty("planType").GetString());

        var got = await (await c.GetAsync($"/api/customers/{monthly.GetProperty("customerId").GetInt32()}")).Json();
        Assert.Equal("Monthly", got.GetProperty("planType").GetString());
    }

    [Fact]
    public async Task Creating_a_customer_also_creates_their_plan_of_that_type()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(new DateOnly(2026, 10, 12), "Monthly", value: 5000m)))
            .GetProperty("customerId").GetInt32();

        var plan = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.CustomerId == id));
        Assert.Equal(PlanType.Monthly, plan.PlanType);
        Assert.Equal(new DateOnly(2026, 10, 12), plan.StartDate);
        Assert.Equal(new DateOnly(2027, 10, 12), plan.EndDate);
        Assert.Equal(5000m, plan.Amount);
    }

    [Fact]
    public async Task Invalid_plan_type_is_rejected_with_422()
    {
        var c = await _api.SignedInAsync();
        var res = await c.PostAsJsonAsync("/api/customers", Body(_api.Today, "Weekly"));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
    }

    [Fact]
    public async Task Switching_the_type_in_an_edit_changes_the_plan_and_keeps_one_plan()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(_api.Today, "AMC"))).GetProperty("customerId").GetInt32();

        var put = await c.PutAsJsonAsync($"/api/customers/{id}", Body(_api.Today, "Monthly"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Monthly", (await put.Json()).GetProperty("planType").GetString());

        var plans = await _api.WithDbAsync(db => db.Plans.Where(p => p.CustomerId == id).ToListAsync());
        Assert.Equal(PlanType.Monthly, Assert.Single(plans).PlanType);

        // Leaving the type out keeps it.
        var again = await c.PutAsJsonAsync($"/api/customers/{id}", Body(_api.Today, null));
        Assert.Equal("Monthly", (await again.Json()).GetProperty("planType").GetString());
    }

    [Fact]
    public async Task Monthly_customer_is_reminded_seven_days_before_the_monthly_due_date_once()
    {
        var c = await _api.SignedInAsync();
        var start = MonthlyStartRemindingToday();
        await Create(c, Body(start, "Monthly"));

        var first = await Run(c);
        Assert.Equal((1, 1), (first.GetProperty("processed").GetInt32(), first.GetProperty("sent").GetInt32()));
        var message = Assert.Single(_api.Whatsapp.Sent);
        Assert.Equal(_api.Today.AddDays(7), message.EndDate); // the message names the monthly due date

        var row = await _api.WithDbAsync(db => db.Reminders.SingleAsync());
        Assert.Equal(_api.Today, row.ScheduledDate);
        Assert.Equal(ReminderStatus.Sent, row.Status);

        // Running again the same day sends nothing more.
        var second = await Run(c);
        Assert.Equal(0, second.GetProperty("sent").GetInt32());
        Assert.Single(_api.Whatsapp.Sent);
    }

    [Fact]
    public async Task Monthly_customer_is_not_reminded_on_other_days()
    {
        var c = await _api.SignedInAsync();
        // Installed today: the first reminder is a month away (minus 7 days).
        await Create(c, Body(_api.Today, "Monthly"));
        var run = await Run(c);
        Assert.Equal(0, run.GetProperty("sent").GetInt32());
        Assert.Empty(_api.Whatsapp.Sent);
    }

    [Fact]
    public async Task Yearly_customer_is_reminded_one_month_before_the_end_date()
    {
        var c = await _api.SignedInAsync();
        var start = _api.Today.AddMonths(-11); // reminder date is today
        await Create(c, Body(start, "AMC"));

        var run = await Run(c);
        Assert.Equal(1, run.GetProperty("sent").GetInt32());
        Assert.Equal(start.AddMonths(12), Assert.Single(_api.Whatsapp.Sent).EndDate); // the AMC end date

        // A yearly customer installed only 5 months ago is not reminded.
        _api.Whatsapp.Sent.Clear();
        await Create(c, Body(_api.Today.AddMonths(-5), "AMC", "Later Co", "+919000000009"));
        Assert.Equal(0, (await Run(c)).GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Opted_out_monthly_customer_is_never_messaged()
    {
        var c = await _api.SignedInAsync();
        await Create(c, new
        {
            customerName = "Quiet Co", phone = "+919000000004", installationDate = MonthlyStartRemindingToday().ToString("yyyy-MM-dd"),
            planType = "Monthly", whatsAppOptOut = true,
        });
        Assert.Equal(0, (await Run(c)).GetProperty("sent").GetInt32());
        Assert.Empty(_api.Whatsapp.Sent);
    }

    [Fact]
    public async Task Customer_shows_the_calculated_end_date_and_reminder_date()
    {
        var c = await _api.SignedInAsync();
        var start = _api.Today.AddMonths(-2);
        var yearly = await Create(c, Body(start, "AMC", "Yearly Co", "+919000000021"));
        Assert.Equal(start.AddMonths(12).ToString("yyyy-MM-dd"), yearly.GetProperty("planEndDate").GetString());
        Assert.Equal(start.AddMonths(11).ToString("yyyy-MM-dd"), yearly.GetProperty("reminderDate").GetString());

        var monthly = await Create(c, Body(start, "Monthly", "Monthly Co", "+919000000022"));
        Assert.Equal(start.AddMonths(12).ToString("yyyy-MM-dd"), monthly.GetProperty("planEndDate").GetString());
        var next = ReminderSchedule.MonthlyDueDates(start).Select(ReminderSchedule.MonthlyReminderDate).First(d => d >= _api.Today);
        Assert.Equal(next.ToString("yyyy-MM-dd"), monthly.GetProperty("reminderDate").GetString());
    }

    [Fact]
    public async Task Editing_the_installation_date_recalculates_the_dates_and_the_plan()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(_api.Today, "AMC"))).GetProperty("customerId").GetInt32();
        var newStart = _api.Today.AddMonths(-5);

        var put = await c.PutAsJsonAsync($"/api/customers/{id}", Body(newStart, "AMC"));
        var json = await put.Json();
        Assert.Equal(newStart.AddMonths(12).ToString("yyyy-MM-dd"), json.GetProperty("planEndDate").GetString());

        var plan = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.CustomerId == id));
        Assert.Equal(newStart, plan.StartDate);
        Assert.Equal(newStart.AddMonths(12), plan.EndDate);
    }

    [Fact]
    public async Task The_daily_run_rolls_a_monthly_customers_next_reminder_forward()
    {
        var c = await _api.SignedInAsync();
        var start = MonthlyStartRemindingToday();
        var id = (await Create(c, Body(start, "Monthly"))).GetProperty("customerId").GetInt32();
        Assert.Equal(_api.Today, (await _api.WithDbAsync(db => db.Customers.SingleAsync(x => x.CustomerId == id))).ReminderDate);

        await Run(c); // sends today's reminder, then refreshes the dates
        var after = await _api.WithDbAsync(db => db.Customers.SingleAsync(x => x.CustomerId == id));
        Assert.Equal(_api.Today, after.ReminderDate); // "today" still counts as the next date
        // Tomorrow the next one would be a month later; the refresh uses the same rule as the schedule:
        var plan = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.CustomerId == id));
        Assert.Equal(ReminderSchedule.NextScheduled(plan, _api.Today), after.ReminderDate);
    }
}
