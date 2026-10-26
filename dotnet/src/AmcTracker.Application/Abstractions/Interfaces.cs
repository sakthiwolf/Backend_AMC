using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Saves pending changes. A unique-key violation surfaces as <see cref="Common.DuplicateKeyException"/>.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Business time. "Today" is the calendar date in Asia/Kolkata.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id, bool includePlans, CancellationToken ct = default);
    /// <summary>Name, phone and project name match <paramref name="search"/> (case-insensitive). Newest first.</summary>
    Task<(List<Customer> Items, int TotalCount)> SearchAsync(
        string? search, bool? active, int page, int pageSize, CancellationToken ct = default);
    void Add(Customer customer);
}

public record PlanFilter(
    PlanStatus? Status = null,
    PlanType? Type = null,
    DateBasis Basis = DateBasis.EndDate,
    DateOnly? From = null,
    DateOnly? To = null,
    int? CustomerId = null,
    bool IncludeInactiveCustomers = false,
    /// <summary>Not renewed, ending from today through today + this many days (the dashboard's "ending in 30 days" list).</summary>
    int? EndingWithinDays = null);

/// <summary>Every plan read loads the plan's renewal, so <see cref="Plan.IsRenewed"/> and the status are right.</summary>
public interface IPlanRepository
{
    /// <summary>The plan with its customer and renewal (tracked), or null.</summary>
    Task<Plan?> GetByIdAsync(int id, CancellationToken ct = default);
    /// <summary>All of a customer's plans, newest first (by start date, then id).</summary>
    Task<List<Plan>> ListForCustomerAsync(int customerId, CancellationToken ct = default);
    /// <summary>
    /// The customer's newest non-renewed plan (tracked), preferring <paramref name="preferredType"/> when the
    /// customer has an open plan of that type. Null when every plan has been renewed or there are none.
    /// </summary>
    Task<Plan?> GetCurrentForCustomerAsync(int customerId, PlanType? preferredType, CancellationToken ct = default);
    /// <summary>True when the customer has a non-renewed plan of <paramref name="type"/> other than <paramref name="exceptPlanId"/>.</summary>
    Task<bool> HasOpenPlanAsync(int customerId, PlanType type, int? exceptPlanId, CancellationToken ct = default);
    /// <summary>Plans with customers, filtered by status as of <paramref name="today"/>.</summary>
    Task<List<Plan>> ListAsync(PlanFilter filter, DateOnly today, CancellationToken ct = default);
    /// <summary>One page of <see cref="ListAsync"/>, ordered by end date.</summary>
    Task<(List<Plan> Items, int TotalCount)> PageAsync(
        PlanFilter filter, DateOnly today, int page, int pageSize, CancellationToken ct = default);
    /// <summary>
    /// Plans whose reminder should go out now: ReminderDate between <paramref name="from"/> and
    /// <paramref name="today"/> (the catch-up window), not renewed, not ended, the customer active and not opted
    /// out. Skips a plan created (IST date) after its ReminderDate and a plan that already has a Sent reminder.
    /// Loaded with their customers.
    /// </summary>
    Task<List<Plan>> ListRemindersDueAsync(DateOnly from, DateOnly today, CancellationToken ct = default);
    void Add(Plan plan);
}

public interface IReminderRepository
{
    /// <summary>A plan's reminders, newest first, with the plan and its renewal.</summary>
    Task<List<Reminder>> ListForPlanAsync(int planId, CancellationToken ct = default);
    Task<Reminder?> GetAsync(int planId, DateOnly scheduledDate, CancellationToken ct = default);
    /// <summary>The reminder with its plan, the plan's renewal and the customer (tracked), or null.</summary>
    Task<Reminder?> GetByIdAsync(int reminderId, CancellationToken ct = default);
    /// <summary>
    /// Failed reminders the daily run tries again: scheduled on or before <paramref name="today"/>, fewer than
    /// <see cref="Reminder.MaxAttempts"/> attempts, plan not renewed and not past its end date, customer active
    /// and not opted out. Loaded with plan, renewal and customer (tracked).
    /// </summary>
    Task<List<Reminder>> ListRetryableAsync(DateOnly today, CancellationToken ct = default);
    /// <summary>
    /// Inserts and saves the reminder. Returns false, and leaves nothing pending, when the unique
    /// (PlanId, ScheduledDate) key already holds a row: the database-level "never send twice".
    /// </summary>
    Task<bool> TryAddAsync(Reminder reminder, CancellationToken ct = default);
}

