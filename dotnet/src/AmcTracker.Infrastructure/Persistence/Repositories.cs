using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Services;
using AmcTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.Infrastructure.Persistence;

internal class CustomerRepository(AppDbContext db) : ICustomerRepository
{
    public Task<Customer?> GetByIdAsync(int id, bool includePlans, CancellationToken ct = default)
    {
        IQueryable<Customer> q = db.Customers;
        if (includePlans) q = q.Include(c => c.Plans);
        return q.FirstOrDefaultAsync(c => c.CustomerId == id, ct);
    }

    public async Task<(List<Customer> Items, int TotalCount)> SearchAsync(
        string? search, bool? active, int page, int pageSize, CancellationToken ct = default)
    {
        IQueryable<Customer> q = db.Customers.AsNoTracking();
        if (active is { } a) q = q.Where(c => c.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Lower-case on both sides so the match does not depend on the column collation.
            var s = search.Trim().ToLower();
            var phone = PhoneNumber.Normalize(search).ToLower();
            q = q.Where(c => c.CustomerName.ToLower().Contains(s)
                || c.Phone.Contains(phone)
                || (c.ProjectName != null && c.ProjectName.ToLower().Contains(s)));
        }
        var total = await q.CountAsync(ct);
        // Newest first, so a customer who was just added is at the top of page 1.
        var items = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.CustomerId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public void Add(Customer customer) => db.Customers.Add(customer);
}

internal class PlanRepository(AppDbContext db) : IPlanRepository
{
    public Task<Plan?> GetByIdAsync(int id, CancellationToken ct = default) =>
        db.Plans.Include(p => p.Customer).Include(p => p.Renewal).FirstOrDefaultAsync(p => p.PlanId == id, ct);

    public Task<List<Plan>> ListForCustomerAsync(int customerId, CancellationToken ct = default) =>
        db.Plans.AsNoTracking().Include(p => p.Customer).Include(p => p.Renewal)
            .Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.PlanId)
            .ToListAsync(ct);

    public async Task<Plan?> GetCurrentForCustomerAsync(
        int customerId, PlanType? preferredType, CancellationToken ct = default)
    {
        var open = db.Plans.Include(p => p.Renewal)
            .Where(p => p.CustomerId == customerId && p.Renewal == null)
            .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.PlanId);
        if (preferredType is { } type && await open.FirstOrDefaultAsync(p => p.PlanType == type, ct) is { } plan)
            return plan;
        return await open.FirstOrDefaultAsync(ct);
    }

    public Task<bool> HasOpenPlanAsync(int customerId, PlanType type, int? exceptPlanId, CancellationToken ct = default) =>
        db.Plans.AnyAsync(p => p.CustomerId == customerId && p.PlanType == type && p.Renewal == null
            && (exceptPlanId == null || p.PlanId != exceptPlanId), ct);

    public Task<List<Plan>> ListAsync(PlanFilter f, DateOnly today, CancellationToken ct = default) =>
        Filter(f, today).OrderBy(p => p.EndDate).ThenBy(p => p.PlanId).ToListAsync(ct);

    public async Task<(List<Plan> Items, int TotalCount)> PageAsync(
        PlanFilter f, DateOnly today, int page, int pageSize, CancellationToken ct = default)
    {
        var q = Filter(f, today);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => p.EndDate).ThenBy(p => p.PlanId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task<List<Plan>> ListRemindersDueAsync(DateOnly from, DateOnly today, CancellationToken ct = default) =>
        db.Plans.AsNoTracking().Include(p => p.Customer)
            .Where(p => p.ReminderDate >= from && p.ReminderDate <= today && today <= p.EndDate
                && p.Renewal == null
                && p.Customer!.IsActive && !p.Customer.WhatsAppOptOut
                // Catch-up guards: a plan added after its reminder date never sends a late reminder, and a plan
                // with a Sent reminder is never sent another.
                && DateOnly.FromDateTime(p.CreatedAt.AddMinutes(IstOffsetMinutes)) <= p.ReminderDate
                && !db.Reminders.Any(r => r.PlanId == p.PlanId && r.Status == ReminderStatus.Sent))
            .OrderBy(p => p.ReminderDate).ThenBy(p => p.PlanId)
            .ToListAsync(ct);

    /// <summary>Asia/Kolkata is UTC+05:30 all year (no daylight saving). CreatedAt is stored in UTC.</summary>
    private const int IstOffsetMinutes = 330;

    public void Add(Plan plan) => db.Plans.Add(plan);

    /// <summary>Status is calculated from the dates and the renewal link, in SQL, the same way as PlanStatusCalculator.</summary>
    private IQueryable<Plan> Filter(PlanFilter f, DateOnly today)
    {
        IQueryable<Plan> q = db.Plans.AsNoTracking().Include(p => p.Customer).Include(p => p.Renewal);
        if (!f.IncludeInactiveCustomers) q = q.Where(p => p.Customer!.IsActive);
        if (f.CustomerId is { } cid) q = q.Where(p => p.CustomerId == cid);
        if (f.Type is { } type) q = q.Where(p => p.PlanType == type);

        // The status rules live in PlanSpec, shared with the dashboard, so a count and its list always agree.
        if (f.Status is { } status) q = q.Where(PlanSpec.HasStatus(status, today));
        if (f.EndingWithinDays is { } days) q = q.Where(PlanSpec.EndsWithin(today, days));

        if (f.From is { } from)
        {
            q = f.Basis switch
            {
                DateBasis.ReminderDate => q.Where(p => p.ReminderDate >= from),
                DateBasis.StartDate => q.Where(p => p.StartDate >= from),
                _ => q.Where(p => p.EndDate >= from),
            };
        }
        if (f.To is { } to)
        {
            q = f.Basis switch
            {
                DateBasis.ReminderDate => q.Where(p => p.ReminderDate <= to),
                DateBasis.StartDate => q.Where(p => p.StartDate <= to),
                _ => q.Where(p => p.EndDate <= to),
            };
        }
        return q;
    }
}

