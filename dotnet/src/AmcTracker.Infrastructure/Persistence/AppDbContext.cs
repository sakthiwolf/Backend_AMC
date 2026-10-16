using AmcTracker.Application.Abstractions;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AmcTracker.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // All timestamps are UTC; mark them so JSON output carries the "Z".
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HaveColumnType("datetime2");
        builder.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);
}

public class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> e)
    {
        e.ToTable("Users");
        e.HasKey(x => x.UserId);
        e.Property(x => x.Username).HasMaxLength(50).IsRequired();
        e.HasIndex(x => x.Username).IsUnique();
        e.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
        e.Property(x => x.FullName).HasMaxLength(100);
        e.Property(x => x.FailedLoginCount).HasDefaultValue(0);
        e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
    }
}

internal class UserDeviceConfiguration : IEntityTypeConfiguration<UserDevice>
{
    public void Configure(EntityTypeBuilder<UserDevice> e)
    {
        e.ToTable("UserDevices");
        e.HasKey(x => x.DeviceId);
        e.Property(x => x.DeviceFingerprint).HasMaxLength(100).IsRequired();
        e.HasIndex(x => x.DeviceFingerprint).IsUnique();
        e.Property(x => x.DeviceLabel).HasMaxLength(100);
        e.Property(x => x.RefreshTokenHash).HasMaxLength(64).IsRequired();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        e.HasIndex(x => x.UserId);
        e.HasOne(x => x.User).WithMany(u => u.Devices).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> e)
    {
        e.ToTable("Customers");
        e.HasKey(x => x.CustomerId);
        e.Property(x => x.CustomerName).HasMaxLength(150).IsRequired();
        e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
        e.Property(x => x.Location).HasMaxLength(255);
        e.Property(x => x.InstallationDate).HasColumnType("date");
        e.Property(x => x.ProjectName).HasMaxLength(150);
        e.Property(x => x.ProjectValue).HasColumnType("decimal(12,2)");
        e.Property(x => x.WhatsAppOptOut).HasDefaultValue(false);
        e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
    }
}

internal class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> e)
    {
        e.ToTable("Plans");
        e.HasKey(x => x.PlanId);
        e.Property(x => x.PlanType).HasConversion<string>().HasMaxLength(10).IsRequired();
        e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
        e.Property(x => x.StartDate).HasColumnType("date");
        e.Property(x => x.EndDate).HasColumnType("date");
        e.Property(x => x.NotificationDate).HasColumnType("date");
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        e.HasIndex(x => x.CustomerId);
        e.HasIndex(x => x.NotificationDate);
        e.HasIndex(x => x.EndDate);
        // Customers are soft-deleted only, so no cascades anywhere.
        e.HasOne(x => x.Customer).WithMany(c => c.Plans).HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.PreviousPlan).WithMany().HasForeignKey(x => x.PreviousPlanId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> e)
    {
        e.ToTable("Reminders");
        e.HasKey(x => x.ReminderId);
        e.Property(x => x.ScheduledDate).HasColumnType("date");
        e.Property(x => x.Channel).HasMaxLength(20).IsRequired().HasDefaultValue("WhatsApp");
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.ErrorMessage).HasMaxLength(500);
        // Enforces "never send twice" at the database level.
        e.HasIndex(x => new { x.PlanId, x.ScheduledDate }).IsUnique().HasDatabaseName("UQ_Reminder_PlanDate");
        e.HasOne(x => x.Plan).WithMany(p => p.Reminders).HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> e)
    {
        e.ToTable("Payments");
        e.HasKey(x => x.PaymentId);
        e.Property(x => x.PaidOn).HasColumnType("date");
        e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
        e.Property(x => x.Mode).HasMaxLength(20);
        e.HasIndex(x => x.PlanId);
        e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}
