using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Dtos;

/// <summary>Which plan date the from/to range of a report applies to.</summary>
public enum DateBasis
{
    EndDate,
    NotificationDate,
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
/// <summary>Body for POST and PUT /api/customers. IsActive is honoured on PUT only.</summary>
public record CustomerRequest(
    string? CustomerName,
    string? Phone,
    string? Location,
    DateOnly? InstallationDate,
    string? ProjectName,
    decimal? ProjectValue,
    bool? WhatsAppOptOut,
    bool? IsActive);

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
    /// <summary>Status of the customer's latest plan; null when they have none.</summary>
    PlanStatus? PlanStatus);

public record CustomerDetailDto(CustomerDto Customer, IReadOnlyList<PlanDto> Plans);

// ---- Plans ----
public record CreatePlanRequest(PlanType? PlanType, decimal? Amount, DateOnly? StartDate);
/// <summary>Both optional. Start defaults to previous EndDate + 1 day; amount to the previous amount.</summary>
public record RenewPlanRequest(DateOnly? StartDate, decimal? Amount);

public record PlanDto(
    int PlanId,
    int CustomerId,
    string CustomerName,
    string Phone,
    string? ProjectName,
    PlanType PlanType,
    decimal Amount,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly NotificationDate,
    PlanStatus Status,
    int? PreviousPlanId,
    DateTime CreatedAt);

public record ReminderDto(
    int ReminderId,
    int PlanId,
    DateOnly ScheduledDate,
    DateTime? SentAt,
    string Channel,
    ReminderStatus Status,
    string? ErrorMessage);

public record ReminderRunResult(int Processed, int Sent, int Failed, int Skipped);

// ---- Dashboard ----
public record DashboardSummaryDto(
    int ActiveCount,
    int DueForRenewalCount,
    int DueIn30DaysCount,
    int ExpiredCount,
    decimal MonthlyRevenue);

public record MonthlyRenewalDto(int Year, int Month, string Label, int Count);

// ---- Reports ----
public record ReportResultDto(
    DateOnly? From, DateOnly? To, DateBasis Basis, int Count, IReadOnlyList<PlanDto> Items);

public record ExportFile(byte[] Content, string ContentType, string FileName);
