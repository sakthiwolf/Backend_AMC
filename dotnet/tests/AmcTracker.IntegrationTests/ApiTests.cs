using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

internal static class Http
{
    /// <summary>
    /// Creating a customer also creates their first plan. Tests that exercise POST /customers/{id}/plans
    /// with their own type and amount remove that plan first.
    /// </summary>
    public static Task<int> DropAutoPlan(this ApiFixture api, int customerId) =>
        api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM Plans WHERE CustomerId = {0}", customerId));

    public static async Task<JsonElement> Json(this HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> ErrorCode(this HttpResponseMessage r) =>
        (await r.Json()).GetProperty("error").GetProperty("code").GetString()!;

    public static object CustomerBody(DateOnly installed, string phone = "+919876543210", string name = "Asha Traders") => new
    {
        customerName = name, phone, location = "Chennai", installationDate = installed.ToString("yyyy-MM-dd"),
        projectName = "CCTV", projectValue = 50000.00m, whatsAppOptOut = false,
    };
}

[Collection("Api")]
public class AuthTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public AuthTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Fp2 => "22222222-2222-4222-8222-222222222222";

    private Task<HttpResponseMessage> Login(HttpClient c, string user = "alice", string pw = ApiFixture.Password, string fp = ApiFixture.Fingerprint) =>
        c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pw, deviceFingerprint = fp });

    [Fact]
    public async Task Health_is_public_and_returns_ok()
    {
        var res = await _api.Anonymous().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ok", (await res.Json()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Login_returns_camelCase_tokens_and_me_works()
    {
        await _api.AddUserAsync("alice", "Alice A");
        var c = _api.Anonymous();
        var res = await Login(c);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Json();
        Assert.Equal("bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(8 * 3600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal("Alice A", body.GetProperty("user").GetProperty("fullName").GetString());
        Assert.True(body.GetProperty("refreshToken").GetString()!.Length >= 64);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        var me = await c.GetAsync("/api/auth/me");
        Assert.Equal("alice", (await me.Json()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Refresh_token_is_stored_only_as_a_hash()
    {
        await _api.AddUserAsync("alice");
        var token = (await (await Login(_api.Anonymous())).Json()).GetProperty("refreshToken").GetString()!;
        var stored = await _api.WithDbAsync(db => db.UserDevices.Select(d => d.RefreshTokenHash).SingleAsync());
        Assert.NotEqual(token, stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_look_identical()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var a = await Login(c, pw: "nope-nope-nope");
        var b = await Login(c, user: "ghost");
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        Assert.Equal((await a.Json()).ToString(), (await b.Json()).ToString());
        Assert.Equal("invalid_credentials", await (await Login(c, user: "ghost")).ErrorCode());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, pw: "wrong-wrong")).StatusCode);
        var locked = await Login(c);
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("account_locked", await locked.ErrorCode());
    }

    [Fact]
    public async Task Refresh_rotates_and_replaying_the_old_token_revokes_the_device()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var first = await (await Login(c)).Json();
        var r1 = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = first.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var second = await r1.Json();
        Assert.NotEqual(first.GetProperty("refreshToken").GetString(), second.GetProperty("refreshToken").GetString());

        var replay = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = first.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_refresh", await replay.ErrorCode());

        var newest = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = second.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, newest.StatusCode); // whole device revoked
    }

    [Fact]
    public async Task Logout_blocks_refresh_and_needs_an_access_token()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var tokens = await (await Login(c)).Json();

        var anon = await c.PostAsJsonAsync("/api/auth/logout", new { deviceFingerprint = ApiFixture.Fingerprint });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var res = await c.PostAsJsonAsync("/api/auth/logout", new { deviceFingerprint = ApiFixture.Fingerprint });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        c.DefaultRequestHeaders.Authorization = null;
        var refresh = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Device_is_bound_to_one_user()
    {
        await _api.AddUserAsync("alice");
        await _api.AddUserAsync("bob");
        var c = _api.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "alice")).StatusCode);
        var clash = await Login(c, "bob");
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("device_in_use", await clash.ErrorCode());
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "bob", fp: Fp2)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not.a.jwt")]
    public async Task Protected_routes_reject_missing_or_malformed_tokens(string? token)
    {
        var c = _api.Anonymous();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await c.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var error = (await res.Json()).GetProperty("error");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(token is null ? "not_authenticated" : "invalid_token", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validation_errors_use_the_standard_shape_and_never_echo_values()
    {
        var res = await _api.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "s3cret-value" });
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("validation_error", text);
        Assert.Contains("deviceFingerprint", text);
        Assert.DoesNotContain("s3cret-value", text);
    }

    [Fact]
    public async Task Unknown_route_returns_json_error()
    {
        var res = await (await _api.SignedInAsync()).GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not_found", await res.ErrorCode());
    }
}

