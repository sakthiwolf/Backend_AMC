using AmcTracker.Application.Abstractions;
using AmcTracker.Domain.Entities;
using AmcTracker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.Api.Infrastructure;

/// <summary>
/// Creates the staff login: <c>dotnet AmcTracker.Api.dll create-user &lt;username&gt; [full name]</c>.
/// The password comes from AMC_NEW_PASSWORD if set, otherwise it is prompted for (hidden).
/// </summary>
public static class CreateUserCommand
{
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: create-user <username> [full name]");
            return 2;
        }
        var username = args[0].Trim();
        var fullName = args.Length > 1 ? string.Join(' ', args[1..]) : null;
        if (username.Length is 0 or > 50)
        {
            Console.Error.WriteLine("Username must be 1 to 50 characters.");
            return 2;
        }

        var password = Environment.GetEnvironmentVariable("AMC_NEW_PASSWORD");
        if (password is null)
        {
            password = ReadHidden("Password (hidden while typing): ");
            if (password != ReadHidden("Repeat password: "))
            {
                Console.Error.WriteLine("Passwords do not match.");
                return 2;
            }
        }
        if (password.Length < 8)
        {
            Console.Error.WriteLine("Password must be at least 8 characters.");
            return 2;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        if (await db.Users.AnyAsync(u => u.Username == username))
        {
            Console.Error.WriteLine($"User '{username}' already exists.");
            return 1;
        }
        db.Users.Add(new User
        {
            Username = username,
            FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        Console.WriteLine($"Created user {username}.");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
