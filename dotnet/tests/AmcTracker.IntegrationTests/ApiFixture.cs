using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Options;
using AmcTracker.Infrastructure.Jobs;
using AmcTracker.Infrastructure.Persistence;
using AmcTracker.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// A stand-in for the Wacto HTTP endpoint. While <see cref="UseWacto"/> is true the API sends through the
    /// real <see cref="WhatsAppCloudSender"/>, whose HttpClient talks only to this handler: nothing leaves the
    /// machine.
    /// </summary>
    public WactoStub Wacto { get; } = new();
    public bool UseWacto { get; set; }

    /// <summary>The settings the Wacto sender uses in tests. Fake values only.</summary>
    public WhatsAppOptions WactoOptions { get; private set; } = DefaultWactoOptions();

    /// <summary>Reminder settings the API reads; tests may change them, <see cref="ResetAsync"/> restores them.</summary>
    public ReminderOptions Reminders { get; } = DefaultReminders();

    /// <summary>This run's API log files (Serilog), so tests can check what was logged.</summary>
    public string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "amc-tests", Guid.NewGuid().ToString("N"));

    public const string WactoToken = "test-token-not-real-0123456789";

    public static WhatsAppOptions DefaultWactoOptions() => new()
    {
        Provider = WhatsAppProviders.Wacto,
        BaseUrl = "https://wacto.invalid",
        ApiVersion = "v99.0",
        PhoneNumberId = "pnid-123",
        Token = WactoToken,
    };

    private static ReminderOptions DefaultReminders() => new() { Enabled = true, TestPhoneOnly = false };

    private static void Restore(ReminderOptions o)
    {
        var d = DefaultReminders();
        o.Enabled = d.Enabled;
        o.TestPhoneOnly = d.TestPhoneOnly;
        o.TestPhone = d.TestPhone;
        o.TestMode = d.TestMode;
        o.UsedDeprecatedTestMode = d.UsedDeprecatedTestMode;
    }

    /// <summary>Use different Wacto settings (e.g. another auth header) for the rest of this test.</summary>
    public void ConfigureWacto(Action<WhatsAppOptions> change)
    {
        var o = DefaultWactoOptions();
        change(o);
        WactoOptions = o;
    }
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
        Environment.SetEnvironmentVariable("WhatsApp__Provider", "Fake");
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", Path.Combine(LogDirectory, "log-.txt"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IWhatsAppSender>();
            s.AddSingleton<IWhatsAppSender>(new SwitchingSender(this));
            s.RemoveAll<IOptions<ReminderOptions>>();
            s.AddSingleton<IOptions<ReminderOptions>>(Options.Create(Reminders));
            // The daily 09:00 job never runs in tests: they call the run endpoint instead.
            var job = s.Single(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(ReminderBackgroundService));
            s.Remove(job);
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
        Wacto.Reset();
        UseWacto = false;
        WactoOptions = DefaultWactoOptions();
        Restore(Reminders);
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

/// <summary>Sends through the fake, or through the real Wacto sender pointed at <see cref="WactoStub"/>.</summary>
internal sealed class SwitchingSender(ApiFixture api) : IWhatsAppSender
{
    public Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        if (!api.UseWacto) return api.Whatsapp.SendTemplateAsync(phone, templateName, parameters, ct);
        var http = new HttpClient(api.Wacto, disposeHandler: false) { Timeout = WhatsAppCloudSender.Timeout };
        return new WhatsAppCloudSender(http, Options.Create(api.WactoOptions))
            .SendTemplateAsync(phone, templateName, parameters, ct);
    }
}

/// <summary>One request the Wacto stub received.</summary>
public record WactoRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>
/// Fake HttpMessageHandler for the Wacto endpoint: records each request and answers with <see cref="Respond"/>
/// (default: 200 with a Meta-style message id). Never touches the network.
/// </summary>
public sealed class WactoStub : HttpMessageHandler
{
    private readonly ConcurrentQueue<WactoRequest> _requests = new();
    public IReadOnlyList<WactoRequest> Requests => [.. _requests];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = Ok;
    /// <summary>Wait this long (honouring cancellation) before answering, to test timeouts.</summary>
    public TimeSpan? Delay { get; set; }

    public static HttpResponseMessage Ok(HttpRequestMessage _) => Reply(200, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.TEST1"}]}""");

    public static HttpResponseMessage Reply(int status, string body, string mediaType = "application/json") =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    public void Reset()
    {
        _requests.Clear();
        Respond = Ok;
        Delay = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        _requests.Enqueue(new WactoRequest(request.Method, request.RequestUri!, headers, body));
        if (Delay is { } delay) await Task.Delay(delay, ct);
        return Respond(request);
    }
}