[Collection("Api")]
public class CustomerAndPlanTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public CustomerAndPlanTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateCustomer(HttpClient c, DateOnly installed, string phone = "+919876543210", string name = "Asha Traders")
    {
        var res = await c.PostAsJsonAsync("/api/customers", Http.CustomerBody(installed, phone, name));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Json()).GetProperty("customerId").GetInt32();
    }

    [Fact]
    public async Task Customer_routes_need_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task Plan_dates_status_renewal_and_filters()
    {
        var c = await _api.SignedInAsync();
        var id = await CreateCustomer(c, new DateOnly(2026, 1, 31));

        await _api.DropAutoPlan(id);
        var created = await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = "AMC", amount = 12000.00m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var plan = await created.Json();
        Assert.Equal("AMC", plan.GetProperty("planType").GetString());
        Assert.Equal("2026-01-31", plan.GetProperty("startDate").GetString());
        Assert.Equal("2027-01-31", plan.GetProperty("endDate").GetString());
        Assert.Equal("2026-12-31", plan.GetProperty("notificationDate").GetString());
        var planId = plan.GetProperty("planId").GetInt32();

        var renew = await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { });
        Assert.Equal(HttpStatusCode.Created, renew.StatusCode);
        var renewal = await renew.Json();
        Assert.Equal("2027-02-01", renewal.GetProperty("startDate").GetString()); // previous end + 1 day
        Assert.Equal("2028-02-01", renewal.GetProperty("endDate").GetString());
        Assert.Equal(planId, renewal.GetProperty("previousPlanId").GetInt32());

        var old = await _api.WithDbAsync(db => db.Plans.SingleAsync(p => p.PlanId == planId));
        Assert.Equal(PlanStatus.Renewed, old.Status);

        var again = await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_renewed", await again.ErrorCode());

        var renewed = await (await c.GetAsync("/api/plans?status=Renewed")).Json();
        Assert.Single(renewed.EnumerateArray());
        Assert.Equal(2, (await (await c.GetAsync("/api/plans?type=AMC")).Json()).GetArrayLength());
        Assert.Equal(0, (await (await c.GetAsync("/api/plans?type=Monthly")).Json()).GetArrayLength());
        Assert.Equal(0, (await (await c.GetAsync($"/api/plans/{planId}/reminders")).Json()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/plans/99999/reminders")).StatusCode);
    }

    [Fact]
    public async Task Plan_status_filter_follows_the_dates()
    {
        var c = await _api.SignedInAsync();
        var today = _api.Today;
        // Installed 11 months ago today: reminder date is today, so due. 2 years ago: expired. Today: active.
        var due = await CreateCustomer(c, today.AddMonths(-11), "+919000000011", "Due Co");
        var expired = await CreateCustomer(c, today.AddYears(-2), "+919000000012", "Old Co");
        var active = await CreateCustomer(c, today, "+919000000013", "New Co");
        foreach (var id in new[] { due, expired, active })
            { await _api.DropAutoPlan(id);
              await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = "Monthly", amount = 500m }); }

        async Task<string[]> Names(string status) =>
            (await (await c.GetAsync($"/api/plans?status={status}")).Json()).EnumerateArray()
                .Select(p => p.GetProperty("customerName").GetString()!).ToArray();

        Assert.Equal(["Due Co"], await Names("DueForRenewal"));
        Assert.Equal(["Old Co"], await Names("Expired"));
        Assert.Equal(["New Co"], await Names("Active"));

        var summary = await (await c.GetAsync("/api/dashboard/summary")).Json();
        Assert.Equal((1, 1, 1), (summary.GetProperty("activeCount").GetInt32(),
            summary.GetProperty("dueForRenewalCount").GetInt32(), summary.GetProperty("expiredCount").GetInt32()));
        Assert.Equal(1, summary.GetProperty("dueIn30DaysCount").GetInt32());

        var upcoming = await (await c.GetAsync("/api/dashboard/upcoming?days=30")).Json();
        Assert.Equal(["Due Co"], upcoming.EnumerateArray().Select(p => p.GetProperty("customerName").GetString()!).ToArray());
        Assert.Equal(12, (await (await c.GetAsync("/api/dashboard/monthly-renewals")).Json()).GetArrayLength());
    }
}

