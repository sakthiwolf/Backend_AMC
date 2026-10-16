using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Application.Abstractions;
using AmcTracker.Infrastructure.Persistence;
using AmcTracker.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AmcTracker.IntegrationTests;

/// <summary>Records what the reminder job tried to send; can be told to fail.</summary>
public class FakeWhatsAppSender : IWhatsAppSender
{
    public List<WhatsAppReminderMessage> Sent { get; } = [];
    public bool Fail { get; set; }

    public Task<WhatsAppSendResult> SendRenewalReminderAsync(WhatsAppReminderMessage m, CancellationToken ct = default)
    {
        Sent.Add(m);
        return Task.FromResult(Fail ? new WhatsAppSendResult(false, "HTTP 400: simulated failure") : new WhatsAppSendResult(true));
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

    public FakeWhatsAppSender Whatsapp { get; } = new();
    public IConfiguration TestConfig { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    public string ConnectionString =>
        TestConfig.GetConnectionString("Test")
        ?? throw new InvalidOperationException("ConnectionStrings:Test is not configured.");

    public DateOnly Today { get; } = DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZones.Resolve("Asia/Kolkata")));

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
        Whatsapp.Sent.Clear();
        Whatsapp.Fail = false;
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
