using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Abstractions;

// ---- What the repository is asked for (already validated) ----

/// <summary>Plans whose period overlaps From..To (start on or before To, end on or after From), for active customers.</summary>
public record PlansReportQuery(
    DateOnly From, DateOnly To, DateOnly Today, PlanType? Type, PlanStatus? Status, bool IncludeRenewed,
    int Skip, int Take);

public record DueReportQuery(DateOnly AsOf, PlanType? Type);

public record RemindersReportQuery(DateOnly From, DateOnly To, ReminderStatus? Status, int Skip, int Take);

public record RenewalsReportQuery(DateOnly From, DateOnly To, int Skip, int Take);

// ---- What it returns ----

/// <param name="Total">How many rows match, ignoring paging. Null when it was not asked for (exports).</param>
public record ReportRows<T>(IReadOnlyList<T> Rows, int? Total);

/// <summary>A plan row. The status is worked out by the service from the dates and <paramref name="IsRenewed"/>.</summary>
public record PlanReportRow(
    int PlanId, string CustomerName, string Phone, string? Location, string? ProjectName, PlanType PlanType,
    decimal Amount, DateOnly StartDate, DateOnly EndDate, DateOnly ReminderDate, bool IsRenewed);

public record ReminderReportRow(
    int ReminderId, int PlanId, string CustomerName, string Phone, DateOnly ScheduledDate, ReminderStatus Status,
    int Attempts, DateTime? SentAt, string? ErrorMessage);

public record RenewalReportRow(
    int RenewedPlanId, int PreviousPlanId, string CustomerName, string Phone, string? ProjectName, PlanType PlanType,
    DateOnly PreviousEndDate, DateOnly NewStartDate, DateOnly NewEndDate, decimal Amount,
    DateOnly? PaidOn, PaymentMode? PaymentMode, decimal? PaymentAmount);

/// <summary>
/// Read-only report queries. Each is a single filtered query over active customers (a second one counts the rows
/// when a total is asked for), using the status rules in <see cref="Domain.Services.PlanSpec"/>. AsNoTracking.
/// </summary>
public interface IReportRepository
{
    Task<ReportRows<PlanReportRow>> PlansAsync(PlansReportQuery query, bool withTotal, CancellationToken ct = default);
    /// <summary>Plans DueForRenewal or Expired on <c>AsOf</c>, soonest end date first.</summary>
    Task<IReadOnlyList<PlanReportRow>> DueAsync(DueReportQuery query, CancellationToken ct = default);
    Task<ReportRows<ReminderReportRow>> RemindersAsync(RemindersReportQuery query, bool withTotal, CancellationToken ct = default);
    Task<ReportRows<RenewalReportRow>> RenewalsAsync(RenewalsReportQuery query, bool withTotal, CancellationToken ct = default);
}

/// <summary>Turns a <see cref="ReportTable"/> into a file. One implementation per format (Excel, PDF).</summary>
public interface IReportFileBuilder
{
    /// <summary>xlsx or pdf.</summary>
    string Format { get; }
    string ContentType { get; }
    /// <summary>Without the dot.</summary>
    string Extension { get; }
    /// <param name="generatedAtIst">The generated time on the Asia/Kolkata clock, shown in the header.</param>
    byte[] Build(ReportTable table, DateTime generatedAtIst);
}
