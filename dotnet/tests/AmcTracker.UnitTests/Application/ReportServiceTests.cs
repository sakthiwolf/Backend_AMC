using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Domain.Enums;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class ReportServiceTests
{
    private static readonly DateOnly Today = new(2027, 3, 15);
    private readonly IReportRepository _repo = Substitute.For<IReportRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ListLogger<ReportService> _log = new();

    public ReportServiceTests()
    {
        _clock.Today.Returns(Today);
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>([], 0));
        _repo.DueAsync(default!, default).ReturnsForAnyArgs(Array.Empty<PlanReportRow>());
        _repo.RemindersAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<ReminderReportRow>([], 0));
        _repo.RenewalsAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<RenewalReportRow>([], 0));
    }

    private ReportService Service() => new(_repo, _clock, _log);

    private PlansReportQuery LastPlansQuery() =>
        (PlansReportQuery)_repo.ReceivedCalls().Last(c => c.GetMethodInfo().Name == nameof(IReportRepository.PlansAsync)).GetArguments()[0]!;

    private static PlanReportRow Plan(int id, string end = "2027-04-10", string reminder = "2027-04-03", bool renewed = false, decimal amount = 12000m) =>
        new(id, $"Customer {id}", "+919876543210", "Chennai", "CCTV", PlanType.Monthly, amount,
            DateOnly.Parse("2027-03-10"), DateOnly.Parse(end), DateOnly.Parse(reminder), renewed);

    // ---- Range ----

    [Fact]
    public async Task The_default_range_is_the_current_calendar_month()
    {
        await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null));
        var q = LastPlansQuery();
        Assert.Equal((new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31), Today), (q.From, q.To, q.Today));
    }

    [Fact]
    public async Task The_default_range_follows_the_ist_date_of_the_clock_at_a_month_start()
    {
        _clock.Today.Returns(new DateOnly(2026, 11, 1)); // 00:30 IST on the 1st
        await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null));
        var q = LastPlansQuery();
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), (q.From, q.To));
    }

    [Fact]
    public async Task From_after_to_is_422_invalid_range()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Service().GetPlansAsync(new PlansReportFilter(new DateOnly(2027, 3, 2), new DateOnly(2027, 3, 1), null, null)));
        Assert.Equal((422, "invalid_range"), (ex.StatusCode, ex.Code));
        await _repo.DidNotReceiveWithAnyArgs().PlansAsync(default!, default, default);
    }

    [Theory]
    [InlineData(0, true)]    // one day
    [InlineData(366, true)]  // the longest allowed
    [InlineData(367, false)]
    [InlineData(1000, false)]
    public async Task A_range_is_at_most_366_days(int days, bool allowed)
    {
        var from = new DateOnly(2027, 1, 1);
        var filter = new PlansReportFilter(from, from.AddDays(days), null, null);
        if (allowed)
        {
            await Service().GetPlansAsync(filter);
            return;
        }
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().GetPlansAsync(filter));
        Assert.Equal((422, "range_too_large"), (ex.StatusCode, ex.Code));
        Assert.Equal("The date range can be at most 366 days.", ex.Message);
    }

    [Fact]
    public async Task The_range_rules_apply_to_reminders_and_renewals_too()
    {
        var from = new DateOnly(2027, 1, 1);
        foreach (var call in new Func<DateOnly, Task>[]
        {
            to => Service().GetRemindersAsync(new RemindersReportFilter(from, to, null)),
            to => Service().GetRenewalsAsync(new RenewalsReportFilter(from, to)),
        })
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => call(from.AddDays(367)));
            Assert.Equal("range_too_large", ex.Code);
            await call(from.AddDays(366));
        }
    }

    // ---- Paging, type and status ----

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-5)]
    public async Task A_page_size_outside_1_to_200_is_422(int pageSize)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Service().GetPlansAsync(new PlansReportFilter(null, null, null, null, true, 1, pageSize)));
        Assert.Equal((422, "validation_error"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task Page_below_1_is_422_and_the_default_is_page_1_of_50()
    {
        await Assert.ThrowsAsync<AppException>(() =>
            Service().GetPlansAsync(new PlansReportFilter(null, null, null, null, true, 0, 10)));

        await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null));
        Assert.Equal((0, 50), (LastPlansQuery().Skip, LastPlansQuery().Take));
        await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null, true, 3, 20));
        Assert.Equal((40, 20), (LastPlansQuery().Skip, LastPlansQuery().Take));
    }

    [Fact]
    public async Task Type_and_status_are_checked_and_parsed()
    {
        await Service().GetPlansAsync(new PlansReportFilter(null, null, "amc", "dueforrenewal"));
        Assert.Equal((PlanType.AMC, PlanStatus.DueForRenewal), (LastPlansQuery().Type, LastPlansQuery().Status));
        await Service().GetPlansAsync(new PlansReportFilter(null, null, "all", ""));
        Assert.Equal((null, null), (LastPlansQuery().Type, LastPlansQuery().Status));

        foreach (var bad in new[]
        {
            new PlansReportFilter(null, null, "Weekly", null),
            new PlansReportFilter(null, null, null, "Sent"),
        })
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => Service().GetPlansAsync(bad));
            Assert.Equal((422, "validation_error"), (ex.StatusCode, ex.Code));
        }
        await Assert.ThrowsAsync<AppException>(() =>
            Service().GetRemindersAsync(new RemindersReportFilter(null, null, "Renewed")));
        await Assert.ThrowsAsync<AppException>(() =>
            Service().GetDueAndExpiringAsync(new DueReportFilter(null, "Weekly")));
    }

    // ---- Items ----

    [Fact]
    public async Task Status_comes_from_the_shared_rules_and_money_is_each_rows_stored_value()
    {
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>(
        [
            Plan(1, end: "2027-04-10", reminder: "2027-04-03"),  // Active on 15 Mar
            Plan(2, end: "2027-03-20", reminder: "2027-03-13"),  // Due
            Plan(3, end: "2027-03-01", reminder: "2027-02-22"),  // Expired
            Plan(4, end: "2027-03-01", reminder: "2027-02-22", renewed: true), // Renewed
            Plan(5, amount: 12345.6m),
        ], 5));

        var report = await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null));

        Assert.Equal(
            new[] { PlanStatus.Active, PlanStatus.DueForRenewal, PlanStatus.Expired, PlanStatus.Renewed, PlanStatus.Active },
            report.Items.Select(i => i.Status));
        Assert.Equal(new[] { 12000.00m, 12000.00m, 12000.00m, 12000.00m, 12345.60m }, report.Items.Select(i => i.Amount));
        Assert.Equal(5, report.TotalCount);
        Assert.Equal(5, report.Items.Count); // as many items as rows: nothing added up or appended
    }

    [Fact]
    public async Task Due_items_carry_days_until_end_negative_when_expired_and_the_date_is_today_by_default()
    {
        _repo.DueAsync(default!, default).ReturnsForAnyArgs(new[]
        {
            Plan(1, end: "2027-03-01", reminder: "2027-02-22"),  // expired 14 days ago
            Plan(2, end: "2027-03-20", reminder: "2027-03-13"),  // 5 days left
            Plan(3, end: "2027-03-15", reminder: "2027-03-08"),  // ends today
        });

        var due = await Service().GetDueAndExpiringAsync(new DueReportFilter(null, null));

        Assert.Equal(Today, due.AsOf);
        Assert.Equal(new[] { -14, 5, 0 }, due.Items.Select(i => i.DaysUntilEnd));
        Assert.Equal(new[] { PlanStatus.Expired, PlanStatus.DueForRenewal, PlanStatus.DueForRenewal }, due.Items.Select(i => i.Status));
    }

    [Fact]
    public async Task Reminder_error_messages_are_cut_to_200_characters()
    {
        _repo.RemindersAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<ReminderReportRow>(
        [
            new(1, 1, "A", "+919876543210", Today, ReminderStatus.Failed, 2, null, new string('e', 500)),
            new(2, 1, "A", "+919876543210", Today, ReminderStatus.Failed, 1, null, new string('f', 200)),
            new(3, 1, "A", "+919876543210", Today, ReminderStatus.Sent, 1, new DateTime(2027, 3, 15, 3, 30, 0, DateTimeKind.Utc), null),
        ], 3));

        var items = (await Service().GetRemindersAsync(new RemindersReportFilter(null, null, null))).Items;

        Assert.Equal(new[] { 200, 200 }, items.Take(2).Select(i => i.ErrorMessage!.Length));
        Assert.Equal(new string('e', 200), items[0].ErrorMessage);
        Assert.Null(items[2].ErrorMessage);
    }

    [Fact]
    public async Task A_renewal_with_no_payment_has_null_payment_fields()
    {
        _repo.RenewalsAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<RenewalReportRow>(
        [
            new(11, 10, "A", "+919876543210", null, PlanType.Monthly, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 15),
                new DateOnly(2027, 4, 15), 500m, null, null, null),
            new(21, 20, "B", "+919876543211", "CCTV", PlanType.AMC, new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 2),
                new DateOnly(2028, 3, 2), 12000m, new DateOnly(2027, 3, 2), PaymentMode.UPI, 11999.5m),
        ], 2));

        var items = (await Service().GetRenewalsAsync(new RenewalsReportFilter(null, null))).Items;

        Assert.Equal((10, null, null, null), (items[0].PreviousPlanId, items[0].PaidOn, items[0].PaymentMode, items[0].PaymentAmount));
        Assert.Equal((20, PaymentMode.UPI, 11999.50m), (items[1].PreviousPlanId, items[1].PaymentMode, items[1].PaymentAmount));
    }

    // ---- Export ----

    [Fact]
    public async Task Export_reads_every_row_in_one_query_with_no_count_and_allows_exactly_20000()
    {
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>(
            [.. Enumerable.Range(1, 20_000).Select(i => Plan(i))], null));

        var items = await Service().GetPlansForExportAsync(new PlansReportFilter(null, null, null, null));

        Assert.Equal(20_000, items.Count);
        var query = LastPlansQuery();
        Assert.Equal((0, 20_001), (query.Skip, query.Take)); // one more than the cap, to know it was exceeded
        await _repo.Received(1).PlansAsync(Arg.Any<PlansReportQuery>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task More_than_20000_rows_is_422_export_too_large_for_every_report()
    {
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>(
            [.. Enumerable.Range(1, 20_001).Select(i => Plan(i))], null));
        _repo.DueAsync(default!, default).ReturnsForAnyArgs(Enumerable.Range(1, 20_001).Select(i => Plan(i)).ToArray());
        _repo.RemindersAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<ReminderReportRow>(
            [.. Enumerable.Range(1, 20_001).Select(i => new ReminderReportRow(i, 1, "A", "+919876543210", Today, ReminderStatus.Sent, 1, null, null))], null));
        _repo.RenewalsAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<RenewalReportRow>(
            [.. Enumerable.Range(1, 20_001).Select(i => new RenewalReportRow(i, 1, "A", "+919876543210", null, PlanType.AMC, Today, Today, Today, 1m, null, null, null))], null));
        var service = Service();

        foreach (var call in new Func<Task>[]
        {
            () => service.GetPlansForExportAsync(new PlansReportFilter(null, null, null, null)),
            async () => await service.GetDueForExportAsync(new DueReportFilter(null, null), default),
            () => service.GetRemindersForExportAsync(new RemindersReportFilter(null, null, null), default),
            () => service.GetRenewalsForExportAsync(new RenewalsReportFilter(null, null), default),
        })
        {
            var ex = await Assert.ThrowsAsync<AppException>(call);
            Assert.Equal((422, "export_too_large"), (ex.StatusCode, ex.Code));
        }
    }

    // ---- Logging ----

    [Fact]
    public async Task Logs_carry_the_report_name_row_count_and_time_only()
    {
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>(
            [Plan(1), Plan(2)], 2));

        await Service().GetPlansAsync(new PlansReportFilter(null, null, null, null));

        var line = Assert.Single(_log.Lines);
        Assert.Contains("plans", line);
        Assert.Contains("2 rows", line);
        Assert.Contains(" ms", line);
        Assert.DoesNotContain("9876543210", line); // no phone numbers
        Assert.DoesNotContain("Customer", line);    // no names
        Assert.DoesNotContain("Chennai", line);
        Assert.DoesNotContain("CCTV", line);
    }
}

