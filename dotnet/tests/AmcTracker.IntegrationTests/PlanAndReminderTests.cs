using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

internal static class Plans
{
    public static string Iso(this DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static object Customer(DateOnly installed, string type = "AMC", string phone = "+919876543210",
        string name = "Asha Traders", bool optOut = false, decimal? value = 50000m) => new
    {
        customerName = name, phone, location = "Chennai", installationDate = installed.Iso(),
        projectName = "CCTV", projectValue = value, whatsAppOptOut = optOut, planType = type,
    };

    /// <summary>Creates a customer (and with it their first plan). Returns (customerId, planId).</summary>
    public static async Task<(int CustomerId, int PlanId)> AddCustomer(this HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/customers", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Json()).GetProperty("customerId").GetInt32();
        var plans = await (await c.GetAsync($"/api/customers/{id}/plans")).Json();
        return (id, plans[0].GetProperty("planId").GetInt32());
    }

    /// <summary>An installation date whose first plan of <paramref name="type"/> has its reminder on <paramref name="day"/>.</summary>
    public static DateOnly StartRemindingOn(PlanType type, DateOnly day)
    {
        var guess = type == PlanType.AMC ? day.AddMonths(1).AddYears(-1) : day.AddDays(7).AddMonths(-1);
        for (var start = guess.AddDays(-3); start <= guess.AddDays(3); start = start.AddDays(1))
            if (PlanScheduleCalculator.Calculate(type, start).ReminderDate == day) return start;
        throw new InvalidOperationException($"No {type} start reminds on {day}.");
    }

    public static async Task<JsonElement> Run(this HttpClient c)
    {
        var res = await c.PostAsync("/api/reminders/run", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Json();
    }

    public static async Task<JsonElement> Reminders(this HttpClient c, int planId) =>
        await (await c.GetAsync($"/api/plans/{planId}/reminders")).Json();
}

[Collection("Api")]
public class PlanEndpointTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public PlanEndpointTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Plan_routes_need_a_token()
    {
        var anon = _api.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/plans/1/renew", null)).StatusCode);
    }

    [Fact]
    public async Task First_plan_follows_the_type_rules_and_stores_the_amount_as_entered()
    {
        var c = await _api.SignedInAsync();
        var (amcCustomer, _) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 10, 12), "AMC", "+919000000001", "Yearly Co"));
        var (monthlyCustomer, _) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 10, 12), "Monthly", "+919000000002", "Monthly Co", value: 499.5m));

        var amc = (await (await c.GetAsync($"/api/customers/{amcCustomer}/plans")).Json())[0];
        Assert.Equal(("2026-10-12", "2027-10-12", "2027-09-12"),
            (amc.GetProperty("startDate").GetString(), amc.GetProperty("endDate").GetString(), amc.GetProperty("reminderDate").GetString()));
        Assert.Equal("50000.00", amc.GetProperty("amount").GetRawText()); // two decimals

        var monthly = (await (await c.GetAsync($"/api/customers/{monthlyCustomer}/plans")).Json())[0];
        Assert.Equal(("2026-11-12", "2026-11-05"), (monthly.GetProperty("endDate").GetString(), monthly.GetProperty("reminderDate").GetString()));
        Assert.Equal("499.50", monthly.GetProperty("amount").GetRawText()); // never multiplied (open decision 1)
        Assert.False(monthly.TryGetProperty("notificationDate", out _));
    }

    [Fact]
    public async Task Create_plan_defaults_to_the_installation_date_and_rejects_an_overlapping_open_plan()
    {
        var c = await _api.SignedInAsync();
        var (id, _) = await c.AddCustomer(Plans.Customer(new DateOnly(2027, 1, 31), "AMC")); // has an open AMC plan

        var monthly = await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = "Monthly", amount = 500m });
        Assert.Equal(HttpStatusCode.Created, monthly.StatusCode);
        var plan = await monthly.Json();
        Assert.Equal(("2027-01-31", "2027-02-28", "2027-02-21"), // month-end clamp
            (plan.GetProperty("startDate").GetString(), plan.GetProperty("endDate").GetString(), plan.GetProperty("reminderDate").GetString()));

        foreach (var type in new[] { "Monthly", "AMC" })
        {
            var again = await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = type, amount = 500m });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("overlapping_plan", await again.ErrorCode());
        }
    }

    [Theory]
    [InlineData("{\"planType\":\"Weekly\",\"amount\":100}")]
    [InlineData("{\"amount\":100}")]
    [InlineData("{\"planType\":\"AMC\",\"amount\":-1}")]
    [InlineData("{\"planType\":\"AMC\",\"amount\":10.555}")]
    [InlineData("{\"planType\":\"AMC\"}")]
    public async Task Invalid_plan_input_is_422(string json)
    {
        var c = await _api.SignedInAsync();
        var (id, _) = await c.AddCustomer(Plans.Customer(_api.Today, "AMC"));
        await _api.DropAutoPlan(id);

        var res = await c.PostAsync($"/api/customers/{id}/plans",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode)422, res.StatusCode);
    }

    [Fact]
    public async Task Start_date_more_than_a_year_ahead_is_422_and_unknown_customer_is_404()
    {
        var c = await _api.SignedInAsync();
        var (id, _) = await c.AddCustomer(Plans.Customer(_api.Today, "AMC"));
        await _api.DropAutoPlan(id);

        var far = await c.PostAsJsonAsync($"/api/customers/{id}/plans",
            new { planType = "AMC", amount = 1m, startDate = _api.Today.AddYears(1).AddDays(1).Iso() });
        Assert.Equal((HttpStatusCode)422, far.StatusCode);

        var missing = await c.PostAsJsonAsync("/api/customers/99999/plans", new { planType = "AMC", amount = 1m });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<HttpResponseMessage> Renew(HttpClient c, int planId, DateOnly paidOn, decimal amount = 12000m,
        string mode = "UPI", string? note = null, DateOnly? startDate = null, decimal? planAmount = null) =>
        await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new
        {
            paidOn = paidOn.Iso(), amount, mode, note, startDate = startDate?.Iso(), planAmount,
        });

    /// <summary>A customer whose AMC plan (from 31 Jan 2026) ends 31 Jan 2027; the clock is set to <paramref name="today"/>.</summary>
    private async Task<(HttpClient Client, int CustomerId, int PlanId)> AmcEnding31Jan2027(DateOnly today)
    {
        _api.Clock.Fixed = today;
        var c = await _api.SignedInAsync();
        var (id, planId) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 1, 31), "AMC"));
        return (c, id, planId);
    }

    [Fact]
    public async Task Paid_late_the_new_cycle_starts_on_the_payment_date_and_the_payment_is_returned()
    {
        var (c, id, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5)); // expired
        var res = await Renew(c, planId, new DateOnly(2027, 2, 3), 11000m, "Cash", "paid at the shop");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Json();

        var plan = body.GetProperty("plan");
        Assert.Equal(("2027-02-03", "2028-02-03", "2028-01-03"), (plan.GetProperty("startDate").GetString(),
            plan.GetProperty("endDate").GetString(), plan.GetProperty("reminderDate").GetString()));
        Assert.Equal("50000.00", plan.GetProperty("amount").GetRawText()); // copied; the payment amount is separate
        Assert.Equal(planId, plan.GetProperty("previousPlanId").GetInt32());

        var payment = body.GetProperty("payment");
        Assert.Equal(planId, payment.GetProperty("planId").GetInt32()); // the plan that was paid for
        Assert.Equal(("2027-02-03", "11000.00", "Cash", "paid at the shop"), (payment.GetProperty("paidOn").GetString(),
            payment.GetProperty("amount").GetRawText(), payment.GetProperty("mode").GetString(), payment.GetProperty("note").GetString()));

        // Newest first; the old plan is kept and reads as Renewed. The customer shows the new dates.
        var plans = await (await c.GetAsync($"/api/customers/{id}/plans")).Json();
        Assert.Equal(2, plans.GetArrayLength());
        Assert.Equal("Renewed", plans[1].GetProperty("status").GetString());
        var customer = await (await c.GetAsync($"/api/customers/{id}")).Json();
        Assert.Equal("2028-02-03", customer.GetProperty("planEndDate").GetString());
    }

    [Theory]
    [InlineData("2027-01-10", "2027-01-05", "2027-02-01")] // due, paid before the end: the day after the end
    [InlineData("2027-01-31", "2027-01-31", "2027-02-01")] // paid on the end date: the day after the end
    [InlineData("2027-01-31", "2027-01-10", "2027-02-01")] // paid well before the end
    [InlineData("2027-03-10", "2027-03-10", "2027-03-10")] // paid late, today: the payment date
    public async Task New_cycle_start_is_the_later_of_the_day_after_the_end_and_the_payment_date(
        string today, string paidOn, string expectedStart)
    {
        var (c, _, planId) = await AmcEnding31Jan2027(DateOnly.Parse(today));
        var res = await Renew(c, planId, DateOnly.Parse(paidOn));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(expectedStart, (await res.Json()).GetProperty("plan").GetProperty("startDate").GetString());
    }

    [Fact]
    public async Task Renewing_an_active_plan_is_409_not_due_for_renewal_and_records_no_payment()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2026, 6, 1)); // reminder is 31 Dec 2026
        var res = await Renew(c, planId, new DateOnly(2026, 6, 1));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("not_due_for_renewal", await res.ErrorCode());
        Assert.Equal(0, await _api.WithDbAsync(db => db.Payments.CountAsync()));
        Assert.Equal(1, await _api.WithDbAsync(db => db.Plans.CountAsync()));
    }

    [Fact]
    public async Task Renewing_twice_is_409_plan_already_renewed()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        Assert.Equal(HttpStatusCode.Created, (await Renew(c, planId, new DateOnly(2027, 2, 5))).StatusCode);

        var again = await Renew(c, planId, new DateOnly(2027, 2, 5));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("plan_already_renewed", await again.ErrorCode());
        Assert.Equal(1, await _api.WithDbAsync(db => db.Payments.CountAsync())); // no second payment
    }

    [Fact]
    public async Task A_start_date_override_before_the_default_is_422_and_after_it_is_accepted()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 10));
        // Paid 5 Feb: the default start is 5 Feb.
        var before = await Renew(c, planId, new DateOnly(2027, 2, 5), startDate: new DateOnly(2027, 2, 4));
        Assert.Equal((HttpStatusCode)422, before.StatusCode);
        Assert.Equal("start_before_default", await before.ErrorCode());
        Assert.Equal(0, await _api.WithDbAsync(db => db.Payments.CountAsync()));

        var after = await Renew(c, planId, new DateOnly(2027, 2, 5), startDate: new DateOnly(2027, 3, 1));
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
        Assert.Equal("2027-03-01", (await after.Json()).GetProperty("plan").GetProperty("startDate").GetString());
    }

    [Fact]
    public async Task A_monthly_renewal_is_one_month_with_the_reminder_seven_days_before_the_end()
    {
        _api.Clock.Fixed = new DateOnly(2026, 11, 14);
        var c = await _api.SignedInAsync();
        var (_, planId) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 10, 12), "Monthly", value: 499.5m)); // ended 12 Nov
        var res = await Renew(c, planId, new DateOnly(2026, 11, 14), 499.5m);
        var plan = (await res.Json()).GetProperty("plan");
        Assert.Equal(("2026-11-14", "2026-12-14", "2026-12-07"), (plan.GetProperty("startDate").GetString(),
            plan.GetProperty("endDate").GetString(), plan.GetProperty("reminderDate").GetString()));
        Assert.Equal("499.50", plan.GetProperty("amount").GetRawText());
    }

    [Fact]
    public async Task A_monthly_renewal_from_a_month_end_clamps()
    {
        _api.Clock.Fixed = new DateOnly(2027, 1, 31);
        var c = await _api.SignedInAsync();
        var (_, planId) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 12, 31), "Monthly")); // ends 31 Jan 2027
        var plan = (await (await Renew(c, planId, new DateOnly(2027, 1, 31))).Json()).GetProperty("plan");
        Assert.Equal(("2027-02-01", "2027-03-01", "2027-02-22"), (plan.GetProperty("startDate").GetString(),
            plan.GetProperty("endDate").GetString(), plan.GetProperty("reminderDate").GetString()));
    }

    [Fact]
    public async Task The_new_plan_amount_is_editable()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        var plan = (await (await Renew(c, planId, new DateOnly(2027, 2, 5), planAmount: 13500m)).Json()).GetProperty("plan");
        Assert.Equal("13500.00", plan.GetProperty("amount").GetRawText());
    }

    [Fact]
    public async Task Bad_payment_input_is_422_and_nothing_is_saved()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        var paid = new DateOnly(2027, 2, 5);
        var bad = new[]
        {
            await Renew(c, planId, paid, amount: 0m),                       // a payment must be above 0
            await Renew(c, planId, paid, amount: -10m),
            await Renew(c, planId, paid, mode: "Cheque"),                   // not Cash, UPI or Bank
            await Renew(c, planId, paid.AddDays(1)),                         // paid in the future
            await Renew(c, planId, paid, planAmount: -1m),                  // a negative plan amount
            await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { }), // no payment at all
        };
        foreach (var res in bad) Assert.Equal((HttpStatusCode)422, res.StatusCode);

        // The old empty-body form is gone: clients must send the payment.
        var empty = await c.PostAsync($"/api/plans/{planId}/renew", null);
        Assert.True((int)empty.StatusCode is 400 or 415 or 422, $"empty body gave {(int)empty.StatusCode}");

        Assert.Equal(0, await _api.WithDbAsync(db => db.Payments.CountAsync()));
        Assert.Equal(1, await _api.WithDbAsync(db => db.Plans.CountAsync()));
    }

    [Theory]
    [InlineData("Cheque", 1)]
    [InlineData("Cash", 0)]
    [InlineData("Cash", -1)]
    public async Task The_database_rejects_a_payment_with_a_bad_mode_or_amount(string mode, int amount)
    {
        var (_, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => _api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Payments (PlanId, PaidOn, Amount, Mode) VALUES ({0}, '2027-02-05', {1}, {2})",
                planId, amount, mode)));
    }

    [Fact]
    public async Task If_the_payment_cannot_be_saved_no_new_plan_is_saved_either()
    {
        var (c, id, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        // A trigger that makes every payment insert fail, to prove the renewal and the payment are one transaction.
        await _api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER trg_Payments_ForceFailure ON Payments AFTER INSERT AS THROW 50001, 'forced payment failure', 1;"));
        try
        {
            var res = await Renew(c, planId, new DateOnly(2027, 2, 5));
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            await _api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_Payments_ForceFailure"));
        }

        Assert.Equal(1, await _api.WithDbAsync(db => db.Plans.CountAsync(p => p.CustomerId == id))); // no new Plan row
        Assert.Equal(0, await _api.WithDbAsync(db => db.Payments.CountAsync()));
        // The plan was not left half-renewed: it can still be renewed.
        Assert.Equal(HttpStatusCode.Created, (await Renew(c, planId, new DateOnly(2027, 2, 5))).StatusCode);
    }

    [Fact]
    public async Task The_payments_endpoint_returns_only_that_plans_payments()
    {
        _api.Clock.Fixed = new DateOnly(2027, 2, 5);
        var c = await _api.SignedInAsync();
        var (_, planA) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 1, 31), "AMC", "+919000000031", "A Co"));
        var (_, planB) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 1, 31), "AMC", "+919000000032", "B Co"));
        var renewalA = (await (await Renew(c, planA, new DateOnly(2027, 2, 1), 100m)).Json())
            .GetProperty("plan").GetProperty("planId").GetInt32();
        await Renew(c, planB, new DateOnly(2027, 2, 2), 200m, "Bank");
        // A second cycle: plan A's renewal becomes due a year later and is paid again.
        _api.Clock.Fixed = new DateOnly(2028, 2, 5);
        await Renew(c, renewalA, new DateOnly(2028, 2, 4), 150m, "Cash");

        var a = await (await c.GetAsync($"/api/plans/{planA}/payments")).Json();
        var payment = Assert.Single(a.EnumerateArray());
        Assert.Equal((planA, "100.00", "UPI"), (payment.GetProperty("planId").GetInt32(),
            payment.GetProperty("amount").GetRawText(), payment.GetProperty("mode").GetString()));

        var second = await (await c.GetAsync($"/api/plans/{renewalA}/payments")).Json();
        Assert.Equal("150.00", Assert.Single(second.EnumerateArray()).GetProperty("amount").GetRawText());

        var b = await (await c.GetAsync($"/api/plans/{planB}/payments")).Json();
        Assert.Equal("200.00", Assert.Single(b.EnumerateArray()).GetProperty("amount").GetRawText());

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/plans/99999/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().GetAsync($"/api/plans/{planA}/payments")).StatusCode);
        // Payments are read-only through the API.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync($"/api/plans/{planA}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.PutAsJsonAsync($"/api/plans/{planA}/payments", new { })).StatusCode);
    }

    [Fact]
    public async Task Payments_come_back_newest_first()
    {
        var (c, _, planId) = await AmcEnding31Jan2027(new DateOnly(2027, 2, 5));
        await Renew(c, planId, new DateOnly(2027, 2, 1), 100m);
        // Two more payments recorded against the same plan by a manual database change.
        await _api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Payments (PlanId, PaidOn, Amount, Mode) VALUES ({0}, '2027-02-04', 300, 'Cash'), ({0}, '2027-01-20', 50, 'Bank')",
            planId));

        var list = await (await c.GetAsync($"/api/plans/{planId}/payments")).Json();
        Assert.Equal(["2027-02-04", "2027-02-01", "2027-01-20"],
            list.EnumerateArray().Select(p => p.GetProperty("paidOn").GetString()!).ToArray());
    }

    [Fact]
    public async Task Database_allows_only_one_renewal_per_plan()
    {
        var c = await _api.SignedInAsync();
        var (id, planId) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 1, 1), "AMC"));
        await Assert.ThrowsAsync<DbUpdateException>(() => _api.WithDbAsync(async db =>
        {
            for (var i = 0; i < 2; i++)
            {
                var p = Plan.Create(id, PlanType.AMC, 1m, new DateOnly(2027, 1, 2), planId);
                p.CreatedAt = DateTime.UtcNow;
                db.Plans.Add(p);
            }
            await db.SaveChangesAsync();
            return 0;
        }));
    }

    [Fact]
    public async Task Status_is_calculated_on_read_and_the_list_filters_and_pages()
    {
        var c = await _api.SignedInAsync();
        var today = _api.Today;
        await c.AddCustomer(Plans.Customer(Plans.StartRemindingOn(PlanType.Monthly, today), "Monthly", "+919000000011", "Due Co"));
        await c.AddCustomer(Plans.Customer(today.AddYears(-2), "Monthly", "+919000000012", "Old Co"));
        await c.AddCustomer(Plans.Customer(today, "Monthly", "+919000000013", "New Co"));
        var (_, renewedPlan) = await c.AddCustomer(Plans.Customer(today.AddMonths(-11), "AMC", "+919000000014", "Renewing Co"));
        await Renew(c, renewedPlan, today); // due since today, so it can be renewed

        async Task<string[]> Names(string query) =>
            (await (await c.GetAsync($"/api/plans?{query}")).Json()).GetProperty("items").EnumerateArray()
                .Select(p => p.GetProperty("customerName").GetString()!).Order().ToArray();

        Assert.Equal(["Due Co"], await Names("status=DueForRenewal"));
        Assert.Equal(["Old Co"], await Names("status=Expired"));
        Assert.Equal(["New Co", "Renewing Co"], await Names("status=Active"));
        Assert.Equal(["Renewing Co"], await Names("status=Renewed"));
        Assert.Equal(["Renewing Co", "Renewing Co"], await Names("type=AMC"));

        var page = await (await c.GetAsync("/api/plans?page=2&pageSize=2")).Json();
        Assert.Equal((2, 2, 5), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(),
            page.GetProperty("totalCount").GetInt32()));
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());

        var summary = await (await c.GetAsync("/api/dashboard/summary")).Json();
        Assert.Equal((2, 1, 1), (summary.GetProperty("activeCount").GetInt32(),
            summary.GetProperty("dueCount").GetInt32(), summary.GetProperty("expiredCount").GetInt32()));

        var upcoming = await (await c.GetAsync("/api/dashboard/upcoming?days=7")).Json();
        Assert.Equal(["Due Co"], upcoming.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("customerName").GetString()!).ToArray());
    }

    [Fact]
    public async Task Status_follows_the_clock_across_the_boundary_days()
    {
        var c = await _api.SignedInAsync();
        _api.Clock.Fixed = new DateOnly(2026, 10, 12);
        var (_, planId) = await c.AddCustomer(Plans.Customer(new DateOnly(2026, 10, 12), "Monthly")); // reminder 5 Nov, ends 12 Nov

        async Task<string> StatusOn(DateOnly day)
        {
            _api.Clock.Fixed = day;
            var list = await (await c.GetAsync("/api/plans")).Json();
            return list.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("planId").GetInt32() == planId)
                .GetProperty("status").GetString()!;
        }

        Assert.Equal("Active", await StatusOn(new DateOnly(2026, 11, 4)));
        Assert.Equal("DueForRenewal", await StatusOn(new DateOnly(2026, 11, 5)));
        Assert.Equal("DueForRenewal", await StatusOn(new DateOnly(2026, 11, 12)));
        Assert.Equal("Expired", await StatusOn(new DateOnly(2026, 11, 13)));
    }
}