/// <summary>Plan counts by status, for plans of active customers.</summary>
public record StatusCounts(int Active, int Due, int Expired, int Renewed);

/// <summary>One row of the "needs action" list, read together with its customer and reminder in one query.</summary>
public record UpcomingRow(
    int PlanId,
    int CustomerId,
    string CustomerName,
    string Phone,
    string? ProjectName,
    PlanType PlanType,
    DateOnly EndDate,
    DateOnly ReminderDate,
    ReminderStatus? ReminderStatus);

/// <summary>Plans ending in one calendar month, and how many of them have a renewal.</summary>
public record MonthCount(int Year, int Month, int Ending, int Renewed);

/// <summary>
/// Read-only queries for the dashboard. Each one is a single grouped or filtered query over plans of active
/// customers, using the status rules in <see cref="Domain.Services.PlanSpec"/>. Nothing is stored.
/// </summary>
public interface IDashboardRepository
{
    Task<StatusCounts> CountByStatusAsync(DateOnly today, CancellationToken ct = default);
    Task<int> CountEndingWithinAsync(DateOnly today, int days, CancellationToken ct = default);
    Task<int> CountActiveCustomersAsync(CancellationToken ct = default);
    /// <summary>Due plans and plans reminding within <paramref name="days"/> days, soonest end date first.</summary>
    Task<List<UpcomingRow>> ListNeedingActionAsync(DateOnly today, int days, CancellationToken ct = default);
    /// <summary>Plans ending from <paramref name="first"/> through <paramref name="last"/>, counted per calendar month.</summary>
    Task<List<MonthCount>> CountEndingByMonthAsync(DateOnly first, DateOnly last, CancellationToken ct = default);
}

/// <summary>Renewal payments. Add and read only: payments are never updated or deleted through the API.</summary>
public interface IPaymentRepository
{
    void Add(Payment payment);
    /// <summary>The payments made for one plan, newest first.</summary>
    Task<List<Payment>> ListForPlanAsync(int planId, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserDevice?> GetDeviceAsync(string fingerprint, CancellationToken ct = default);
    void Add(User user);
    void AddDevice(UserDevice device);
}

/// <summary>
/// The outcome of one WhatsApp send. <see cref="ErrorMessage"/> is at most 500 characters and never holds a token.
/// <see cref="IsRetryable"/> is false for a request the provider refused (4xx other than 429): trying again
/// would fail the same way. <see cref="HttpStatus"/> and <see cref="Duration"/> are for logging only.
/// </summary>
public record WhatsAppSendResult(
    bool Success,
    string? ProviderMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool IsRetryable = true,
    int? HttpStatus = null,
    TimeSpan? Duration = null)
{
    public const int MaxErrorLength = 500;

    public static WhatsAppSendResult Failed(string message, bool retryable, string? code = null,
        int? httpStatus = null, TimeSpan? duration = null) =>
        new(false, null, code, message.Length <= MaxErrorLength ? message : message[..MaxErrorLength],
            retryable, httpStatus, duration);
}

/// <summary>Sends an approved WhatsApp template. Implementations never log phone numbers or message bodies.</summary>
public interface IWhatsAppSender
{
    /// <param name="phone">E.164 recipient, e.g. +919876543210.</param>
    /// <param name="templateName">The approved template (configuration, not code).</param>
    /// <param name="parameters">Body parameters, in template order.</param>
    Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public record AccessToken(string Token, int ExpiresInSeconds);

public interface IJwtTokenService
{
    AccessToken CreateAccessToken(User user);
    string GenerateRefreshToken();
    string HashRefreshToken(string token);
    bool RefreshTokenMatches(string token, string storedHash);
}
