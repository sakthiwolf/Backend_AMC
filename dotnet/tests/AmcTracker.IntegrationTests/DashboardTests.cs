using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using AmcTracker.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>
/// The dashboard endpoints against SQL Server with a fake clock. Counts and dates only: no test ever looks
/// for, or finds, an amount or revenue.
/// </summary>
[Collection("Api")]
public class DashboardTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    private int _next;
    public DashboardTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private async Task<(int CustomerId, int PlanId)> Add(HttpClient c, string start, string type = "Monthly", string? name = null)
    {
        _next++;
        return await c.AddCustomer(Plans.Customer(D(start), type, $"+9190{_next:00000000}", name ?? $"Customer {_next}"));
    }

    private static async Task<JsonElement> Get(HttpClient c, string url) => await (await c.GetAsync(url)).Json();

    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static async Task Renew(HttpClient c, int planId, string paidOn) =>
        Assert.Equal(HttpStatusCode.Created,
            (await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { paidOn, amount = 100m, mode = "Cash" })).StatusCode);

    private static string[] Ids(JsonElement upcoming, string field = "planId") =>
        upcoming.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).ToString()).ToArray();

    // ---- Summary ----

    [Fact]
    public async Task Summary_counts_a_plan_in_each_status_and_has_no_money_field()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-03-10"); // ends 10 Apr, reminder 3 Apr: Active
        await Add(c, "2027-02-20"); // ends 20 Mar, reminder 13 Mar: Due
        await Add(c, "2027-01-01"); // ended 1 Feb: Expired

        var s = await Get(c, "/api/dashboard/summary");

        Assert.Equal((1, 1, 1, 2, 3, "2027-03-15"), (Int(s, "activeCount"), Int(s, "dueCount"), Int(s, "expiredCount"),
            Int(s, "endingIn30DaysCount"), Int(s, "customerCount"), s.GetProperty("asOf").GetString()));
        Assert.Equal(new[] { "activeCount", "customerCount", "dueCount", "endingIn30DaysCount", "expiredCount", "asOf" }.Order(),
            s.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_renewed_plan_is_not_counted_as_active_due_expired_or_ending_soon()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-03-10"); // Active, ends 10 Apr
        await Add(c, "2027-02-20"); // Due, ends 20 Mar
        await Add(c, "2027-01-01"); // Expired
        var (_, oldPlan) = await Add(c, "2027-02-01"); // ended 1 Mar: expired, then renewed today
        await Renew(c, oldPlan, "2027-03-15");          // the renewal runs 15 Mar to 15 Apr (today + 31)

        var s = await Get(c, "/api/dashboard/summary");

        // The renewed plan counts nowhere; its renewal is Active and ends one day past the 30-day window.
        Assert.Equal((2, 1, 1, 2, 4), (Int(s, "activeCount"), Int(s, "dueCount"), Int(s, "expiredCount"),
            Int(s, "endingIn30DaysCount"), Int(s, "customerCount")));
    }

    [Fact]
    public async Task A_plan_of_an_inactive_customer_is_counted_nowhere()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-02-20"); // Due, ends 20 Mar
        var (gone, _) = await Add(c, "2027-02-18"); // Due, ends 18 Mar
        Assert.Equal(2, Int(await Get(c, "/api/dashboard/summary"), "dueCount"));
        Assert.Equal(2, Ids(await Get(c, "/api/dashboard/upcoming")).Length);
        var march = (await Get(c, "/api/dashboard/monthly-renewals?months=2")).GetProperty("months")[0];
        Assert.Equal(2, Int(march, "endingCount"));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/customers/{gone}")).StatusCode);

        var s = await Get(c, "/api/dashboard/summary");
        Assert.Equal((1, 1, 1), (Int(s, "dueCount"), Int(s, "endingIn30DaysCount"), Int(s, "customerCount")));
        Assert.Single((await Get(c, "/api/dashboard/upcoming")).GetProperty("items").EnumerateArray());
        march = (await Get(c, "/api/dashboard/monthly-renewals?months=2")).GetProperty("months")[0];
        Assert.Equal(1, Int(march, "endingCount"));
    }

    [Fact]
    public async Task Ending_in_30_days_includes_today_plus_30_and_excludes_today_plus_31_and_matches_its_list()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-03-14", name: "Ends in 30");   // ends 14 Apr = today + 30
        await Add(c, "2027-03-15", name: "Ends in 31");   // ends 15 Apr = today + 31
        await Add(c, "2027-02-15", name: "Ends today");   // ends 15 Mar
        await Add(c, "2027-02-14", name: "Ended yesterday"); // ended 14 Mar

        var count = Int(await Get(c, "/api/dashboard/summary"), "endingIn30DaysCount");
        var list = await Get(c, "/api/plans?endingWithinDays=30");

        Assert.Equal(2, count);
        Assert.Equal(count, Int(list, "totalCount")); // the card and the list it opens agree
        Assert.Equal(["Ends today", "Ends in 30"],
            list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("customerName").GetString()!)); // soonest end first
    }

    [Fact]
    public async Task Every_card_count_equals_the_length_of_the_list_it_opens()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        foreach (var start in new[] { "2027-03-10", "2027-03-12", "2027-02-20", "2027-02-21", "2027-01-01", "2026-12-01", "2027-03-14" })
            await Add(c, start);
        var (gone, _) = await Add(c, "2027-02-19");
        await c.DeleteAsync($"/api/customers/{gone}");
        var (_, renewedPlan) = await Add(c, "2027-02-01");
        await Renew(c, renewedPlan, "2027-03-15");

        var s = await Get(c, "/api/dashboard/summary");

        foreach (var (status, field) in new[] { ("Active", "activeCount"), ("DueForRenewal", "dueCount"), ("Expired", "expiredCount") })
        {
            var list = await Get(c, $"/api/plans?status={status}&pageSize=100");
            Assert.Equal(Int(s, field), Int(list, "totalCount"));
            Assert.Equal(Int(s, field), list.GetProperty("items").GetArrayLength());
        }
        var ending = await Get(c, "/api/plans?endingWithinDays=30&pageSize=100");
        Assert.Equal(Int(s, "endingIn30DaysCount"), Int(ending, "totalCount"));
        var ends = ending.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("endDate").GetString()!).ToArray();
        Assert.Equal(ends.Order(), ends); // sorted by end date
    }

    // ---- Upcoming ----

    [Fact]
    public async Task Upcoming_lists_due_plans_and_plans_reminding_soon_in_end_date_order()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        var (_, due) = await Add(c, "2027-02-20", name: "Due");               // Due, ends 20 Mar
        var (_, soon) = await Add(c, "2027-02-25", name: "Reminds in 3");      // Active, reminder 18 Mar, ends 25 Mar
        await Add(c, "2026-04-22", "AMC", "Reminds in 7");                      // Active, reminder 22 Mar (today + 7), ends 22 Apr
        await Add(c, "2026-04-23", "AMC", "Reminds in 8");                      // reminder 23 Mar (today + 8): outside
        await Add(c, "2027-03-02", name: "Reminds in 11");                      // reminder 26 Mar, ends 2 Apr
        await Add(c, "2027-01-01", name: "Expired");                            // never listed
        var (_, renewed) = await Add(c, "2027-02-01", name: "Renewed");         // ended 1 Mar, then renewed
        await Renew(c, renewed, "2027-03-15");

        // Reminder rows: a sent one for the due plan, a failed one for the next.
        await _api.WithDbAsync(async db =>
        {
            var plans = await db.Plans.AsNoTracking().Where(p => p.PlanId == due || p.PlanId == soon).ToListAsync();
            foreach (var p in plans)
            {
                db.Reminders.Add(new Reminder
                {
                    PlanId = p.PlanId, ScheduledDate = p.ReminderDate, Attempts = 1,
                    Status = p.PlanId == due ? ReminderStatus.Sent : ReminderStatus.Failed,
                });
            }
            return await db.SaveChangesAsync();
        });

        var week = await Get(c, "/api/dashboard/upcoming");
        var names = week.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customerName").GetString()!).ToArray();

        Assert.Equal(["Due", "Reminds in 3", "Reminds in 7"], names); // soonest end date first
        var items = week.GetProperty("items");
        Assert.Equal(("DueForRenewal", "Sent"), (items[0].GetProperty("status").GetString(), items[0].GetProperty("reminderStatus").GetString()));
        Assert.Equal(("Active", "Failed"), (items[1].GetProperty("status").GetString(), items[1].GetProperty("reminderStatus").GetString()));
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("reminderStatus").ValueKind); // no reminder row yet
        Assert.Equal(new[] { "planId", "customerId", "customerName", "phone", "projectName", "planType", "endDate", "reminderDate", "status", "reminderStatus" }.Order(),
            items[0].EnumerateObject().Select(p => p.Name).Order());

        // A wider window adds the plans reminding in 8 and 11 days, still in end-date order.
        var wider = await Get(c, "/api/dashboard/upcoming?days=14");
        Assert.Equal(["Due", "Reminds in 3", "Reminds in 11", "Reminds in 7", "Reminds in 8"],
            wider.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customerName").GetString()!));
    }

    [Fact]
    public async Task Upcoming_days_default_to_7_and_must_be_1_to_90()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-02-25"); // reminds in 3 days

        Assert.Equal(Ids(await Get(c, "/api/dashboard/upcoming?days=7")), Ids(await Get(c, "/api/dashboard/upcoming")));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dashboard/upcoming?days=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dashboard/upcoming?days=90")).StatusCode);
        foreach (var bad in new[] { "0", "91", "-3", "abc" })
        {
            var res = await c.GetAsync($"/api/dashboard/upcoming?days={bad}");
            Assert.Equal((HttpStatusCode)422, res.StatusCode);
            Assert.Equal("validation_error", await res.ErrorCode());
        }
    }

    // ---- Monthly renewals ----

    [Fact]
    public async Task Monthly_renewals_put_each_end_date_in_its_month_across_the_year_boundary()
    {
        _api.Clock.Fixed = D("2026-12-10");
        var c = await _api.SignedInAsync();
        await Add(c, "2025-12-01", "AMC");                 // ends 1 Dec 2026: first day of December
        var (_, lastDay) = await Add(c, "2025-12-31", "AMC"); // ends 31 Dec 2026: last day of December (Due)
        await Add(c, "2026-01-01", "AMC");                 // ends 1 Jan 2027: first day of January
        await Renew(c, lastDay, "2026-12-10");              // renewal starts 1 Jan 2027, ends 1 Jan 2028 (outside)

        var months = (await Get(c, "/api/dashboard/monthly-renewals?months=3")).GetProperty("months");

        Assert.Equal(["2026-12", "2027-01", "2027-02"], months.EnumerateArray().Select(m => m.GetProperty("month").GetString()!));
        Assert.Equal((2, 1, 1), (Int(months[0], "endingCount"), Int(months[0], "renewedCount"), Int(months[0], "pendingCount")));
        Assert.Equal((1, 0, 1), (Int(months[1], "endingCount"), Int(months[1], "renewedCount"), Int(months[1], "pendingCount")));
        Assert.Equal((0, 0, 0), (Int(months[2], "endingCount"), Int(months[2], "renewedCount"), Int(months[2], "pendingCount")));
        Assert.Equal(new[] { "month", "endingCount", "renewedCount", "pendingCount" }.Order(), months[0].EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Monthly_renewals_always_add_up_and_default_to_12_months_from_this_month()
    {
        _api.Clock.Fixed = D("2027-03-15");
        var c = await _api.SignedInAsync();
        foreach (var start in new[] { "2027-02-20", "2026-04-01", "2026-04-30", "2026-08-31", "2026-03-15", "2026-12-31" })
            await Add(c, start, "AMC");
        var (_, due) = await Add(c, "2027-02-18");
        await Renew(c, due, "2027-03-15");

        var months = (await Get(c, "/api/dashboard/monthly-renewals")).GetProperty("months");
        Assert.Equal(12, months.GetArrayLength());
        Assert.Equal("2027-03", months[0].GetProperty("month").GetString());
        Assert.Equal("2028-02", months[11].GetProperty("month").GetString());

        foreach (var m in (await Get(c, "/api/dashboard/monthly-renewals?months=24")).GetProperty("months").EnumerateArray())
            Assert.Equal(Int(m, "endingCount"), Int(m, "renewedCount") + Int(m, "pendingCount"));
        Assert.True(Int(months[0], "renewedCount") >= 1);
    }

    [Fact]
    public async Task Monthly_renewals_months_must_be_1_to_24()
    {
        var c = await _api.SignedInAsync();
        Assert.Equal(1, (await Get(c, "/api/dashboard/monthly-renewals?months=1")).GetProperty("months").GetArrayLength());
        Assert.Equal(24, (await Get(c, "/api/dashboard/monthly-renewals?months=24")).GetProperty("months").GetArrayLength());
        foreach (var bad in new[] { "0", "25", "-1", "x" })
        {
            var res = await c.GetAsync($"/api/dashboard/monthly-renewals?months={bad}");
            Assert.Equal((HttpStatusCode)422, res.StatusCode);
            Assert.Equal("validation_error", await res.ErrorCode());
        }
    }

    // ---- Time zone and access ----

    [Fact]
    public void The_business_clock_is_Asia_Kolkata_so_00_30_on_the_1st_is_already_the_new_month()
    {
        // 19:00 UTC on 31 Oct is 00:30 IST on 1 Nov: the business date, and so the current month, is November.
        var utc = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);
        var ist = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZones.Resolve("Asia/Kolkata"));
        Assert.Equal(new DateOnly(2026, 11, 1), DateOnly.FromDateTime(ist));
        Assert.Equal(new TimeOnly(0, 30), TimeOnly.FromDateTime(ist));
    }

    [Theory]
    [InlineData("/api/dashboard/summary")]
    [InlineData("/api/dashboard/upcoming")]
    [InlineData("/api/dashboard/monthly-renewals")]
    public async Task Dashboard_endpoints_return_401_without_a_token(string url) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().GetAsync(url)).StatusCode);

    // ---- One copy of the rules: the SQL agrees with StatusOn, day by day ----

    /// <summary>
    /// Real plans in SQL Server (month-end, leap-year, renewed, and an inactive customer's), and for every day
    /// from ReminderDate - 1 through EndDate + 1 of each Monthly plan (the boundary days of each AMC plan), the
    /// summary, the status lists, the 30-day list and the upcoming list must equal what Plan.StatusOn says.
    /// </summary>
    [Fact]
    public async Task The_sql_counts_and_lists_match_StatusOn_on_every_day_around_the_boundaries()
    {
        _api.Clock.Fixed = D("2027-06-01");
        var c = await _api.SignedInAsync();
        await Add(c, "2027-01-31");                    // Monthly, ends 28 Feb (month-end clamp)
        await Add(c, "2028-01-31");                    // Monthly, ends 29 Feb 2028 (leap year)
        await Add(c, "2026-12-31");                    // Monthly across the year end
        await Add(c, "2028-02-29", "AMC");             // AMC from a leap day, ends 28 Feb 2029
        await Add(c, "2027-03-31", "AMC");             // AMC whose reminder lands on 29 Feb 2028
        var (_, renewedPlan) = await Add(c, "2027-02-01");
        await Renew(c, renewedPlan, "2027-06-01");     // a renewed plan, and its renewal
        var (gone, _) = await Add(c, "2027-02-10");
        await c.DeleteAsync($"/api/customers/{gone}"); // an inactive customer's plan

        var plans = await _api.WithDbAsync(db => db.Plans.AsNoTracking()
            .Include(p => p.Customer).Include(p => p.Renewal).ToListAsync());
        Assert.Contains(plans, p => p.IsRenewed);
        Assert.Contains(plans, p => !p.Customer!.IsActive);
        var counted = plans.Where(p => p.Customer!.IsActive).ToList();

        var days = new SortedSet<DateOnly>();
        foreach (var p in plans)
        {
            if (p.PlanType == PlanType.Monthly)
                for (var d = p.ReminderDate.AddDays(-1); d <= p.EndDate.AddDays(1); d = d.AddDays(1)) days.Add(d);
            else
                days.UnionWith([p.ReminderDate.AddDays(-1), p.ReminderDate, p.EndDate, p.EndDate.AddDays(1)]);
        }
        Assert.True(days.Count > 50, $"only {days.Count} days to check");

        foreach (var day in days)
        {
            _api.Clock.Fixed = day;
            var s = await Get(c, "/api/dashboard/summary");
            int Expect(PlanStatus st) => counted.Count(p => p.StatusOn(day) == st);
            var where = $"on {day}";

            Assert.True(Expect(PlanStatus.Active) == Int(s, "activeCount"), $"active {where}");
            Assert.True(Expect(PlanStatus.DueForRenewal) == Int(s, "dueCount"), $"due {where}");
            Assert.True(Expect(PlanStatus.Expired) == Int(s, "expiredCount"), $"expired {where}");
            var ending = counted.Count(p => !p.IsRenewed && p.EndDate >= day && p.EndDate <= day.AddDays(30));
            Assert.True(ending == Int(s, "endingIn30DaysCount"), $"ending in 30 days {where}");

            // The lists that the cards open have the same length as the counts.
            foreach (var (status, st) in new[] { ("Active", PlanStatus.Active), ("DueForRenewal", PlanStatus.DueForRenewal),
                         ("Expired", PlanStatus.Expired), ("Renewed", PlanStatus.Renewed) })
            {
                var list = await Get(c, $"/api/plans?status={status}&pageSize=1");
                Assert.True(Expect(st) == Int(list, "totalCount"), $"{status} list {where}");
            }
            Assert.True(ending == Int(await Get(c, "/api/plans?endingWithinDays=30&pageSize=1"), "totalCount"), $"30-day list {where}");

            // Upcoming: due, or a reminder within 7 days; never renewed; soonest end first.
            var expectedUpcoming = counted
                .Where(p => !p.IsRenewed && (p.StatusOn(day) == PlanStatus.DueForRenewal
                                             || (p.ReminderDate >= day && p.ReminderDate <= day.AddDays(7))))
                .OrderBy(p => p.EndDate).ThenBy(p => p.PlanId).Select(p => p.PlanId.ToString()).ToArray();
            var actualUpcoming = Ids(await Get(c, "/api/dashboard/upcoming"));
            Assert.True(Enumerable.SequenceEqual(expectedUpcoming, actualUpcoming), $"upcoming {where}");
        }
    }
}