public class ReportTablesTests
{
    [Theory]
    [InlineData("12000", "₹12,000.00")]
    [InlineData("125000", "₹1,25,000.00")]
    [InlineData("100000", "₹1,00,000.00")]
    [InlineData("12500000", "₹1,25,00,000.00")]
    [InlineData("999.5", "₹999.50")]
    [InlineData("0", "₹0.00")]
    [InlineData("1234567.891", "₹12,34,567.89")]
    [InlineData("1000", "₹1,000.00")]
    [InlineData("99999.999", "₹1,00,000.00")]
    public void Rupees_use_indian_grouping_with_two_decimals(string amount, string expected) =>
        Assert.Equal(expected, ReportTables.Rupees(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Dates_are_in_words_and_the_clock_is_india_time()
    {
        Assert.Equal("12 Oct 2027", ReportTables.Date(new DateOnly(2027, 10, 12)));
        var ist = ReportTables.ToIst(new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc)); // 00:30 IST on 1 Nov
        Assert.Equal(new DateTime(2026, 11, 1, 0, 30, 0), ist);
        Assert.Equal("01 Nov 2026, 00:30", ReportTables.DateTimeText(ist));
    }

    [Fact]
    public void A_table_has_exactly_one_row_per_item_and_no_totals_row()
    {
        var items = Enumerable.Range(1, 3).Select(i => new PlansReportItemDto(
            i, $"C{i}", "+919876543210", null, null, PlanType.AMC, 100m * i, new DateOnly(2027, 1, 1),
            new DateOnly(2028, 1, 1), new DateOnly(2027, 12, 1), PlanStatus.Active)).ToList();

        var table = ReportTables.Plans(items, new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31), null);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new object?[] { "C3" }, table.Rows[^1].Take(1)); // the last row is the last item, not a total
        Assert.All(table.Rows, r => Assert.Equal(table.Columns.Count, r.Length));
        Assert.Equal(new decimal[] { 100m, 200m, 300m }, table.Rows.Select(r => (decimal)r[5]!)); // each row's own value
        Assert.Equal(ReportColumnKind.Phone, table.Columns[1].Kind); // phones stay text
    }
}

