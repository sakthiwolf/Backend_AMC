using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Dtos;

/// <summary>Which plan date the from/to range of a report applies to.</summary>
public enum DateBasis
{
    EndDate,
    ReminderDate,
    StartDate,
}

// ---- Auth ----
public record LoginRequest(string? Username, string? Password, string? DeviceFingerprint, string? DeviceLabel);
public record RefreshRequest(string? DeviceFingerprint, string? RefreshToken);
public record LogoutRequest(string? DeviceFingerprint);
public record UserDto(int UserId, string Username, string FullName);
public record TokenResponse(
    string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, UserDto User);

// ---- Customers ----
/// <summary>The customer fields a client may send (shared by create and update).</summary>
public interface ICustomerFields
{
    string? CustomerName { get; }
    string? Phone { get; }
    string? Location { get; }
    DateOnly? InstallationDate { get; }
    string? ProjectName { get; }
    /// <summary>
    /// The rate for the plan type, stored exactly as entered: the yearly AMC amount for AMC, the monthly amount
    /// for Monthly. Nothing is multiplied or totalled. (Property name kept for the existing API.)
    /// </summary>
    decimal? ProjectValue { get; }
    bool? WhatsAppOptOut { get; }
    /// <summary>"AMC" (yearly) or "Monthly". Omitted means AMC on create and "unchanged" on update.</summary>
    PlanType? PlanType { get; }
}

/// <summary>Body for POST /api/customers.</summary>
public record CreateCustomerRequest(
    string? CustomerName,
    string? Phone,
    string? Location,
    DateOnly? InstallationDate,
    string? ProjectName,
    decimal? ProjectValue,
    bool? WhatsAppOptOut,
    PlanType? PlanType = null) : ICustomerFields;

/// <summary>Body for PUT /api/customers/{id}. Same fields as create; IsActive is changed only by DELETE.</summary>
public record UpdateCustomerRequest(
    string? CustomerName,
    string? Phone,
    string? Location,
    DateOnly? InstallationDate,
    string? ProjectName,
    decimal? ProjectValue,
    bool? WhatsAppOptOut,
    PlanType? PlanType = null) : ICustomerFields;

/// <summary>One page of a list: { items, page, pageSize, totalCount }.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record CustomerDto(
    int CustomerId,
    string CustomerName,
    string Phone,
    string? Location,
    DateOnly InstallationDate,
    string? ProjectName,
    decimal? ProjectValue,
    bool WhatsAppOptOut,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    PlanType PlanType,
    /// <summary>Calculated: when the plan ends.</summary>
    DateOnly? PlanEndDate,
    /// <summary>Calculated: the next WhatsApp reminder date.</summary>
    DateOnly? ReminderDate);

// ---- Plans ----
/// <summary>Body for POST /api/customers/{id}/plans. StartDate defaults to the customer's installation date.</summary>
public record CreatePlanRequest(PlanType? PlanType, decimal? Amount, DateOnly? StartDate);
/// <summary>
/// Body for POST /api/plans/{id}/renew. The payment is required: <c>paidOn</c> (not in the future),
/// <c>amount</c> (received, above 0) and <c>mode</c> (Cash, UPI or Bank); <c>note</c> is optional.
/// The new cycle starts on the later of (previous EndDate + 1 day) and <c>paidOn</c>; <c>startDate</c> may
/// move it later but never earlier (422 start_before_default). <c>planAmount</c> is the amount of the new
/// plan (the rate for its type, 0 or more); it defaults to the previous plan's amount.
/// </summary>
public record RenewPlanRequest(
    DateOnly? PaidOn,
    decimal? Amount,
    PaymentMode? Mode,
    string? Note = null,
    DateOnly? StartDate = null,
    decimal? PlanAmount = null);

public record PaymentDto(
    int PaymentId,
    int PlanId,
    DateOnly PaidOn,
    decimal Amount,
    PaymentMode Mode,
    string? Note,
    DateTime CreatedAt);

/// <summary>The new plan and the payment recorded with it.</summary>
public record RenewalResultDto(PlanDto Plan, PaymentDto Payment);

public record PlanDto(
    int PlanId,
    int CustomerId,
    string CustomerName,
    string Phone,
    string? ProjectName,
    PlanType PlanType,
    /// <summary>Exactly as entered (open decision 1): never multiplied or totalled.</summary>
    decimal Amount,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly ReminderDate,
    /// <summary>Calculated on read from today's date and whether a renewal exists.</summary>
    PlanStatus Status,
    int? PreviousPlanId,
    /// <summary>The plan that renewed this one, when Status is Renewed.</summary>
    int? RenewedByPlanId,
    DateTime CreatedAt);

public record ReminderDto(
    int ReminderId,
    int PlanId,
    DateOnly ScheduledDate,
    DateTime? SentAt,
    string Channel,
    ReminderStatus Status,
    int Attempts,
    string? ErrorMessage,
    /// <summary>Failed, attempts left, and the plan still current: staff may press "Try again".</summary>
    bool CanRetry);

public record ReminderRunResult(int Processed, int Sent, int Failed, int Skipped);

// ---- Dashboard (counts and dates only: never an amount, a total or revenue) ----
/// <summary>The dashboard cards. Counts only; <c>AsOf</c> is the Asia/Kolkata date they were worked out for.</summary>
public record DashboardSummaryDto(
    int ActiveCount,
    int DueCount,
    int ExpiredCount,
    /// <summary>Not renewed, ending from today through today + 30 days (both inclusive).</summary>
    int EndingIn30DaysCount,
    int CustomerCount,
    DateOnly AsOf);

/// <summary>A plan that needs action now, for the dashboard list.</summary>
public record UpcomingPlanDto(
    int PlanId,
    int CustomerId,
    string CustomerName,
    string Phone,
    string? ProjectName,
    PlanType PlanType,
    DateOnly EndDate,
    DateOnly ReminderDate,
    PlanStatus Status,
    /// <summary>The reminder row for this plan and reminder date: Pending, Sent or Failed; null when none.</summary>
    ReminderStatus? ReminderStatus);

public record UpcomingResponse(IReadOnlyList<UpcomingPlanDto> Items);

/// <summary>One calendar month of the renewals chart. <c>EndingCount = RenewedCount + PendingCount</c>.</summary>
public record MonthlyRenewalPointDto(string Month, int EndingCount, int RenewedCount, int PendingCount);

public record MonthlyRenewalsResponse(IReadOnlyList<MonthlyRenewalPointDto> Months);

// ---- Reports: see ReportDtos.cs ----

/// <summary>A generated report file, returned to the caller and never stored.</summary>
public record ExportFile(byte[] Content, string ContentType, string FileName);
