using AmcTracker.Application.Abstractions;
using AmcTracker.Domain.Entities;
using AmcTracker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.Api.Infrastructure;

/// <summary>
/// Creates the first staff login at startup from configuration (Seed:Username, Seed:Password, Seed:FullName).
/// Does nothing when Seed:Username or Seed:Password is empty, or when that username already exists.
/// </summary>
public static class SeedUser
{
    public static async Task RunAsync(WebApplication app)
    {
        var username = app.Configuration["Seed:Username"]?.Trim();
        var password = app.Configuration["Seed:Password"];
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return;

        using var scope = app.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Users.AnyAsync(u => u.Username == username)) return;
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var fullName = app.Configuration["Seed:FullName"];
            db.Users.Add(new User
            {
                Username = username,
                FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName,
                PasswordHash = hasher.Hash(password),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            log.LogInformation("Seeded login user {Username}.", username);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Seeding the login user failed (is the database migrated?).");
        }
    }
}