[Collection("Api")]
public class ReminderAndReportTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public ReminderAndReportTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> Seed(HttpClient c, string name, string phone, bool optOut = false, bool deleted = false)
    {
        var body = (dynamic)Http.CustomerBody(_api.Today.AddMonths(-11), phone, name);
        var res = await c.PostAsJsonAsync("/api/customers", new
        {
            body.customerName, body.phone, body.location, body.installationDate, body.projectName,
            body.projectValue, whatsAppOptOut = optOut,
        });
        var id = (await res.Json()).GetProperty("customerId").GetInt32();
        await _api.DropAutoPlan(id);
        var plan = await c.PostAsJsonAsync($"/api/customers/{id}/plans", new { planType = "AMC", amount = 9000m });
        var planId = (await plan.Json()).GetProperty("planId").GetInt32();
        if (deleted) await c.DeleteAsync($"/api/customers/{id}");
        return planId;
    }

    [Fact]
    public async Task Run_sends_once_skips_opted_out_and_deleted_and_never_resends()
    {
        var c = await _api.SignedInAsync();
        var planId = await Seed(c, "Due Co", "+919000000021");
        await Seed(c, "Opted Out", "+919000000022", optOut: true);
        await Seed(c, "Deleted Co", "+919000000023", deleted: true);

        var first = await (await c.PostAsync("/api/reminders/run", null)).Json();
        Assert.Equal((1, 1, 0), (first.GetProperty("processed").GetInt32(), first.GetProperty("sent").GetInt32(), first.GetProperty("failed").GetInt32()));
        var message = Assert.Single(_api.Whatsapp.Sent);
        Assert.Equal("+919000000021", message.Phone);
        Assert.Equal("Due Co", message.CustomerName);

        var second = await (await c.PostAsync("/api/reminders/run", null)).Json();
        Assert.Equal(0, second.GetProperty("processed").GetInt32());
        Assert.Single(_api.Whatsapp.Sent); // nothing sent twice

        var history = await (await c.GetAsync($"/api/plans/{planId}/reminders")).Json();
        var row = Assert.Single(history.EnumerateArray());
        Assert.Equal("Sent", row.GetProperty("status").GetString());
        Assert.Equal("WhatsApp", row.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Failure_is_logged_then_retried_on_the_next_run_using_the_same_row()
    {
        var c = await _api.SignedInAsync();
        var planId = await Seed(c, "Due Co", "+919000000031");

        _api.Whatsapp.Fail = true;
        var failed = await (await c.PostAsync("/api/reminders/run", null)).Json();
        Assert.Equal(1, failed.GetProperty("failed").GetInt32());
        var row = (await (await c.GetAsync($"/api/plans/{planId}/reminders")).Json())[0];
        Assert.Equal("Failed", row.GetProperty("status").GetString());
        Assert.Contains("simulated failure", row.GetProperty("errorMessage").GetString());

        _api.Whatsapp.Fail = false; // "next day's run"
        var retried = await (await c.PostAsync("/api/reminders/run", null)).Json();
        Assert.Equal(1, retried.GetProperty("sent").GetInt32());
        var history = await (await c.GetAsync($"/api/plans/{planId}/reminders")).Json();
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal("Sent", history[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Database_rejects_a_duplicate_reminder_for_the_same_plan_and_date()
    {
        var c = await _api.SignedInAsync();
        var planId = await Seed(c, "Due Co", "+919000000041");
        var date = _api.Today;
        await Assert.ThrowsAsync<DbUpdateException>(() => _api.WithDbAsync(async db =>
        {
            db.Reminders.Add(new Reminder { PlanId = planId, ScheduledDate = date, Status = ReminderStatus.Sent });
            db.Reminders.Add(new Reminder { PlanId = planId, ScheduledDate = date, Status = ReminderStatus.Sent });
            await db.SaveChangesAsync();
            return 0;
        }));
    }

    [Fact]
    public async Task Reports_and_exports()
    {
        var c = await _api.SignedInAsync();
        await Seed(c, "Due Co", "+919000000051");

        var report = await (await c.GetAsync("/api/reports/plans?type=AMC")).Json();
        Assert.Equal(1, report.GetProperty("count").GetInt32());
        var none = await (await c.GetAsync("/api/reports/plans?from=2000-01-01&to=2000-12-31")).Json();
        Assert.Equal(0, none.GetProperty("count").GetInt32());

        var xlsx = await c.GetAsync("/api/reports/export?format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        Assert.Equal((byte)'P', (await xlsx.Content.ReadAsByteArrayAsync())[0]); // zip container

        var pdf = await c.GetAsync("/api/reports/export?format=pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        var bad = await c.GetAsync("/api/reports/export?format=csv");
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
    }
}
