using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Entities;
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

    public Task<List<Customer>> ListAsync(string? search, bool? active, CancellationToken ct = default)
    {
        IQueryable<Customer> q = db.Customers.AsNoTracking().Include(c => c.Plans);
        if (active is { } a) q = q.Where(c => c.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => c.CustomerName.Contains(s) || c.Phone.Contains(s)
                || (c.Location != null && c.Location.Contains(s))
                || (c.ProjectName != null && c.ProjectName.Contains(s)));
        }
        return q.OrderBy(c => c.CustomerName).ThenBy(c => c.CustomerId).ToListAsync(ct);
    }

    public void Add(Customer customer) => db.Customers.Add(customer);
}

internal class PlanRepository(AppDbContext db) : IPlanRepository
{
    public Task<Plan?> GetByIdAsync(int id, CancellationToken ct = default) =>
        db.Plans.Include(p => p.Customer).FirstOrDefaultAsync(p => p.PlanId == id, ct);

    public Task<List<Plan>> ListAsync(PlanFilter f, DateOnly today, CancellationToken ct = default)
    {
        IQueryable<Plan> q = db.Plans.AsNoTracking().Include(p => p.Customer);
        if (!f.IncludeInactiveCustomers) q = q.Where(p => p.Customer!.IsActive);
        if (f.CustomerId is { } cid) q = q.Where(p => p.CustomerId == cid);
        if (f.Type is { } type) q = q.Where(p => p.PlanType == type);

        // Effective status is derived from dates; only Renewed is stored authoritatively.
        if (f.Status is { } status)
        {
            q = status switch
            {
                PlanStatus.Renewed => q.Where(p => p.Status == PlanStatus.Renewed),
                PlanStatus.Active => q.Where(p => p.Status != PlanStatus.Renewed && today < p.NotificationDate),
                PlanStatus.DueForRenewal => q.Where(p => p.Status != PlanStatus.Renewed
                    && p.NotificationDate <= today && today <= p.EndDate),
                _ => q.Where(p => p.Status != PlanStatus.Renewed && today > p.EndDate),
            };
        }

        if (f.From is { } from)
        {
            q = f.Basis switch
            {
                DateBasis.NotificationDate => q.Where(p => p.NotificationDate >= from),
                DateBasis.StartDate => q.Where(p => p.StartDate >= from),
                _ => q.Where(p => p.EndDate >= from),
            };
        }
        if (f.To is { } to)
        {
            q = f.Basis switch
            {
                DateBasis.NotificationDate => q.Where(p => p.NotificationDate <= to),
                DateBasis.StartDate => q.Where(p => p.StartDate <= to),
                _ => q.Where(p => p.EndDate <= to),
            };
        }
        return q.OrderBy(p => p.EndDate).ThenBy(p => p.PlanId).ToListAsync(ct);
    }

    public Task<List<Plan>> ListPendingRemindersAsync(DateOnly today, CancellationToken ct = default) =>
        db.Plans.Include(p => p.Customer)
            .Where(p => p.Status != PlanStatus.Renewed
                && p.Customer!.IsActive && !p.Customer.WhatsAppOptOut
                && p.NotificationDate <= today && today <= p.EndDate
                && !db.Reminders.Any(r => r.PlanId == p.PlanId && r.Status == ReminderStatus.Sent))
            .OrderBy(p => p.NotificationDate).ThenBy(p => p.PlanId)
            .ToListAsync(ct);

    public async Task SyncStatusesAsync(DateOnly today, CancellationToken ct = default)
    {
        await db.Plans.Where(p => p.Status != PlanStatus.Renewed && p.Status != PlanStatus.Active
                && today < p.NotificationDate)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanStatus.Active), ct);
        await db.Plans.Where(p => p.Status != PlanStatus.Renewed && p.Status != PlanStatus.DueForRenewal
                && p.NotificationDate <= today && today <= p.EndDate)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanStatus.DueForRenewal), ct);
        await db.Plans.Where(p => p.Status != PlanStatus.Renewed && p.Status != PlanStatus.Expired
                && today > p.EndDate)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanStatus.Expired), ct);
    }

    public void Add(Plan plan) => db.Plans.Add(plan);
}

internal class ReminderRepository(AppDbContext db) : IReminderRepository
{
    public Task<List<Reminder>> ListForPlanAsync(int planId, CancellationToken ct = default) =>
        db.Reminders.AsNoTracking().Where(r => r.PlanId == planId)
            .OrderByDescending(r => r.ScheduledDate).ThenByDescending(r => r.ReminderId).ToListAsync(ct);

    public Task<Reminder?> GetAsync(int planId, DateOnly scheduledDate, CancellationToken ct = default) =>
        db.Reminders.FirstOrDefaultAsync(r => r.PlanId == planId && r.ScheduledDate == scheduledDate, ct);

    public void Add(Reminder reminder) => db.Reminders.Add(reminder);
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
