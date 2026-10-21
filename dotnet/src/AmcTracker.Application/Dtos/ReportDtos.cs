using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Dtos;

// Reports show stored values and counts only. Each money field is the stored value of that row; nothing
// here is, or can hold, a sum, an average, a total or an amount derived from Plans.Amount.

// ---- Filters (raw input from the query string; ReportService validates them) ----

/// <param name="Type">AMC, Monthly or all (default all).</param>
/// <param name="Status">Active, DueForRenewal, Expired or Renewed; empty means any.</param>
/// <param name="IncludeRenewed">Renewed plans are listed only when true (default true).</param>
public record PlansReportFilter(
    DateOnly? From, DateOnly? To, string? Type, string? Status, bool IncludeRenewed = true,
    int? Page = null, int? PageSize = null);

public record DueReportFilter(DateOnly? AsOf, string? Type);

/// <param name="Status">Pending, Sent or Failed; empty means any.</param>
public record RemindersReportFilter(DateOnly? From, DateOnly? To, string? Status, int? Page = null, int? PageSize = null);

/// <summary>The renewal is chosen by the NEW plan's start date.</summary>
public record RenewalsReportFilter(DateOnly? From, DateOnly? To, int? Page = null, int? PageSize = null);

/// <param name="Report">plans, due, reminders or renewals.</param>
/// <param name="Format">xlsx or pdf.</param>
/// <param name="AsOf">Due report only; defaults to today.</param>
public record ReportExportRequest(
    string? Report, string? Format, DateOnly? From, DateOnly? To, string? Type, string? Status,
    bool IncludeRenewed = true, DateOnly? AsOf = null);

// ---- Items ----

public record PlansReportItemDto(
    int PlanId,
    string CustomerName,
    string Phone,
    string? Location,
    string? ProjectName,
    PlanType PlanType,
    decimal Amount,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly ReminderDate,
    PlanStatus Status);

/// <summary><see cref="PlansReportItemDto"/> plus how long is left. <c>DaysUntilEnd</c> is negative once expired.</summary>
public record DueReportItemDto(
    int PlanId,
    string CustomerName,
    string Phone,
    string? Location,
    string? ProjectName,
    PlanType PlanType,
    decimal Amount,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly ReminderDate,
    PlanStatus Status,
    int DaysUntilEnd);

public record DueReportDto(IReadOnlyList<DueReportItemDto> Items, DateOnly AsOf);

public record RemindersReportItemDto(
    int ReminderId,
    int PlanId,
    string CustomerName,
    string Phone,
    DateOnly ScheduledDate,
    ReminderStatus Status,
    int Attempts,
    DateTime? SentAt,
    /// <summary>Cut to 200 characters.</summary>
    string? ErrorMessage);

/// <summary>A renewal: the new plan, the plan it renewed, and the payment recorded for that renewal (null when none).</summary>
public record RenewalsReportItemDto(
    int RenewedPlanId,
    int PreviousPlanId,
    string CustomerName,
    string Phone,
    string? ProjectName,
    PlanType PlanType,
    DateOnly PreviousEndDate,
    DateOnly NewStartDate,
    DateOnly NewEndDate,
    /// <summary>The new plan's stored amount.</summary>
    decimal Amount,
    DateOnly? PaidOn,
    PaymentMode? PaymentMode,
    decimal? PaymentAmount);

// ---- Export table: one shape for Excel and PDF, with the same columns as the screen ----

public enum ReportColumnKind
{
    Text,
    /// <summary>Always text, so a phone number is never reformatted.</summary>
    Phone,
    Date,
    /// <summary>A date and time on the IST clock.</summary>
    DateTime,
    Money,
    Number,
}

/// <param name="Width">Excel column width in characters.</param>
/// <param name="Weight">Relative width in the PDF.</param>
public record ReportColumn(string Header, ReportColumnKind Kind, double Width, float Weight);

/// <summary>
/// Cells hold <see cref="string"/> (Text, Phone), <see cref="DateOnly"/> (Date), <see cref="decimal"/> (Money),
/// <see cref="int"/> (Number) or null. There is no totals row.
/// </summary>
public record ReportTable(
    string Title,
    string RangeText,
    string? FilterText,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows);