public class ReportExportServiceTests
{
    private sealed class FakeBuilder(string format, string extension, string contentType) : IReportFileBuilder
    {
        public string Format { get; } = format;
        public string ContentType { get; } = contentType;
        public string Extension { get; } = extension;
        public ReportTable? Table { get; private set; }
        public DateTime GeneratedAt { get; private set; }

        public byte[] Build(ReportTable table, DateTime generatedAtIst)
        {
            Table = table;
            GeneratedAt = generatedAtIst;
            return [1, 2, 3];
        }
    }

    private readonly IReportRepository _repo = Substitute.For<IReportRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly FakeBuilder _excel = new("xlsx", "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    private readonly FakeBuilder _pdf = new("pdf", "pdf", "application/pdf");
    private readonly ListLogger<ReportExportService> _log = new();

    public ReportExportServiceTests()
    {
        // 00:30 IST on 15 March 2027 is 19:00 UTC on the 14th: the file uses the IST date.
        _clock.UtcNow.Returns(new DateTime(2027, 3, 14, 19, 0, 0, DateTimeKind.Utc));
        _clock.Today.Returns(new DateOnly(2027, 3, 15));
        _repo.PlansAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<PlanReportRow>(
            [new(1, "Asha", "+919876543210", null, null, PlanType.AMC, 100m, new DateOnly(2027, 3, 1), new DateOnly(2028, 3, 1), new DateOnly(2028, 2, 1), false)], null));
        _repo.DueAsync(default!, default).ReturnsForAnyArgs(Array.Empty<PlanReportRow>());
        _repo.RemindersAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<ReminderReportRow>([], null));
        _repo.RenewalsAsync(default!, default, default).ReturnsForAnyArgs(new ReportRows<RenewalReportRow>([], null));
    }

    private ReportExportService Service() => new(
        new ReportService(_repo, _clock, new ListLogger<ReportService>()), [_excel, _pdf], _clock, _log);

    [Fact]
    public async Task The_file_is_named_report_and_ist_date_and_uses_the_chosen_builder()
    {
        var xlsx = await Service().ExportAsync(new ReportExportRequest("plans", "xlsx", null, null, null, null));
        var pdf = await Service().ExportAsync(new ReportExportRequest("DUE", "PDF", null, null, null, null));

        Assert.Equal(("plans-20270315.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"), (xlsx.FileName, xlsx.ContentType));
        Assert.Equal(("due-20270315.pdf", "application/pdf"), (pdf.FileName, pdf.ContentType));
        Assert.Equal(new byte[] { 1, 2, 3 }, xlsx.Content);
        Assert.Equal(new DateTime(2027, 3, 15, 0, 30, 0), _excel.GeneratedAt); // the generated time is IST
    }

    [Fact]
    public async Task The_table_has_the_range_the_filters_and_no_totals_row()
    {
        await Service().ExportAsync(new ReportExportRequest("plans", "xlsx", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31), "AMC", "Active", false));

        var table = _excel.Table!;
        Assert.Equal("Plans report", table.Title);
        Assert.Equal("01 Mar 2027 to 31 Mar 2027", table.RangeText);
        Assert.Equal("Plan type: AMC · Status: Active · Renewed plans not included", table.FilterText);
        Assert.Single(table.Rows); // the one plan; no totals row
        Assert.Equal("Asha", table.Rows[0][0]);
    }

    [Fact]
    public async Task The_due_export_is_as_of_a_date_not_a_range()
    {
        await Service().ExportAsync(new ReportExportRequest("due", "xlsx", null, null, null, null, true, new DateOnly(2027, 4, 2)));
        Assert.Equal("As of 02 Apr 2027", _excel.Table!.RangeText);
    }

    [Theory]
    [InlineData(null, "xlsx")]
    [InlineData("payments", "xlsx")]
    [InlineData("plans", null)]
    [InlineData("plans", "csv")]
    public async Task An_unknown_report_or_format_is_422(string? report, string? format)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().ExportAsync(new ReportExportRequest(report, format, null, null, null, null)));
        Assert.Equal((422, "validation_error"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task The_range_rules_apply_to_exports_and_the_log_has_no_customer_data()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Service().ExportAsync(
            new ReportExportRequest("plans", "xlsx", new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 3), null, null)));
        Assert.Equal("range_too_large", ex.Code);

        await Service().ExportAsync(new ReportExportRequest("plans", "xlsx", null, null, null, null));
        var line = Assert.Single(_log.Lines);
        Assert.Contains("plans", line);
        Assert.Contains("1 rows", line);
        Assert.DoesNotContain("Asha", line);
        Assert.DoesNotContain("9876543210", line);
    }
}