internal class ReminderRepository(AppDbContext db) : IReminderRepository
{
    public Task<List<Reminder>> ListForPlanAsync(int planId, CancellationToken ct = default) =>
        db.Reminders.AsNoTracking()
            .Include(r => r.Plan!).ThenInclude(p => p.Renewal)
            .Where(r => r.PlanId == planId)
            .OrderByDescending(r => r.ScheduledDate).ThenByDescending(r => r.ReminderId).ToListAsync(ct);

    public Task<Reminder?> GetAsync(int planId, DateOnly scheduledDate, CancellationToken ct = default) =>
        db.Reminders.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PlanId == planId && r.ScheduledDate == scheduledDate, ct);

    public Task<Reminder?> GetByIdAsync(int reminderId, CancellationToken ct = default) =>
        WithPlan().FirstOrDefaultAsync(r => r.ReminderId == reminderId, ct);

    public Task<List<Reminder>> ListRetryableAsync(DateOnly today, CancellationToken ct = default) =>
        WithPlan()
            .Where(r => r.Status == ReminderStatus.Failed
                && r.ScheduledDate <= today
                && r.Attempts < Reminder.MaxAttempts
                && r.Plan!.Renewal == null && today <= r.Plan.EndDate
                && r.Plan.Customer!.IsActive && !r.Plan.Customer.WhatsAppOptOut)
            .OrderBy(r => r.ScheduledDate).ThenBy(r => r.ReminderId)
            .ToListAsync(ct);

    public async Task<bool> TryAddAsync(Reminder reminder, CancellationToken ct = default)
    {
        db.Reminders.Add(reminder);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (AppDbContext.IsUniqueViolation(ex))
        {
            db.Entry(reminder).State = EntityState.Detached; // so a later save does not try it again
            return false;
        }
    }

    private IQueryable<Reminder> WithPlan() => db.Reminders
        .Include(r => r.Plan!).ThenInclude(p => p.Customer)
        .Include(r => r.Plan!).ThenInclude(p => p.Renewal);
}

internal class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public void Add(Payment payment) => db.Payments.Add(payment);

    public Task<List<Payment>> ListForPlanAsync(int planId, CancellationToken ct = default) =>
        db.Payments.AsNoTracking().Where(p => p.PlanId == planId)
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.PaymentId)
            .ToListAsync(ct);
}

/// <summary>
/// Dashboard queries: one grouped or filtered query each, over plans of active customers. The status rules come
/// from <see cref="PlanSpec"/>. Counts and dates only.
/// </summary>
internal class DashboardRepository(AppDbContext db) : IDashboardRepository
{
    private IQueryable<Plan> ActivePlans => db.Plans.AsNoTracking().Where(p => p.Customer!.IsActive);

    public async Task<StatusCounts> CountByStatusAsync(DateOnly today, CancellationToken ct = default)
    {
        var rows = await ActivePlans.GroupBy(PlanSpec.StatusOf(today))
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(PlanStatus s) => rows.Where(r => r.Status == s).Sum(r => r.Count);
        return new StatusCounts(Of(PlanStatus.Active), Of(PlanStatus.DueForRenewal), Of(PlanStatus.Expired), Of(PlanStatus.Renewed));
    }

    public Task<int> CountEndingWithinAsync(DateOnly today, int days, CancellationToken ct = default) =>
        ActivePlans.CountAsync(PlanSpec.EndsWithin(today, days), ct);

    public Task<int> CountActiveCustomersAsync(CancellationToken ct = default) =>
        db.Customers.AsNoTracking().CountAsync(c => c.IsActive, ct);

    public Task<List<UpcomingRow>> ListNeedingActionAsync(DateOnly today, int days, CancellationToken ct = default) =>
        ActivePlans.Where(PlanSpec.NeedsAction(today, days))
            .OrderBy(p => p.EndDate).ThenBy(p => p.PlanId)
            .Select(p => new UpcomingRow(
                p.PlanId, p.CustomerId, p.Customer!.CustomerName, p.Customer.Phone, p.Customer.ProjectName,
                p.PlanType, p.EndDate, p.ReminderDate,
                // The reminder row for this plan and reminder date, read in the same query.
                db.Reminders.Where(r => r.PlanId == p.PlanId && r.ScheduledDate == p.ReminderDate)
                    .OrderByDescending(r => r.ReminderId)
                    .Select(r => (ReminderStatus?)r.Status).FirstOrDefault()))
            .ToListAsync(ct);

    public async Task<List<MonthCount>> CountEndingByMonthAsync(DateOnly first, DateOnly last, CancellationToken ct = default)
    {
        var rows = await ActivePlans.Where(p => p.EndDate >= first && p.EndDate <= last)
            .GroupBy(p => new { p.EndDate.Year, p.EndDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Ending = g.Count(), Renewed = g.Sum(p => p.Renewal != null ? 1 : 0) })
            .ToListAsync(ct);
        return rows.Select(r => new MonthCount(r.Year, r.Month, r.Ending, r.Renewed)).ToList();
    }
}

internal class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(int id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.UserId == id, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<UserDevice?> GetDeviceAsync(string fingerprint, CancellationToken ct = default) =>
        db.UserDevices.Include(d => d.User).FirstOrDefaultAsync(d => d.DeviceFingerprint == fingerprint, ct);

    public void Add(User user) => db.Users.Add(user);
    public void AddDevice(UserDevice device) => db.UserDevices.Add(device);
}