[Collection("Api")]
public class ReminderJobTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private readonly ApiFixture _api;
    public ReminderJobTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A customer whose plan reminds on <paramref name="on"/>. Returns the plan id.</summary>
    private async Task<int> Seed(HttpClient c, DateOnly on, string phone, string name = "Due Co",
        PlanType type = PlanType.Monthly, bool optOut = false, bool deleted = false)
    {
        var (id, planId) = await c.AddCustomer(Plans.Customer(Plans.StartRemindingOn(type, on), type.ToString(), phone, name, optOut));
        if (deleted) await c.DeleteAsync($"/api/customers/{id}");
        return planId;
    }

    private async Task<HttpClient> At(DateOnly day)
    {
        _api.Clock.Fixed = day;
        return await _api.SignedInAsync();
    }

    [Fact]
    public async Task Sends_the_template_on_the_reminder_date_not_the_day_before_and_catches_up_a_missed_day()
    {
        var c = await At(Day.AddDays(-1));
        var planId = await Seed(c, Day, "+919000000021");
        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32()); // the day before: nothing

        _api.Clock.Fixed = Day;
        // A plan (created before it) whose reminder date was yesterday: the run that day never happened, so
        // today's run catches it up (CLAUDE.md 4.5, 3-day catch-up).
        var missed = await Seed(c, Day.AddDays(-1), "+919000000022", "Yesterday Co");
        var run = await c.Run();
        Assert.Equal((2, 2), (run.GetProperty("processed").GetInt32(), run.GetProperty("sent").GetInt32()));
        var call = Assert.Single(_api.Whatsapp.Calls, x => x.Phone == "+919000000021");
        Assert.Equal("amc_monthly_reminder", call.TemplateName);
        var end = PlanScheduleCalculator.Calculate(PlanType.Monthly, Plans.StartRemindingOn(PlanType.Monthly, Day)).EndDate;
        Assert.Equal(["Due Co", "CCTV", end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)], call.Parameters);

        var row = (await c.Reminders(planId))[0];
        Assert.Equal(("Sent", Day.Iso(), 1), (row.GetProperty("status").GetString(),
            row.GetProperty("scheduledDate").GetString(), row.GetProperty("attempts").GetInt32()));
        var caughtUp = (await c.Reminders(missed))[0];
        Assert.Equal(("Sent", Day.AddDays(-1).Iso()), (caughtUp.GetProperty("status").GetString(),
            caughtUp.GetProperty("scheduledDate").GetString())); // the row keeps the plan's reminder date

        _api.Clock.Fixed = Day.AddDays(1);
        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32()); // the day after: nothing new
        Assert.Equal(2, _api.Whatsapp.Calls.Count);
    }

    [Fact]
    public async Task Running_twice_on_the_same_day_sends_once()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000031", type: PlanType.AMC);

        Assert.Equal(1, (await c.Run()).GetProperty("sent").GetInt32());
        var second = await c.Run();
        // A plan with a Sent reminder is not even picked up again.
        Assert.Equal((0, 0), (second.GetProperty("sent").GetInt32(), second.GetProperty("processed").GetInt32()));
        Assert.Single(_api.Whatsapp.Calls);
        Assert.Equal(1, (await c.Reminders(planId)).GetArrayLength());
    }

    [Fact]
    public async Task Opted_out_and_deleted_customers_get_no_reminder_row()
    {
        var c = await At(Day);
        var optedOut = await Seed(c, Day, "+919000000041", "Quiet Co", optOut: true);
        var deleted = await Seed(c, Day, "+919000000042", "Gone Co", deleted: true);

        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32());
        Assert.Empty(_api.Whatsapp.Calls);
        Assert.Equal(0, await _api.WithDbAsync(db => db.Reminders.CountAsync(r => r.PlanId == optedOut || r.PlanId == deleted)));
    }

    [Fact]
    public async Task A_failure_is_retried_the_next_day_not_in_the_same_run_and_stops_after_five_attempts()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000051"); // Monthly: ends 7 days after the reminder
        _api.Whatsapp.FailWith = "HTTP 400: simulated failure";

        var first = await c.Run();
        Assert.Equal(1, first.GetProperty("failed").GetInt32());
        Assert.Single(_api.Whatsapp.Calls); // one attempt in the run, no loop

        for (var day = 1; day <= 4; day++)
        {
            _api.Clock.Fixed = Day.AddDays(day);
            Assert.Equal(1, (await c.Run()).GetProperty("failed").GetInt32());
        }
        var row = (await c.Reminders(planId))[0];
        Assert.Equal(("Failed", 5, false), (row.GetProperty("status").GetString(),
            row.GetProperty("attempts").GetInt32(), row.GetProperty("canRetry").GetBoolean()));
        Assert.Contains("simulated failure", row.GetProperty("errorMessage").GetString());

        _api.Clock.Fixed = Day.AddDays(5);
        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32());
        Assert.Equal(5, _api.Whatsapp.Calls.Count);
        Assert.Equal(1, (await c.Reminders(planId)).GetArrayLength()); // always the same row

        var retry = await c.PostAsync($"/api/reminders/{row.GetProperty("reminderId").GetInt32()}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("reminder_out_of_attempts", await retry.ErrorCode());
    }

    [Fact]
    public async Task A_failure_that_succeeds_the_next_day_updates_the_same_row()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000061");
        _api.Whatsapp.FailWith = "HTTP 500: down";
        await c.Run();

        _api.Whatsapp.FailWith = null;
        _api.Clock.Fixed = Day.AddDays(1);
        Assert.Equal(1, (await c.Run()).GetProperty("sent").GetInt32());

        var history = await c.Reminders(planId);
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal(("Sent", 2), (history[0].GetProperty("status").GetString(), history[0].GetProperty("attempts").GetInt32()));
    }

    [Fact]
    public async Task Failed_reminders_are_not_retried_once_the_plan_is_renewed()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000071");
        _api.Whatsapp.FailWith = "HTTP 500: down";
        await c.Run();
        await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { paidOn = Day.Iso(), amount = 100m, mode = "Cash" });

        _api.Clock.Fixed = Day.AddDays(1);
        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32());
        Assert.False((await c.Reminders(planId))[0].GetProperty("canRetry").GetBoolean());
    }

    [Fact]
    public async Task Staff_can_try_a_failed_reminder_again()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000081");
        _api.Whatsapp.FailWith = "HTTP 500: down";
        await c.Run();
        var reminderId = (await c.Reminders(planId))[0].GetProperty("reminderId").GetInt32();

        _api.Whatsapp.FailWith = null;
        var retry = await c.PostAsync($"/api/reminders/{reminderId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var dto = await retry.Json();
        Assert.Equal(("Sent", 2), (dto.GetProperty("status").GetString(), dto.GetProperty("attempts").GetInt32()));

        var again = await c.PostAsync($"/api/reminders/{reminderId}/retry", null);
        Assert.Equal("reminder_already_sent", await again.ErrorCode());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/reminders/99999/retry", null)).StatusCode);
    }

    [Fact]
    public async Task Run_is_for_admins_only()
    {
        _api.Clock.Fixed = Day;
        var clerk = await _api.SignedInAsync("clerk");
        var res = await clerk.PostAsync("/api/reminders/run", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("forbidden", await res.ErrorCode());
    }

    [Fact]
    public async Task Database_rejects_a_duplicate_reminder_for_the_same_plan_and_date()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+919000000091");
        await Assert.ThrowsAsync<DbUpdateException>(() => _api.WithDbAsync(async db =>
        {
            db.Reminders.Add(new Reminder { PlanId = planId, ScheduledDate = Day, Status = ReminderStatus.Sent });
            db.Reminders.Add(new Reminder { PlanId = planId, ScheduledDate = Day, Status = ReminderStatus.Sent });
            await db.SaveChangesAsync();
            return 0;
        }));
    }
}
