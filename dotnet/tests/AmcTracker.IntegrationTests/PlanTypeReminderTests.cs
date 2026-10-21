using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>The AMC / Monthly choice on the customer form: stored on the customer, creates the plan, drives its dates.</summary>
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

    private static async Task<JsonElement> Create(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/customers", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Json();
    }

    [Fact]
    public async Task Plan_type_is_returned_and_defaults_to_AMC()
    {
        var c = await _api.SignedInAsync();
        var monthly = await Create(c, Body(_api.Today, "Monthly", "Monthly Co", "+919000000001"));
        var yearly = await Create(c, Body(_api.Today, "AMC", "Yearly Co", "+919000000002"));
        var omitted = await Create(c, Body(_api.Today, null, "Default Co", "+919000000003"));

        Assert.Equal("Monthly", monthly.GetProperty("planType").GetString());
        Assert.Equal("AMC", yearly.GetProperty("planType").GetString());
        Assert.Equal("AMC", omitted.GetProperty("planType").GetString());
    }

    [Fact]
    public async Task Creating_a_customer_creates_their_plan_of_that_type()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(new DateOnly(2026, 10, 12), "Monthly", value: 5000m))).GetProperty("customerId").GetInt32();

        var plan = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.CustomerId == id));
        Assert.Equal(PlanType.Monthly, plan.PlanType);
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 5)),
            (plan.StartDate, plan.EndDate, plan.ReminderDate));
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
    public async Task Customer_shows_the_calculated_end_date_and_reminder_date()
    {
        var c = await _api.SignedInAsync();
        var start = new DateOnly(2026, 10, 12);
        var yearly = await Create(c, Body(start, "AMC", "Yearly Co", "+919000000021"));
        Assert.Equal(("2027-10-12", "2027-09-12"),
            (yearly.GetProperty("planEndDate").GetString(), yearly.GetProperty("reminderDate").GetString()));

        var monthly = await Create(c, Body(start, "Monthly", "Monthly Co", "+919000000022"));
        Assert.Equal(("2026-11-12", "2026-11-05"),
            (monthly.GetProperty("planEndDate").GetString(), monthly.GetProperty("reminderDate").GetString()));
    }

    [Fact]
    public async Task Switching_the_type_in_an_edit_recalculates_the_plan_and_keeps_one_plan()
    {
        var c = await _api.SignedInAsync();
        var start = new DateOnly(2026, 10, 12);
        var id = (await Create(c, Body(start, "AMC"))).GetProperty("customerId").GetInt32();

        var put = await c.PutAsJsonAsync($"/api/customers/{id}", Body(start, "Monthly"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var json = await put.Json();
        Assert.Equal(("Monthly", "2026-11-12"), (json.GetProperty("planType").GetString(), json.GetProperty("planEndDate").GetString()));

        var plan = Assert.Single(await _api.WithDbAsync(db => db.Plans.Where(p => p.CustomerId == id).ToListAsync()));
        Assert.Equal((PlanType.Monthly, new DateOnly(2026, 11, 12)), (plan.PlanType, plan.EndDate));

        // Leaving the type out keeps it.
        var again = await c.PutAsJsonAsync($"/api/customers/{id}", Body(start, null));
        Assert.Equal("Monthly", (await again.Json()).GetProperty("planType").GetString());
    }

    [Fact]
    public async Task Switching_to_a_type_that_already_has_an_open_plan_is_409()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(_api.Today, "AMC"))).GetProperty("customerId").GetInt32();
        await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = "Monthly", amount = 100m });

        var put = await c.PutAsJsonAsync($"/api/customers/{id}", Body(_api.Today, "Monthly"));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("overlapping_plan", await put.ErrorCode());
    }

    [Fact]
    public async Task Editing_the_installation_date_recalculates_the_first_plan()
    {
        var c = await _api.SignedInAsync();
        var id = (await Create(c, Body(_api.Today, "AMC"))).GetProperty("customerId").GetInt32();
        var newStart = new DateOnly(2028, 2, 29);
        _api.Clock.Fixed = new DateOnly(2027, 6, 1); // so the new date is not more than a year ahead

        var json = await (await c.PutAsJsonAsync($"/api/customers/{id}", Body(newStart, "AMC"))).Json();
        Assert.Equal("2029-02-28", json.GetProperty("planEndDate").GetString());

        var plan = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.CustomerId == id));
        Assert.Equal((newStart, new DateOnly(2029, 2, 28), new DateOnly(2029, 1, 28)),
            (plan.StartDate, plan.EndDate, plan.ReminderDate));
    }
}
