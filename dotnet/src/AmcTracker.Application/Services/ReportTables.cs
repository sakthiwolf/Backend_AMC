using System.Globalization;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Services;

/// <summary>
/// Turns report items into the table that the Excel and PDF builders draw. The columns are the ones the screen
/// shows. Every money cell is the stored value of its row; there is no totals row.
/// </summary>
public static class ReportTables
{
    private const ReportColumnKind Text = ReportColumnKind.Text;

    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>"12 Oct 2027".</summary>
    public static string Date(DateOnly d) => d.ToString("dd MMM yyyy", Invariant);

    /// <summary>"15 Oct 2026, 09:05" (the value is already on the IST clock).</summary>
    public static string DateTimeText(DateTime d) => d.ToString("dd MMM yyyy, HH:mm", Invariant);

    /// <summary>UTC to the Asia/Kolkata wall clock. India has no daylight-saving time: a fixed +05:30.</summary>
    public static DateTime ToIst(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).AddHours(5).AddMinutes(30);

    /// <summary>"₹12,000.00" with Indian grouping (₹1,25,000.00).</summary>
    public static string Rupees(decimal amount)
    {
        var rounded = decimal.Round(amount, 2);
        var digits = decimal.Truncate(Math.Abs(rounded)).ToString("0", Invariant);
        var fraction = (Math.Abs(rounded) - decimal.Truncate(Math.Abs(rounded))).ToString("0.00", Invariant)[1..];
        var grouped = digits.Length <= 3
            ? digits
            : Group(digits[..^3]) + "," + digits[^3..];
        return (rounded < 0 ? "-" : "") + "₹" + grouped + fraction;

        static string Group(string head)
        {
            var parts = new List<string>();
            while (head.Length > 2)
            {
                parts.Insert(0, head[^2..]);
                head = head[..^2];
            }
            if (head.Length > 0) parts.Insert(0, head);
            return string.Join(",", parts);
        }
    }

    public static string Status(PlanStatus s) => s switch
    {
        PlanStatus.DueForRenewal => "Due for renewal",
        _ => s.ToString(),
    };

    public static string Range(DateOnly from, DateOnly to) => $"{Date(from)} to {Date(to)}";

    // ---- The four tables ----

    public static ReportTable Plans(IReadOnlyList<PlansReportItemDto> items, DateOnly from, DateOnly to, string? filterText) =>
        new("Plans report", Range(from, to), filterText,
        [
            new("Customer", Text, 28, 3f),
            new("Phone", ReportColumnKind.Phone, 17, 2.4f),
            new("Location", Text, 20, 2f),
            new("Project", Text, 26, 2.6f),
            new("Plan type", Text, 11, 1.3f),
            new("Amount", ReportColumnKind.Money, 16, 1.9f),
            new("Starts", ReportColumnKind.Date, 13, 1.8f),
            new("Ends", ReportColumnKind.Date, 13, 1.8f),
            new("Reminder on", ReportColumnKind.Date, 13, 1.8f),
            new("Status", Text, 16, 1.9f),
        ],
        items.Select(i => new object?[]
        {
            i.CustomerName, i.Phone, i.Location, i.ProjectName, i.PlanType.ToString(), i.Amount,
            i.StartDate, i.EndDate, i.ReminderDate, Status(i.Status),
        }).ToList());

    public static ReportTable Due(IReadOnlyList<DueReportItemDto> items, DateOnly asOf, string? filterText) =>
        new("Due and expiring plans", $"As of {Date(asOf)}", filterText,
        [
            new("Customer", Text, 28, 3f),
            new("Phone", ReportColumnKind.Phone, 17, 2.4f),
            new("Location", Text, 20, 2f),
            new("Project", Text, 26, 2.6f),
            new("Plan type", Text, 11, 1.3f),
            new("Amount", ReportColumnKind.Money, 16, 1.9f),
            new("Starts", ReportColumnKind.Date, 13, 1.8f),
            new("Ends", ReportColumnKind.Date, 13, 1.8f),
            new("Reminder on", ReportColumnKind.Date, 13, 1.8f),
            new("Status", Text, 16, 1.9f),
            new("Days until end", ReportColumnKind.Number, 14, 1.4f),
        ],
        items.Select(i => new object?[]
        {
            i.CustomerName, i.Phone, i.Location, i.ProjectName, i.PlanType.ToString(), i.Amount,
            i.StartDate, i.EndDate, i.ReminderDate, Status(i.Status), i.DaysUntilEnd,
        }).ToList());

    public static ReportTable Reminders(IReadOnlyList<RemindersReportItemDto> items, DateOnly from, DateOnly to, string? filterText) =>
        new("Reminders report", Range(from, to), filterText,
        [
            new("Customer", Text, 28, 3f),
            new("Phone", ReportColumnKind.Phone, 17, 2.4f),
            new("Plan no.", ReportColumnKind.Number, 10, 1f),
            new("Scheduled for", ReportColumnKind.Date, 15, 1.8f),
            new("Status", Text, 11, 1.3f),
            new("Attempts", ReportColumnKind.Number, 10, 1.1f),
            new("Sent at", ReportColumnKind.DateTime, 20, 2.4f),
            new("Error", Text, 44, 5f),
        ],
        items.Select(i => new object?[]
        {
            i.CustomerName, i.Phone, i.PlanId, i.ScheduledDate, i.Status.ToString(), i.Attempts,
            i.SentAt is { } sent ? ToIst(sent) : null, i.ErrorMessage,
        }).ToList());

    public static ReportTable Renewals(IReadOnlyList<RenewalsReportItemDto> items, DateOnly from, DateOnly to) =>
        new("Renewals report", Range(from, to), null,
        [
            new("Customer", Text, 28, 3f),
            new("Phone", ReportColumnKind.Phone, 17, 2.4f),
            new("Project", Text, 26, 2.6f),
            new("Plan type", Text, 11, 1.3f),
            new("Previous plan ended", ReportColumnKind.Date, 19, 1.9f),
            new("New plan starts", ReportColumnKind.Date, 16, 1.8f),
            new("New plan ends", ReportColumnKind.Date, 16, 1.8f),
            new("Plan amount", ReportColumnKind.Money, 16, 1.9f),
            new("Paid on", ReportColumnKind.Date, 13, 1.8f),
            new("Mode", Text, 9, 1.1f),
            new("Payment amount", ReportColumnKind.Money, 17, 1.9f),
        ],
        items.Select(i => new object?[]
        {
            i.CustomerName, i.Phone, i.ProjectName, i.PlanType.ToString(), i.PreviousEndDate, i.NewStartDate,
            i.NewEndDate, i.Amount, i.PaidOn, i.PaymentMode?.ToString(), i.PaymentAmount,
        }).ToList());
}
