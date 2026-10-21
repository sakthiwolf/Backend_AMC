using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>
/// The four reports and their export, against SQL Server with a fake clock (15 March 2027, Asia/Kolkata).
/// Stored values and counts only: nothing here looks for, or finds, a sum, an average or a total.
/// </summary>
[Collection("Api")]
public class ReportTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2027, 3, 15);
    private const string March = "from=2027-03-01&to=2027-03-31";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ApiFixture _api;
    private int _next;
    public ReportTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private async Task<HttpClient> SignedIn()
    {
        _api.Clock.Fixed = Today;
        return await _api.SignedInAsync();
    }

    private async Task<(int CustomerId, int PlanId)> Add(HttpClient c, string name, string start, string type = "Monthly")
    {
        _next++;
        return await c.AddCustomer(Plans.Customer(D(start), type, $"+9100000{_next:00000}", name));
    }

    private static async Task Renew(HttpClient c, int planId, string paidOn = "2027-03-15", decimal amount = 100m, string mode = "UPI") =>
        Assert.Equal(HttpStatusCode.Created,
            (await c.PostAsJsonAsync($"/api/plans/{planId}/renew", new { paidOn, amount, mode })).StatusCode);

    private static async Task<JsonElement> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url} gave {(int)res.StatusCode}");
        return await res.Json();
    }

    private static string[] Names(JsonElement report) =>
        report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customerName").GetString()!).ToArray();

    private static string[] Ids(JsonElement report, string field = "planId") =>
        report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).ToString()).ToArray();

    private static async Task<HttpResponseMessage> Error(HttpClient c, string url, string code)
    {
        var res = await c.GetAsync(url);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal(code, await res.ErrorCode());
        return res;
    }

    private Task InsertReminder(int planId, string date, ReminderStatus status, int attempts = 1, string? error = null, DateTime? sentAt = null) =>
        _api.WithDbAsync(async db =>
        {
            db.Reminders.Add(new Reminder
            {
                PlanId = planId, ScheduledDate = D(date), Status = status, Attempts = attempts,
                ErrorMessage = error, SentAt = sentAt,
            });
            return await db.SaveChangesAsync();
        });

    /// <summary>The plans report data set: five plans in March 2027 for active customers, plus ones that must not show.</summary>
    private async Task<HttpClient> SeedPlans()
    {
        var c = await SignedIn();
        await Add(c, "Alpha AMC", "2026-06-01", "AMC");  // ends 1 Jun 2027, Active
        await Add(c, "Bravo Active", "2027-03-10");        // ends 10 Apr, Active
        await Add(c, "Charlie Due", "2027-02-20");         // ends 20 Mar, reminder 13 Mar: Due
        await Add(c, "Delta Expired", "2027-01-01");       // ended 1 Feb: Expired, outside March
        var (_, echo) = await Add(c, "Echo Renewed", "2027-02-01"); // ends 1 Mar, then renewed today
        await Renew(c, echo);                                        // renewal runs 15 Mar to 15 Apr
        var (gone, _) = await Add(c, "Foxtrot Inactive", "2027-03-05");
        await c.DeleteAsync($"/api/customers/{gone}");
        return c;
    }

    // ---- Access ----

    [Theory]
    [InlineData("/api/reports/plans")]
    [InlineData("/api/reports/due")]
    [InlineData("/api/reports/reminders")]
    [InlineData("/api/reports/renewals")]
    [InlineData("/api/reports/export?report=plans&format=xlsx")]
    [InlineData("/api/reports/export?report=due&format=pdf")]
    public async Task Report_endpoints_return_401_without_a_token(string url) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().GetAsync(url)).StatusCode);

    // ---- Plans ----

    [Fact]
    public async Task Plans_report_lists_the_plans_in_the_range_by_end_date_with_the_stored_values()
    {
        var c = await SeedPlans();

        var report = await Get(c, $"/api/reports/plans?{March}");

        // Overlapping 1-31 March, active customers only, soonest end first. Echo's old plan ends on 1 March.
        Assert.Equal(new[] {"Echo Renewed", "Charlie Due", "Bravo Active", "Echo Renewed", "Alpha AMC"}, Names(report));
        var statuses = report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()!).ToArray();
        Assert.Equal(new[] {"Renewed", "DueForRenewal", "Active", "Active", "Active"}, statuses);
        Assert.Equal((1, 50, 5), (report.GetProperty("page").GetInt32(), report.GetProperty("pageSize").GetInt32(),
            report.GetProperty("totalCount").GetInt32()));

        var first = report.GetProperty("items")[0];
        Assert.Equal(new[] {"planId", "customerName", "phone", "location", "projectName", "planType", "amount", "startDate",
            "endDate", "reminderDate", "status"}.Order(), first.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("50000.00", first.GetProperty("amount").GetRawText()); // the row's stored value, two decimals
        Assert.Equal(("2027-02-01", "2027-03-01"), (first.GetProperty("startDate").GetString(), first.GetProperty("endDate").GetString()));

        // With no range it is the current month (15 March 2027): the same list.
        Assert.Equal(Names(report), Names(await Get(c, "/api/reports/plans")));
    }

    [Fact]
    public async Task Plans_report_includes_a_plan_whose_period_overlaps_the_range()
    {
        var c = await SeedPlans();

        var early = await Get(c, "/api/reports/plans?from=2027-01-15&to=2027-02-10");

        // Delta (1 Jan to 1 Feb) and Echo's old plan (1 Feb to 1 Mar) overlap; Charlie starts on 20 Feb and does not.
        Assert.Equal(new[] {"Delta Expired", "Echo Renewed", "Alpha AMC"}, Names(early));
    }

    [Fact]
    public async Task Plans_report_filters_by_type_and_by_status()
    {
        var c = await SeedPlans();

        Assert.Equal(new[] {"Alpha AMC"}, Names(await Get(c, $"/api/reports/plans?{March}&type=AMC")));
        Assert.Equal(new[] {"Alpha AMC"}, Names(await Get(c, $"/api/reports/plans?{March}&type=amc"))); // any case
        Assert.Equal(4, Names(await Get(c, $"/api/reports/plans?{March}&type=Monthly")).Length);
        Assert.Equal(5, Names(await Get(c, $"/api/reports/plans?{March}&type=all")).Length);

        Assert.Equal(new[] {"Charlie Due"}, Names(await Get(c, $"/api/reports/plans?{March}&status=DueForRenewal")));
        Assert.Equal(new[] {"Echo Renewed"}, Names(await Get(c, $"/api/reports/plans?{March}&status=Renewed")));
        Assert.Equal(3, Names(await Get(c, $"/api/reports/plans?{March}&status=Active")).Length);
        Assert.Empty(Names(await Get(c, $"/api/reports/plans?{March}&status=Expired"))); // Delta ended before March
    }

    [Fact]
    public async Task Renewed_plans_show_only_when_include_renewed_is_true_and_that_is_the_default()
    {
        var c = await SeedPlans();

        var withRenewed = await Get(c, $"/api/reports/plans?{March}&includeRenewed=true");
        var byDefault = await Get(c, $"/api/reports/plans?{March}");
        var without = await Get(c, $"/api/reports/plans?{March}&includeRenewed=false");

        Assert.Equal(5, withRenewed.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, byDefault.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, without.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain("Renewed", without.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()!));
        // A renewed status with renewed plans left out is simply empty, not an error.
        Assert.Equal(0, (await Get(c, $"/api/reports/plans?{March}&status=Renewed&includeRenewed=false")).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Plans_report_pages()
    {
        var c = await SeedPlans();

        var first = await Get(c, $"/api/reports/plans?{March}&page=1&pageSize=2");
        var second = await Get(c, $"/api/reports/plans?{March}&page=2&pageSize=2");
        var third = await Get(c, $"/api/reports/plans?{March}&page=3&pageSize=2");
        var beyond = await Get(c, $"/api/reports/plans?{March}&page=9&pageSize=2");

        Assert.Equal((2, 2, 5), (first.GetProperty("items").GetArrayLength(), first.GetProperty("pageSize").GetInt32(), first.GetProperty("totalCount").GetInt32()));
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal(1, third.GetProperty("items").GetArrayLength());
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
        Assert.Equal(5, beyond.GetProperty("totalCount").GetInt32());
        var whole = Ids(await Get(c, $"/api/reports/plans?{March}"));
        IEnumerable<string> pages = Ids(first).Concat(Ids(second)).Concat(Ids(third));
        Assert.Equal(whole.AsEnumerable(), pages); // the pages are the whole list, in order
    }

    // ---- Due and expiring ----

    [Fact]
    public async Task Due_report_lists_due_and_expired_plans_by_end_date_with_days_until_end()
    {
        var c = await SignedIn();
        await Add(c, "Active Co", "2027-03-10");                    // Active: not listed
        await Add(c, "Due Co", "2027-02-20");                       // ends 20 Mar: Due
        await Add(c, "Expired Co", "2027-01-01");                   // ended 1 Feb: Expired
        var (_, old) = await Add(c, "Renewed Co", "2027-02-01");    // renewed: not listed
        await Renew(c, old);
        await Add(c, "Due Yearly Co", "2026-03-20", "AMC");         // ends 20 Mar 2027, reminder 20 Feb: Due

        var due = await Get(c, "/api/reports/due");

        Assert.Equal(new[] {"Expired Co", "Due Co", "Due Yearly Co"}, Names(due)); // soonest end date first
        var items = due.GetProperty("items");
        Assert.Equal(("Expired", -42), (items[0].GetProperty("status").GetString(), items[0].GetProperty("daysUntilEnd").GetInt32()));
        Assert.Equal(("DueForRenewal", 5), (items[1].GetProperty("status").GetString(), items[1].GetProperty("daysUntilEnd").GetInt32()));
        Assert.Equal("2027-03-15", due.GetProperty("asOf").GetString());
        Assert.Equal(new[] {"planId", "customerName", "phone", "location", "projectName", "planType", "amount", "startDate",
            "endDate", "reminderDate", "status", "daysUntilEnd"}.Order(), items[0].EnumerateObject().Select(p => p.Name).Order());

        // Plan type filter, and a later "as of" date turns the due plans into expired ones.
        Assert.Equal(new[] {"Due Yearly Co"}, Names(await Get(c, "/api/reports/due?type=AMC")));
        Assert.Equal(new[] {"Expired Co", "Due Co"}, Names(await Get(c, "/api/reports/due?type=Monthly")));
        var later = await Get(c, "/api/reports/due?asOf=2027-03-25");
        Assert.Equal(-5, later.GetProperty("items")[1].GetProperty("daysUntilEnd").GetInt32());
        Assert.Equal("Expired", later.GetProperty("items")[1].GetProperty("status").GetString());
    }

    // ---- Reminders ----

    [Fact]
    public async Task Reminders_report_filters_by_status_and_range_and_cuts_long_errors_to_200_characters()
    {
        var c = await SignedIn();
        var (_, p1) = await Add(c, "Sent Co", "2027-02-10");
        var (_, p2) = await Add(c, "Failed Co", "2027-02-11");
        var longError = "HTTP 400: " + new string('x', 290);
        await InsertReminder(p1, "2027-03-03", ReminderStatus.Sent, 1, null, new DateTime(2027, 3, 3, 3, 30, 0, DateTimeKind.Utc));
        await InsertReminder(p1, "2027-02-10", ReminderStatus.Sent);                 // before March
        await InsertReminder(p2, "2027-03-05", ReminderStatus.Failed, 2, longError);
        await InsertReminder(p2, "2027-03-06", ReminderStatus.Pending, 0);

        var all = await Get(c, $"/api/reports/reminders?{March}");

        Assert.Equal(new[] {"Failed Co", "Failed Co", "Sent Co"}, Names(all)); // newest scheduled date first
        var items = all.GetProperty("items");
        Assert.Equal(new[] {"Pending", "Failed", "Sent"}, items.EnumerateArray().Select(i => i.GetProperty("status").GetString()!));
        Assert.Equal(new[] {"reminderId", "planId", "customerName", "phone", "scheduledDate", "status", "attempts", "sentAt", "errorMessage"}.Order(),
            items[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(200, items[1].GetProperty("errorMessage").GetString()!.Length);
        Assert.Equal(longError[..200], items[1].GetProperty("errorMessage").GetString());
        Assert.Equal(2, items[1].GetProperty("attempts").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, items[2].GetProperty("sentAt").ValueKind);

        Assert.Equal(new[] {"Failed Co"}, Names(await Get(c, $"/api/reports/reminders?{March}&status=Failed")));
        Assert.Equal(new[] {"Sent Co"}, Names(await Get(c, $"/api/reports/reminders?{March}&status=sent")));
        Assert.Equal(1, (await Get(c, $"/api/reports/reminders?{March}&status=Pending")).GetProperty("totalCount").GetInt32());
        Assert.Equal(4, (await Get(c, "/api/reports/reminders?from=2027-02-01&to=2027-03-31")).GetProperty("totalCount").GetInt32());
        await Error(c, $"/api/reports/reminders?{March}&status=Renewed", "validation_error"); // not a reminder status
    }

    // ---- Renewals ----

    [Fact]
    public async Task Renewals_report_links_the_new_plan_to_the_previous_plan_and_its_payment()
    {
        var c = await SignedIn();
        var (_, oldPlan) = await Add(c, "Renewed With Payment", "2027-02-10"); // ends 10 Mar
        Assert.Equal(HttpStatusCode.Created,
            (await c.PostAsJsonAsync($"/api/plans/{oldPlan}/renew", new { paidOn = "2027-03-15", amount = 120.5m, mode = "Cash" })).StatusCode);
        var (customer2, old2) = await Add(c, "Renewed Without Payment", "2027-01-01"); // ended 1 Feb
        var (customer3, old3) = await Add(c, "Renewed In April", "2027-01-05");
        var (noPayment, inApril) = await _api.WithDbAsync(async db =>
        {
            var a = Plan.Create(customer2, PlanType.Monthly, 80m, D("2027-03-20"), old2);
            var b = Plan.Create(customer3, PlanType.Monthly, 90m, D("2027-04-20"), old3);
            a.CreatedAt = b.CreatedAt = DateTime.UtcNow;
            db.Plans.AddRange(a, b); // renewals saved with no payment (for example a manual correction)
            await db.SaveChangesAsync();
            return (a.PlanId, b.PlanId);
        });

        var report = await Get(c, $"/api/reports/renewals?{March}");

        Assert.Equal(new[] {"Renewed Without Payment", "Renewed With Payment"}, Names(report)); // newest start first
        var items = report.GetProperty("items");
        var withPayment = items[1];
        var newPlan = withPayment.GetProperty("renewedPlanId").GetInt32();
        Assert.Equal(oldPlan, withPayment.GetProperty("previousPlanId").GetInt32());
        Assert.Equal(("2027-03-10", "2027-03-15", "2027-04-15"), (withPayment.GetProperty("previousEndDate").GetString(),
            withPayment.GetProperty("newStartDate").GetString(), withPayment.GetProperty("newEndDate").GetString()));
        Assert.Equal(("2027-03-15", "Cash", "120.50", "50000.00"), (withPayment.GetProperty("paidOn").GetString(),
            withPayment.GetProperty("paymentMode").GetString(), withPayment.GetProperty("paymentAmount").GetRawText(),
            withPayment.GetProperty("amount").GetRawText()));
        Assert.True(newPlan > oldPlan);

        var without = items[0];
        Assert.Equal((noPayment, old2), (without.GetProperty("renewedPlanId").GetInt32(), without.GetProperty("previousPlanId").GetInt32()));
        foreach (var field in new[] { "paidOn", "paymentMode", "paymentAmount" })
            Assert.Equal(JsonValueKind.Null, without.GetProperty(field).ValueKind);
        Assert.Equal("80.00", without.GetProperty("amount").GetRawText());

        // Chosen by the new plan's start date: the April renewal is only in April.
        Assert.Equal(new[] {"Renewed In April"}, Names(await Get(c, "/api/reports/renewals?from=2027-04-01&to=2027-04-30")));
        Assert.Equal(inApril, int.Parse(Ids(await Get(c, "/api/reports/renewals?from=2027-04-01&to=2027-04-30"), "renewedPlanId")[0]));
        Assert.Equal(new[] {"renewedPlanId", "previousPlanId", "customerName", "phone", "projectName", "planType", "previousEndDate",
            "newStartDate", "newEndDate", "amount", "paidOn", "paymentMode", "paymentAmount"}.Order(),
            items[0].EnumerateObject().Select(p => p.Name).Order());
    }

    // ---- Inactive customers ----

    [Fact]
    public async Task Inactive_customers_never_appear_in_any_report_or_export()
    {
        var c = await SignedIn();

        // The same shapes for a customer who stays and for one who is then deleted (made inactive):
        // a renewed plan with a payment and a reminder, and a plan that is due.
        async Task Shapes(string name)
        {
            var (_, renewedPlan) = await Add(c, name, "2027-02-10"); // ended 10 Mar, then renewed
            await InsertReminder(renewedPlan, "2027-03-03", ReminderStatus.Sent);
            await Renew(c, renewedPlan);
            await Add(c, name + " Due", "2027-02-20");               // due
        }
        await Shapes("Stays Co");
        await Shapes("Leaves Co");
        var leaving = await _api.WithDbAsync(db => db.Customers
            .Where(x => x.CustomerName.StartsWith("Leaves Co")).Select(x => x.CustomerId).ToListAsync());
        Assert.Equal(2, leaving.Count);
        foreach (var id in leaving)
            Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/customers/{id}")).StatusCode);

        var urls = new[]
        {
            $"/api/reports/plans?{March}", "/api/reports/due", $"/api/reports/reminders?{March}", $"/api/reports/renewals?{March}",
        };
        foreach (var url in urls)
        {
            var names = Names(await Get(c, url));
            Assert.Contains(names, n => n.StartsWith("Stays Co"));            // not an empty report
            Assert.DoesNotContain(names, n => n.StartsWith("Leaves Co"));
        }

        // The Excel files carry the same rows.
        foreach (var report in new[] { "plans", "due", "reminders", "renewals" })
        {
            var customers = await ExcelFirstColumn(c, report);
            Assert.Contains(customers, n => n.StartsWith("Stays Co"));
            Assert.DoesNotContain(customers, n => n.StartsWith("Leaves Co"));
        }
    }

    /// <summary>The customer column of an Excel export: the cells under the header row.</summary>
    private async Task<string[]> ExcelFirstColumn(HttpClient c, string report)
    {
        var bytes = await (await Export(c, report, "xlsx")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        var last = ws.LastRowUsed()!.RowNumber();
        return [.. Enumerable.Range(ExcelHeaderRow + 1, Math.Max(0, last - ExcelHeaderRow)).Select(r => ws.Cell(r, 1).GetString())];
    }

    // ---- Validation ----

    [Fact]
    public async Task Range_rules_apply_to_every_ranged_report_and_export()
    {
        var c = await SignedIn();
        var from = D("2027-01-01");
        var urls = new[] { "plans", "reminders", "renewals" }.Select(r => $"/api/reports/{r}")
            .Concat(new[] { "plans", "reminders", "renewals" }.Select(r => $"/api/reports/export?report={r}&format=xlsx&_=1")).ToArray();

        foreach (var url in urls)
        {
            var sep = url.Contains('?') ? "&" : "?";
            await Error(c, $"{url}{sep}from=2027-03-02&to=2027-03-01", "invalid_range");                       // from after to
            await Error(c, $"{url}{sep}from={from:yyyy-MM-dd}&to={from.AddDays(367):yyyy-MM-dd}", "range_too_large"); // 367 days
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{url}{sep}from={from:yyyy-MM-dd}&to={from.AddDays(366):yyyy-MM-dd}")).StatusCode); // 366 days
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{url}{sep}from=2027-03-01&to=2027-03-01")).StatusCode); // one day
        }
    }

    [Fact]
    public async Task Paging_type_and_status_are_validated()
    {
        var c = await SignedIn();
        foreach (var report in new[] { "plans", "reminders", "renewals" })
        {
            await Error(c, $"/api/reports/{report}?pageSize=0", "validation_error");
            await Error(c, $"/api/reports/{report}?pageSize=201", "validation_error");
            await Error(c, $"/api/reports/{report}?page=0", "validation_error");
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/{report}?pageSize=1")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/{report}?pageSize=200")).StatusCode);
        }
        await Error(c, "/api/reports/plans?type=Weekly", "validation_error");
        await Error(c, "/api/reports/due?type=Weekly", "validation_error");
        await Error(c, "/api/reports/plans?status=Sent", "validation_error");
        await Error(c, "/api/reports/plans?from=notadate", "validation_error");
        await Error(c, "/api/reports/export?report=plans&format=csv", "validation_error");
        await Error(c, "/api/reports/export?report=payments&format=xlsx", "validation_error");
        await Error(c, "/api/reports/export?format=xlsx", "validation_error");
    }

    // ---- Export ----

    private const int ExcelHeaderRow = 6; // company, title, range, generated time, blank, then the header

    private async Task<HttpResponseMessage> Export(HttpClient c, string report, string format, string query = March)
    {
        var res = await c.GetAsync($"/api/reports/export?report={report}&format={format}&{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res;
    }

    [Fact]
    public async Task Excel_export_has_the_header_the_rows_and_real_cells_and_no_totals_row()
    {
        var c = await SeedPlans();

        var res = await Export(c, "plans", "xlsx");

        Assert.Equal(Xlsx, res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("plans-20270315.xlsx", res.Content.Headers.ContentDisposition.FileName!.Trim('"')); // the IST date
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());

        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("Genuine IT Solution", ws.Cell(1, 1).GetString());
        Assert.Equal("Plans report", ws.Cell(2, 1).GetString());
        Assert.Equal("01 Mar 2027 to 31 Mar 2027", ws.Cell(3, 1).GetString());
        Assert.StartsWith("Generated ", ws.Cell(4, 1).GetString());
        Assert.EndsWith(" IST", ws.Cell(4, 1).GetString());

        var headers = Enumerable.Range(1, 10).Select(i => ws.Cell(ExcelHeaderRow, i).GetString()).ToArray();
        Assert.Equal(new[] {"Customer", "Phone", "Location", "Project", "Plan type", "Amount", "Starts", "Ends", "Reminder on", "Status"}, headers);
        Assert.True(Enumerable.Range(1, 10).All(i => ws.Cell(ExcelHeaderRow, i).Style.Font.Bold));
        Assert.Equal(ExcelHeaderRow, ws.SheetView.SplitRow); // frozen under the header
        Assert.True(ws.AutoFilter.IsEnabled);                  // filters on

        // 5 data rows and nothing after them: no totals row.
        Assert.Equal(ExcelHeaderRow + 5, ws.LastRowUsed()!.RowNumber());
        var firstRow = ExcelHeaderRow + 1;
        Assert.Equal("Echo Renewed", ws.Cell(firstRow, 1).GetString());
        Assert.Equal("Renewed", ws.Cell(firstRow, 10).GetString());
        for (var row = firstRow; row <= ExcelHeaderRow + 5; row++) Assert.False(ws.Cell(row, 1).IsEmpty());

        // Cell types: phone as text, dates as real dates, money as a number with the rupee format.
        var phone = ws.Cell(firstRow, 2);
        Assert.Equal(XLDataType.Text, phone.DataType);
        Assert.StartsWith("+91", phone.GetString());
        Assert.Equal("@", phone.Style.NumberFormat.Format);
        foreach (var col in new[] { 7, 8, 9 })
        {
            Assert.Equal(XLDataType.DateTime, ws.Cell(firstRow, col).DataType);
            Assert.Equal("dd mmm yyyy", ws.Cell(firstRow, col).Style.DateFormat.Format);
        }
        Assert.Equal(new DateTime(2027, 3, 1), ws.Cell(firstRow, 8).GetDateTime());
        var money = ws.Cell(firstRow, 6);
        Assert.Equal(XLDataType.Number, money.DataType);
        Assert.Equal(50000d, money.GetDouble());
        Assert.Contains("₹", money.Style.NumberFormat.Format);
        Assert.Contains("0.00", money.Style.NumberFormat.Format);
        Assert.True(ws.Column(1).Width > 10);
    }

    [Fact]
    public async Task Every_report_exports_to_valid_excel_and_pdf_files_named_with_the_ist_date()
    {
        var c = await SeedPlans();
        var (_, plan) = await Add(c, "Reminder Co", "2027-02-12");
        await InsertReminder(plan, "2027-03-05", ReminderStatus.Failed, 1, "HTTP 500");

        var expected = new Dictionary<string, (string Title, string[] Headers)>
        {
            ["plans"] = ("Plans report", ["Customer", "Phone", "Location", "Project", "Plan type", "Amount", "Starts", "Ends", "Reminder on", "Status"]),
            ["due"] = ("Due and expiring plans", ["Customer", "Phone", "Location", "Project", "Plan type", "Amount", "Starts", "Ends", "Reminder on", "Status", "Days until end"]),
            ["reminders"] = ("Reminders report", ["Customer", "Phone", "Plan no.", "Scheduled for", "Status", "Attempts", "Sent at", "Error"]),
            ["renewals"] = ("Renewals report", ["Customer", "Phone", "Project", "Plan type", "Previous plan ended", "New plan starts", "New plan ends", "Plan amount", "Paid on", "Mode", "Payment amount"]),
        };
        foreach (var (report, (title, headers)) in expected)
        {
            var xlsx = await Export(c, report, "xlsx");
            Assert.Equal($"{report}-20270315.xlsx", xlsx.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
            using var wb = new XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()));
            var ws = wb.Worksheet(1);
            Assert.Equal(title, ws.Cell(2, 1).GetString());
            Assert.Equal(headers, Enumerable.Range(1, headers.Length).Select(i => ws.Cell(ExcelHeaderRow, i).GetString()));

            var pdf = await Export(c, report, "pdf");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            Assert.Equal($"{report}-20270315.pdf", pdf.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
            var bytes = await pdf.Content.ReadAsByteArrayAsync();
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes[..4]));
            Assert.True(bytes.Length > 1000, report);
        }
    }

    [Fact]
    public async Task Excel_row_counts_match_the_reports_and_the_filters_apply_to_the_export()
    {
        var c = await SeedPlans();

        async Task<int> Rows(string query)
        {
            using var wb = new XLWorkbook(new MemoryStream(await (await Export(c, "plans", "xlsx", query)).Content.ReadAsByteArrayAsync()));
            var ws = wb.Worksheet(1);
            return ws.LastRowUsed()!.RowNumber() - ExcelHeaderRow;
        }

        Assert.Equal(5, await Rows(March));
        Assert.Equal(4, await Rows($"{March}&includeRenewed=false"));
        Assert.Equal(1, await Rows($"{March}&type=AMC"));
        Assert.Equal(1, await Rows($"{March}&status=DueForRenewal"));
    }

    [Fact]
    public async Task Export_includes_every_row_up_to_20000_and_refuses_more_with_export_too_large()
    {
        var c = await SignedIn();
        await Add(c, "Bulk Co", "2027-03-01");
        await _api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Plans (CustomerId, PlanType, Amount, StartDate, EndDate, ReminderDate, CreatedAt) " +
            "SELECT TOP ({0}) (SELECT TOP 1 CustomerId FROM Customers), 'Monthly', 1, '2027-03-01', '2027-04-01', '2027-03-25', SYSUTCDATETIME() " +
            "FROM sys.all_objects a CROSS JOIN sys.all_objects b", 19_999)); // 1 + 19,999 = 20,000 plans

        var ok = await Export(c, "plans", "xlsx");
        using (var wb = new XLWorkbook(new MemoryStream(await ok.Content.ReadAsByteArrayAsync())))
            Assert.Equal(20_000, wb.Worksheet(1).LastRowUsed()!.RowNumber() - ExcelHeaderRow); // every row, not a page

        await _api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Plans (CustomerId, PlanType, Amount, StartDate, EndDate, ReminderDate, CreatedAt) " +
            "SELECT TOP 1 CustomerId, 'Monthly', 1, '2027-03-01', '2027-04-01', '2027-03-25', SYSUTCDATETIME() FROM Customers"));

        foreach (var format in new[] { "xlsx", "pdf" })
            await Error(c, $"/api/reports/export?report=plans&format={format}&{March}", "export_too_large");
        // The screen is paged, so it still works, and says how many there are.
        Assert.Equal(20_001, (await Get(c, $"/api/reports/plans?{March}&pageSize=200")).GetProperty("totalCount").GetInt32());
        // A narrower filter exports again.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/export?report=plans&format=xlsx&{March}&type=AMC")).StatusCode);
    }

    // ---- One query per report ----

    [Fact]
    public async Task A_report_runs_a_fixed_number_of_statements_whatever_the_number_of_rows()
    {
        var c = await SeedPlans();
        var (_, plan) = await Add(c, "Statement Co", "2027-02-12");
        await InsertReminder(plan, "2027-03-05", ReminderStatus.Failed, 1, "HTTP 500");

        async Task<IReadOnlyList<string>> Run(Func<Task> call)
        {
            _api.Sql.Clear();
            await call();
            return _api.Sql.Report; // the login-token user check is left out
        }

        // Paged reports: one count and one page of rows. Due and exports: one query.
        foreach (var (url, expected) in new[]
        {
            ($"/api/reports/plans?{March}", 2),
            ($"/api/reports/plans?{March}&pageSize=1", 2),
            ($"/api/reports/reminders?{March}", 2),
            ($"/api/reports/renewals?{March}", 2),
            ("/api/reports/due", 1),
            ($"/api/reports/export?report=plans&format=xlsx&{March}", 1),
            ($"/api/reports/export?report=due&format=pdf", 1),
            ($"/api/reports/export?report=reminders&format=xlsx&{March}", 1),
            ($"/api/reports/export?report=renewals&format=pdf&{March}", 1),
        })
        {
            var statements = await Run(() => c.GetAsync(url));
            Assert.True(statements.Count == expected, $"{url} ran {statements.Count} statements, expected {expected}:\n{string.Join("\n", statements)}");
        }

        // More rows, the same number of statements (no per-row queries).
        for (var i = 0; i < 10; i++) await Add(c, $"Extra {i}", "2027-03-02");
        var many = await Run(() => c.GetAsync($"/api/reports/plans?{March}&pageSize=200"));
        Assert.Equal(2, many.Count);

        // An empty result needs only the count.
        var empty = await Run(() => c.GetAsync("/api/reports/plans?from=2000-01-01&to=2000-01-31"));
        Assert.Single(empty);
    }

    // ---- One copy of the status rules, in every report ----

    /// <summary>
    /// Real plans in SQL Server (month-end, leap-year, renewed, an inactive customer's), and for every day around the
    /// boundaries of each plan, the status in the Plans report, its status filter and the Due report must equal
    /// what Plan.StatusOn says: the same check as the dashboard's.
    /// </summary>
    [Fact]
    public async Task The_status_in_every_report_matches_StatusOn_on_every_boundary_day()
    {
        _api.Clock.Fixed = D("2027-06-01");
        var c = await _api.SignedInAsync();
        await Add(c, "M1", "2027-01-31");               // Monthly, ends 28 Feb (month-end clamp)
        await Add(c, "M2", "2028-01-31");               // Monthly, ends 29 Feb 2028 (leap year)
        await Add(c, "M3", "2026-12-31");               // Monthly across the year end
        await Add(c, "A1", "2028-02-29", "AMC");        // AMC from a leap day
        await Add(c, "A2", "2027-03-31", "AMC");        // AMC whose reminder lands on 29 Feb 2028
        var (_, renewed) = await Add(c, "R1", "2027-02-01");
        await Renew(c, renewed, "2027-06-01");          // a renewed plan, and its renewal
        var (gone, _) = await Add(c, "X1", "2027-02-10");
        await c.DeleteAsync($"/api/customers/{gone}");  // an inactive customer's plan

        var plans = await _api.WithDbAsync(db => db.Plans.AsNoTracking().Include(p => p.Customer).Include(p => p.Renewal).ToListAsync());
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
            var from = day.AddDays(-300);
            var to = day.AddDays(60);
            var window = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&pageSize=200";
            var inWindow = counted.Where(p => p.StartDate <= to && p.EndDate >= from).ToList();
            var where = $"on {day}";

            var report = await Get(c, $"/api/reports/plans?{window}");
            var reported = report.GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("planId").GetInt32(), i => i.GetProperty("status").GetString()!);
            Assert.True(inWindow.Select(p => p.PlanId).Order().SequenceEqual(reported.Keys.Order()), $"plans in range {where}");
            foreach (var p in inWindow)
                Assert.True(reported[p.PlanId] == p.StatusOn(day).ToString(), $"status of plan {p.PlanId} {where}: {reported[p.PlanId]} vs {p.StatusOn(day)}");

            foreach (var status in Enum.GetValues<PlanStatus>())
            {
                var filtered = Ids(await Get(c, $"/api/reports/plans?{window}&status={status}"));
                var expectedIds = inWindow.Where(p => p.StatusOn(day) == status).Select(p => p.PlanId.ToString()).Order();
                Assert.True(expectedIds.SequenceEqual(filtered.Order()), $"{status} filter {where}");
            }
            var withoutRenewed = Ids(await Get(c, $"/api/reports/plans?{window}&includeRenewed=false"));
            Assert.True(inWindow.Where(p => !p.IsRenewed).Select(p => p.PlanId.ToString()).Order().SequenceEqual(withoutRenewed.Order()), $"includeRenewed=false {where}");

            var due = await Get(c, "/api/reports/due");
            var expectedDue = counted.Where(p => p.StatusOn(day) is PlanStatus.DueForRenewal or PlanStatus.Expired)
                .OrderBy(p => p.EndDate).ThenBy(p => p.PlanId).ToList();
            Assert.True(expectedDue.Select(p => p.PlanId.ToString()).SequenceEqual(Ids(due)), $"due list {where}");
            var dueItems = due.GetProperty("items").EnumerateArray().ToList();
            for (var i = 0; i < expectedDue.Count; i++)
            {
                Assert.True(dueItems[i].GetProperty("status").GetString() == expectedDue[i].StatusOn(day).ToString(), $"due status {where}");
                Assert.True(dueItems[i].GetProperty("daysUntilEnd").GetInt32() == expectedDue[i].EndDate.DayNumber - day.DayNumber, $"days until end {where}");
            }
        }
    }
}
