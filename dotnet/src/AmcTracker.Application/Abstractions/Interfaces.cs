using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Abstractions;

public interface IUnitOfWork
{
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
    /// <summary>Name, phone and project name match <paramref name="search"/> (case-insensitive). Ordered by name.</summary>
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
    bool IncludeInactiveCustomers = false);

public interface IPlanRepository
{
    /// <summary>Loads the plan with its customer.</summary>
    Task<Plan?> GetByIdAsync(int id, CancellationToken ct = default);
    /// <summary>The customer's newest plan that has not been renewed (tracked), or null.</summary>
    Task<Plan?> GetLatestOpenForCustomerAsync(int customerId, CancellationToken ct = default);
    /// <summary>Plans with customers, filtered by effective status as of <paramref name="today"/>.</summary>
    Task<List<Plan>> ListAsync(PlanFilter filter, DateOnly today, CancellationToken ct = default);
    /// <summary>
    /// Candidate plans for today's reminder run: non-renewed plans of active, non-opted-out customers.
    /// Yearly plans: NotificationDate has arrived, not ended, no Sent reminder yet. Monthly plans: inside
    /// their year (the service then works out which month's reminder is open).
    /// </summary>
    Task<List<Plan>> ListPendingRemindersAsync(DateOnly today, CancellationToken ct = default);
    /// <summary>Rewrites the stored Status of non-renewed plans from the dates.</summary>
    Task SyncStatusesAsync(DateOnly today, CancellationToken ct = default);
    void Add(Plan plan);
}

public interface IReminderRepository
{
    Task<List<Reminder>> ListForPlanAsync(int planId, CancellationToken ct = default);
    Task<Reminder?> GetAsync(int planId, DateOnly scheduledDate, CancellationToken ct = default);
    void Add(Reminder reminder);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<UserDevice?> GetDeviceAsync(string fingerprint, CancellationToken ct = default);
    void Add(User user);
    void AddDevice(UserDevice device);
}

public record WhatsAppReminderMessage(
    string Phone, string CustomerName, string? ProjectName, DateOnly EndDate);

public record WhatsAppSendResult(bool Success, string? Error = null);

public interface IWhatsAppSender
{
    Task<WhatsAppSendResult> SendRenewalReminderAsync(
        WhatsAppReminderMessage message, CancellationToken ct = default);
}

public interface IExcelExporter
{
    byte[] Export(IReadOnlyList<PlanDto> rows, string title);
}

public interface IPdfExporter
{
    byte[] Export(IReadOnlyList<PlanDto> rows, string title);
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
