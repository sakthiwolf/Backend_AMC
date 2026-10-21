using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Application.Abstractions;
using AmcTracker.Infrastructure.Persistence;
using AmcTracker.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AmcTracker.IntegrationTests;

/// <summary>Business clock the tests can move: <see cref="Today"/> is real IST today unless <see cref="Fixed"/> is set.</summary>
public class TestClock : IClock
{
    public DateOnly? Fixed { get; set; }
    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => Fixed ?? DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZones.Resolve("Asia/Kolkata")));
}

/// <summary>Records the text of every SQL statement the API runs, so a test can count them.</summary>
public sealed class SqlLog : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> Commands => [.. _commands];

    /// <summary>Statements that are not the login-token check (which reads [Users] on every request).</summary>
    public IReadOnlyList<string> Report => [.. _commands.Where(c => !c.Contains("[Users]"))];

    public void Clear() => _commands.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, ct);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, ct);
    }
}

/// <summary>
/// Starts the real API against a throwaway SQL Server database. The connection string comes from
/// configuration (ConnectionStrings:Test in appsettings.json, or env var ConnectionStrings__Test).
/// The database is dropped and recreated through the EF migrations, so its name must contain "Test".
/// </summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "correct-horse-battery";
    public const string Fingerprint = "11111111-1111-4111-8111-111111111111";

    /// <summary>Records every send; never contacts WhatsApp. Set FailWith to simulate errors.</summary>
    public FakeWhatsAppSender Whatsapp { get; } = new();
    public TestClock Clock { get; } = new();
    public SqlLog Sql { get; } = new();
    public IConfiguration TestConfig { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    public string ConnectionString =>
        TestConfig.GetConnectionString("Test")
        ?? throw new InvalidOperationException("ConnectionStrings:Test is not configured.");

    /// <summary>The API's "today" (moves with <see cref="Clock"/>).</summary>
    public DateOnly Today => Clock.Today;

    public ApiFixture()
    {
        var db = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!db.Contains("Test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to wipe database '{db}': the test database name must contain 'Test'.");

        // Environment variables are read by Program before any test hook can run.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Secret", "integration-test-signing-key-0123456789abcdef");
        Environment.SetEnvironmentVariable("Reminders__Enabled", "false");
        Environment.SetEnvironmentVariable("Reminders__AdminUsernames__0", "staff");
        Environment.SetEnvironmentVariable("WhatsApp__UseFake", "true");
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path",
            Path.Combine(Path.GetTempPath(), "amc-tests", "log-.txt"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IWhatsAppSender>();
            s.AddSingleton<IWhatsAppSender>(Whatsapp);
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(Clock);
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(Sql));
        });
    }

    public async ValueTask InitializeAsyncCore()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.InitializeAsync() => InitializeAsyncCore().AsTask();
    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>Empties every table so each test starts clean.</summary>
    public async Task ResetAsync()
    {
        Whatsapp.Clear();
        Clock.Fixed = null;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM Payments; DELETE FROM Reminders; UPDATE Plans SET PreviousPlanId = NULL; DELETE FROM Plans; " +
            "DELETE FROM Customers; DELETE FROM UserDevices; DELETE FROM Users;");
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task AddUserAsync(string username, string fullName = "Test User")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<AmcTracker.Application.Abstractions.IPasswordHasher>();
        db.Users.Add(new AmcTracker.Domain.Entities.User
        {
            Username = username, FullName = fullName, PasswordHash = hasher.Hash(Password), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public HttpClient Anonymous() => CreateClient();

    /// <summary>A client signed in as a fresh staff user.</summary>
    public async Task<HttpClient> SignedInAsync(string username = "staff")
    {
        await AddUserAsync(username);
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = Password, deviceFingerprint = Fingerprint });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        return client;
    }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;
