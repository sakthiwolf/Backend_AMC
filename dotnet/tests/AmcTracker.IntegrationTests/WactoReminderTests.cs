using System.Net;
using System.Text;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>
/// The daily run sending through the real Wacto sender, whose HttpClient talks only to <see cref="WactoStub"/>
/// (a fake HttpMessageHandler), against the AMCTracker_Test database. Fake phone numbers only.
/// </summary>
[Collection("Api")]
public class WactoReminderTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private readonly ApiFixture _api;

    public WactoReminderTests(ApiFixture api) => _api = api;

    public async Task InitializeAsync()
    {
        await _api.ResetAsync();
        _api.UseWacto = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private WactoStub Wacto => _api.Wacto;

    private async Task<HttpClient> At(DateOnly day)
    {
        _api.Clock.Fixed = day;
        return await _api.SignedInAsync();
    }

    /// <summary>A customer whose first plan reminds on <paramref name="on"/>. Returns the plan id.</summary>
    private static async Task<int> Seed(HttpClient c, DateOnly on, string phone, string name = "Wacto Co",
        PlanType type = PlanType.Monthly, bool optOut = false, bool deleted = false)
    {
        var (id, planId) = await c.AddCustomer(Plans.Customer(Plans.StartRemindingOn(type, on), type.ToString(), phone, name, optOut));
        if (deleted) await c.DeleteAsync($"/api/customers/{id}");
        return planId;
    }

    private Task<Reminder?> Row(int planId) =>
        _api.WithDbAsync(db => db.Reminders.AsNoTracking().SingleOrDefaultAsync(r => r.PlanId == planId));

    [Fact]
    public async Task A_200_with_an_id_is_Sent_and_the_provider_message_id_is_saved()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000201");

        var run = await c.Run();

        Assert.Equal(1, run.GetProperty("sent").GetInt32());
        var request = Assert.Single(Wacto.Requests);
        Assert.Equal("https://wacto.invalid/v99.0/pnid-123/messages", request.Uri.ToString());
        Assert.Equal("910000000201", request.Json.GetProperty("to").GetString());
        var row = (await Row(planId))!;
        Assert.Equal((ReminderStatus.Sent, "wamid.TEST1", false, 1), (row.Status, row.ProviderMessageId, row.IsTest, row.Attempts));
        Assert.Null(row.ErrorMessage);
    }

    [Fact]
    public async Task Each_plan_type_sends_its_own_template_with_the_three_parameters_in_order()
    {
        var c = await At(Day);
        await Seed(c, Day, "+910000000211", "Yearly Co", PlanType.AMC);
        await Seed(c, Day, "+910000000212", "Monthly Co", PlanType.Monthly);

        await c.Run();

        var byName = Wacto.Requests.ToDictionary(
            r => r.Json.GetProperty("template").GetProperty("name").GetString()!,
            r => r.Json.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")
                .EnumerateArray().Select(p => p.GetProperty("text").GetString()!).ToArray());
        Assert.Equal(["amc_monthly_reminder", "amc_yearly_reminder"], byName.Keys.Order().ToArray());
        Assert.Equal("Yearly Co", byName["amc_yearly_reminder"][0]);
        Assert.Equal("CCTV", byName["amc_yearly_reminder"][1]);
        Assert.Equal("20 Dec 2026", byName["amc_yearly_reminder"][2]); // AMC: ends one month after the reminder
        Assert.Equal(["Monthly Co", "CCTV", "27 Nov 2026"], byName["amc_monthly_reminder"]); // Monthly: 7 days
    }

    [Fact]
    public async Task A_400_fails_is_not_retried_and_keeps_the_provider_error_cut_to_500_characters()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000221");
        var longMessage = "(#131026) Message undeliverable " + new string('z', 700);
        Wacto.Respond = _ => WactoStub.Reply(400, $$$"""{"error":{"code":131026,"message":"{{{longMessage}}}"}}""");

        Assert.Equal(1, (await c.Run()).GetProperty("failed").GetInt32());
        var row = (await Row(planId))!;
        Assert.Equal((ReminderStatus.Failed, Reminder.MaxAttempts), (row.Status, row.Attempts));
        Assert.Equal(500, row.ErrorMessage!.Length);
        Assert.StartsWith("HTTP 400: (#131026) Message undeliverable", row.ErrorMessage);

        // The next days: never sent again, and staff "Try again" is refused with the existing 409.
        Wacto.Respond = WactoStub.Ok;
        _api.Clock.Fixed = Day.AddDays(1);
        var next = await c.Run();
        Assert.Equal((0, 0), (next.GetProperty("sent").GetInt32(), next.GetProperty("failed").GetInt32()));
        Assert.Single(Wacto.Requests); // never called again
        var retry = await c.PostAsync($"/api/reminders/{row.ReminderId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("reminder_out_of_attempts", await retry.ErrorCode());

        // The app shows the stored error with the row.
        var dto = (await c.Reminders(planId))[0];
        Assert.StartsWith("HTTP 400: (#131026)", dto.GetProperty("errorMessage").GetString());
        Assert.False(dto.GetProperty("canRetry").GetBoolean());
    }

    public static TheoryData<string> RetryableFailures => ["429", "500", "timeout"];

    [Theory]
    [MemberData(nameof(RetryableFailures))]
    public async Task A_429_500_or_timeout_fails_and_is_retried_the_next_day_only(string failure)
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000231");
        Wacto.Respond = failure switch
        {
            "timeout" => _ => throw new TaskCanceledException("timed out", new TimeoutException()),
            _ => _ => WactoStub.Reply(int.Parse(failure), """{"error":{"code":4,"message":"try later"}}"""),
        };

        Assert.Equal(1, (await c.Run()).GetProperty("failed").GetInt32());
        Assert.Single(Wacto.Requests); // one attempt in the run, no loop
        var row = (await Row(planId))!;
        Assert.Equal((ReminderStatus.Failed, 1), (row.Status, row.Attempts));
        Assert.Equal(failure == "timeout" ? "Request to WhatsApp timed out." : $"HTTP {failure}: try later", row.ErrorMessage);

        Wacto.Respond = WactoStub.Ok;
        _api.Clock.Fixed = Day.AddDays(1);
        Assert.Equal(1, (await c.Run()).GetProperty("sent").GetInt32());
        Assert.Equal(2, Wacto.Requests.Count);
        row = (await Row(planId))!;
        Assert.Equal((ReminderStatus.Sent, 2, "wamid.TEST1"), (row.Status, row.Attempts, row.ProviderMessageId));
    }

    [Fact]
    public async Task With_reminders_disabled_nothing_is_called_and_nothing_is_written()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000241");
        _api.Reminders.Enabled = false;

        var run = await c.Run();

        Assert.Equal(0, run.GetProperty("processed").GetInt32());
        Assert.Empty(Wacto.Requests);
        Assert.Null(await Row(planId));
    }

    [Fact]
    public async Task Test_phone_only_sends_to_the_test_number_and_marks_the_real_plans_row_as_a_test()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000251");
        _api.Reminders.TestPhoneOnly = true;
        _api.Reminders.TestPhone = "+910000000999";

        await c.Run();

        Assert.Equal("910000000999", Assert.Single(Wacto.Requests).Json.GetProperty("to").GetString());
        var row = (await Row(planId))!;
        Assert.Equal((planId, true, ReminderStatus.Sent), (row.PlanId, row.IsTest, row.Status));
        Assert.True((await c.Reminders(planId))[0].GetProperty("isTest").GetBoolean());
    }

    [Fact]
    public async Task Test_phone_only_without_a_test_phone_sends_nothing()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000255");
        _api.Reminders.TestPhoneOnly = true;
        _api.Reminders.TestPhone = "";

        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32());
        Assert.Empty(Wacto.Requests);
        Assert.Null(await Row(planId));
    }

    [Fact]
    public async Task Opted_out_and_inactive_customers_get_no_call_and_no_row()
    {
        var c = await At(Day);
        var optedOut = await Seed(c, Day, "+910000000261", "Quiet Co", optOut: true);
        var inactive = await Seed(c, Day, "+910000000262", "Gone Co", deleted: true);

        Assert.Equal(0, (await c.Run()).GetProperty("processed").GetInt32());
        Assert.Empty(Wacto.Requests);
        Assert.Equal(0, await _api.WithDbAsync(db => db.Reminders.CountAsync(r => r.PlanId == optedOut || r.PlanId == inactive)));
    }

    [Fact]
    public async Task Catch_up_sends_2_days_late_skips_4_days_late_a_plan_added_after_its_date_and_a_plan_already_sent()
    {
        var c = await At(Day.AddDays(-10));
        var twoDaysAgo = await Seed(c, Day.AddDays(-2), "+910000000271", "Two Days Co");
        var fourDaysAgo = await Seed(c, Day.AddDays(-4), "+910000000272", "Four Days Co");
        var addedLate = await Seed(c, Day.AddDays(-2), "+910000000273", "Added Late Co");
        var alreadySent = await Seed(c, Day.AddDays(-1), "+910000000274", "Sent Co");
        await _api.WithDbAsync(async db =>
        {
            // Entered on the day before yesterday's run... after its reminder date.
            var late = await db.Plans.SingleAsync(p => p.PlanId == addedLate);
            late.CreatedAt = Day.AddDays(-1).ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc); // 09:30 IST
            // A reminder that was already sent for this plan (on another date, e.g. by an earlier set-up).
            db.Reminders.Add(new Reminder
            {
                PlanId = alreadySent, ScheduledDate = Day.AddDays(-5), Status = ReminderStatus.Sent, Attempts = 1,
                SentAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        _api.Clock.Fixed = Day;
        var run = await c.Run();

        Assert.Equal((1, 1), (run.GetProperty("processed").GetInt32(), run.GetProperty("sent").GetInt32()));
        Assert.Equal("910000000271", Assert.Single(Wacto.Requests).Json.GetProperty("to").GetString());
        var sentRow = (await Row(twoDaysAgo))!;
        Assert.Equal((ReminderStatus.Sent, Day.AddDays(-2)), (sentRow.Status, sentRow.ScheduledDate));
        Assert.Null(await Row(fourDaysAgo));
        Assert.Null(await Row(addedLate));
        Assert.Equal(1, await _api.WithDbAsync(db => db.Reminders.CountAsync(r => r.PlanId == alreadySent)));
    }

    [Fact]
    public async Task A_plan_entered_on_its_reminder_date_is_still_sent_that_day()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000281");
        await _api.WithDbAsync(async db =>
        {
            var plan = await db.Plans.SingleAsync(p => p.PlanId == planId);
            plan.CreatedAt = Day.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc).AddDays(-1); // 01:30 IST on Day
            return await db.SaveChangesAsync();
        });

        Assert.Equal(1, (await c.Run()).GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Running_the_job_twice_on_one_day_calls_Wacto_once()
    {
        var c = await At(Day);
        var planId = await Seed(c, Day, "+910000000291", type: PlanType.AMC);

        await c.Run();
        await c.Run();

        Assert.Single(Wacto.Requests);
        Assert.Equal(1, await _api.WithDbAsync(db => db.Reminders.CountAsync(r => r.PlanId == planId)));
    }

    // ---- POST /api/reminders/test-send ----

    [Fact]
    public async Task Test_send_is_409_test_phone_not_set_without_a_test_phone()
    {
        var c = await At(Day);
        var res = await c.PostAsync("/api/reminders/test-send", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("test_phone_not_set", await res.ErrorCode());
        Assert.Empty(Wacto.Requests);
    }

    [Fact]
    public async Task Test_send_sends_one_sample_to_the_test_phone_and_stores_nothing()
    {
        var c = await At(Day);
        _api.Reminders.TestPhone = "+910000000999";

        var res = await c.PostAsync("/api/reminders/test-send", null);
        var monthly = await c.PostAsync("/api/reminders/test-send?planType=Monthly", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Json();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("wamid.TEST1", body.GetProperty("providerMessageId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("errorMessage").ValueKind);
        Assert.Equal(HttpStatusCode.OK, monthly.StatusCode);

        Assert.Equal(2, Wacto.Requests.Count);
        Assert.All(Wacto.Requests, r => Assert.Equal("910000000999", r.Json.GetProperty("to").GetString()));
        Assert.Equal(["amc_yearly_reminder", "amc_monthly_reminder"],
            Wacto.Requests.Select(r => r.Json.GetProperty("template").GetProperty("name").GetString()!).ToArray());
        Assert.Equal(0, await _api.WithDbAsync(db => db.Reminders.CountAsync()));
    }

    [Fact]
    public async Task Test_send_returns_the_providers_error_and_is_for_admins_only()
    {
        var c = await At(Day);
        _api.Reminders.TestPhone = "+910000000999";
        Wacto.Respond = _ => WactoStub.Reply(401, """{"error":{"code":190,"message":"Invalid OAuth access token"}}""");

        var body = await (await c.PostAsync("/api/reminders/test-send", null)).Json();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("HTTP 401: Invalid OAuth access token", body.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Test_send_is_for_admins_only()
    {
        _api.Clock.Fixed = Day;
        _api.Reminders.TestPhone = "+910000000999";
        var clerk = await _api.SignedInAsync("clerk"); // not in Reminders:AdminUsernames
        var res = await clerk.PostAsync("/api/reminders/test-send", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(Wacto.Requests);
    }

    // ---- What reaches the log ----

    [Fact]
    public async Task The_log_holds_no_token_no_phone_number_and_no_auth_header()
    {
        var c = await At(Day);
        await Seed(c, Day, "+910000000301", "Logged Co");
        var failing = await Seed(c, Day, "+910000000302", "Failing Co", PlanType.AMC);
        Wacto.Respond = r => r.Content!.ReadAsStringAsync().Result.Contains("910000000302")
            ? WactoStub.Reply(400, $$$"""{"error":{"code":100,"message":"bad number 910000000302, token {{{ApiFixture.WactoToken}}}"}}""")
            : WactoStub.Ok(r);
        await c.Run();
        _api.Reminders.TestPhone = "+910000000999";
        await c.PostAsync("/api/reminders/test-send", null);

        var log = ReadLog();
        Assert.Contains("Reminder run for", log); // the run was logged at all
        Assert.Contains($"plan {failing}", log);
        Assert.DoesNotContain(ApiFixture.WactoToken, log);
        Assert.DoesNotContain("+91", log);
        Assert.DoesNotContain("9100000003", log);
        Assert.DoesNotContain("910000000999", log);
        Assert.DoesNotContain("Bearer ", log);
        Assert.DoesNotContain("pnid-123", log);
        Assert.DoesNotContain("Logged Co", log);
        // The stored error is scrubbed of the token too.
        var stored = await _api.WithDbAsync(db => db.Reminders.Where(r => r.PlanId == failing).Select(r => r.ErrorMessage).SingleAsync());
        Assert.DoesNotContain(ApiFixture.WactoToken, stored);
    }

    private string ReadLog()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_api.LogDirectory))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }
        return text.ToString();
    }
}
