using System.Text.Json;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Options;
using AmcTracker.Infrastructure;
using AmcTracker.Infrastructure.Jobs;
using AmcTracker.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AmcTracker.IntegrationTests;

/// <summary>
/// Startup wiring of WhatsApp and reminders (AddInfrastructure, which Program calls): provider choice, the
/// missing-key check, the defaults, and the old Reminders:TestMode key. No database is opened.
/// </summary>
public class WhatsAppConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                settings.Select(s => KeyValuePair.Create(s.Key, s.Value)))
            {
                ["ConnectionStrings:Default"] = "Server=unused;Database=Unused_Test;",
            })
            .Build();

    private static ServiceProvider Build(IConfiguration config, bool development = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config, development);
        return services.BuildServiceProvider();
    }

    private static readonly (string, string?)[] FullWacto =
    [
        ("WhatsApp:Provider", "Wacto"),
        ("WhatsApp:BaseUrl", "https://wacto.invalid"),
        ("WhatsApp:ApiVersion", "v99.0"),
        ("WhatsApp:PhoneNumberId", "pnid-123"),
        ("WhatsApp:Token", ApiFixture.WactoToken),
    ];

    [Fact]
    public void Startup_fails_when_the_provider_is_Wacto_and_the_token_is_missing_naming_the_key_not_a_value()
    {
        var config = Config([.. FullWacto.Where(s => s.Item1 != "WhatsApp:Token")]);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));

        Assert.Contains("WhatsApp:Token", ex.Message);
        Assert.DoesNotContain("wacto.invalid", ex.Message);
        Assert.DoesNotContain("pnid-123", ex.Message);
        Assert.DoesNotContain("WhatsApp:BaseUrl", ex.Message); // only the missing one is named
    }

    [Fact]
    public void Only_the_phone_number_id_and_token_must_be_set_the_rest_have_defaults()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(("WhatsApp:Provider", "Wacto"))));
        Assert.Contains("WhatsApp:PhoneNumberId", ex.Message);
        Assert.Contains("WhatsApp:Token", ex.Message);
        Assert.DoesNotContain("WhatsApp:BaseUrl", ex.Message);
        Assert.DoesNotContain("WhatsApp:ApiVersion", ex.Message);

        using var sp = Build(Config(("WhatsApp:Provider", "Wacto"), ("WhatsApp:PhoneNumberId", "pnid-123"),
            ("WhatsApp:Token", ApiFixture.WactoToken)));
        Assert.IsType<WhatsAppCloudSender>(sp.GetRequiredService<IWhatsAppSender>());
    }

    [Fact]
    public void Startup_fails_when_the_phone_number_id_is_missing_naming_the_key_not_a_value()
    {
        var config = Config([.. FullWacto.Where(s => s.Item1 != "WhatsApp:PhoneNumberId")]);
        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));
        Assert.Contains("WhatsApp:PhoneNumberId", ex.Message);
        Assert.DoesNotContain(ApiFixture.WactoToken, ex.Message);
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("40", 40)]
    public void The_Wacto_http_client_uses_WhatsApp_TimeoutSeconds(string? setting, int expected)
    {
        using var sp = Build(Config(setting is null ? FullWacto : [.. FullWacto, ("WhatsApp:TimeoutSeconds", setting)]));
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IWhatsAppSender));
        Assert.Equal(TimeSpan.FromSeconds(expected), http.Timeout);
    }

    [Fact]
    public void A_timeout_out_of_range_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config([.. FullWacto, ("WhatsApp:TimeoutSeconds", "0")])));
        Assert.Contains("WhatsApp:TimeoutSeconds", ex.Message);
    }

    [Fact]
    public void The_Fake_sender_says_it_is_fake_and_the_Wacto_sender_does_not()
    {
        using (var sp = Build(Config(("WhatsApp:Provider", "Fake"))))
            Assert.True(sp.GetRequiredService<IWhatsAppSender>().IsFake);
        using (var sp = Build(Config(FullWacto)))
            Assert.False(sp.GetRequiredService<IWhatsAppSender>().IsFake);
    }

    [Fact]
    public void Startup_fails_without_a_connection_string_naming_the_key()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "", ["WhatsApp:Provider"] = "Fake",
        }).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));
        Assert.Contains("ConnectionStrings:Default", ex.Message);
    }

    [Fact]
    public void Startup_fails_without_a_JWT_secret_naming_the_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtTokenService.SigningKey(new JwtOptions { Secret = "" }));
        Assert.Contains("Jwt:Secret", ex.Message);
    }

    [Fact]
    public void A_base_url_that_is_not_https_is_refused()
    {
        var config = Config([.. FullWacto.Where(s => s.Item1 != "WhatsApp:BaseUrl"), ("WhatsApp:BaseUrl", "http://wacto.invalid")]);
        var ex = Assert.Throws<InvalidOperationException>(() => Build(config));
        Assert.Contains("WhatsApp:BaseUrl (must be an https URL)", ex.Message);
    }

    [Fact]
    public void With_every_key_set_the_Wacto_sender_is_used()
    {
        using var sp = Build(Config(FullWacto));
        Assert.IsType<WhatsAppCloudSender>(sp.GetRequiredService<IWhatsAppSender>());
    }

    [Fact]
    public void Outside_Development_an_unset_provider_means_Wacto_so_a_server_never_fakes_sends_silently()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config()));
        Assert.Contains("WhatsApp:Token", ex.Message);
    }

    [Fact]
    public void Development_defaults_to_the_fake_sender()
    {
        using var sp = Build(Config(), development: true);
        Assert.IsType<FakeWhatsAppSender>(sp.GetRequiredService<IWhatsAppSender>());
    }

    [Fact]
    public void Provider_Fake_uses_the_fake_sender_anywhere()
    {
        using var sp = Build(Config(("WhatsApp:Provider", "Fake")));
        Assert.IsType<FakeWhatsAppSender>(sp.GetRequiredService<IWhatsAppSender>());
    }

    [Fact]
    public void An_unknown_provider_fails_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(("WhatsApp:Provider", "Twilio"))));
        Assert.Contains("WhatsApp:Provider must be", ex.Message);
    }

    [Fact]
    public void Reminders_are_disabled_and_test_phone_only_by_default()
    {
        Assert.False(new ReminderJobOptions().Enabled);
        using var sp = Build(Config(("WhatsApp:Provider", "Fake")));
        var reminders = sp.GetRequiredService<IOptions<ReminderOptions>>().Value;
        Assert.Equal((false, true), (reminders.Enabled, reminders.TestPhoneOnly));
        Assert.False(sp.GetRequiredService<IOptions<ReminderJobOptions>>().Value.Enabled);
        var templates = sp.GetRequiredService<IOptions<WhatsAppTemplateOptions>>().Value;
        Assert.Equal(("amc_template", "monthly_template"), (templates.AMC, templates.Monthly));
    }

    [Fact]
    public void The_shipped_appsettings_keep_reminders_off_and_hold_no_WhatsApp_secrets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AmcTracker.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var json = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(dir.FullName, "src", "AmcTracker.Api", "appsettings.json"))).RootElement;

        Assert.False(json.GetProperty("Reminders").GetProperty("Enabled").GetBoolean());
        Assert.True(json.GetProperty("Reminders").GetProperty("TestPhoneOnly").GetBoolean());
        Assert.Equal("", json.GetProperty("Reminders").GetProperty("TestPhone").GetString());
        var whatsApp = json.GetProperty("WhatsApp");
        foreach (var key in new[] { "PhoneNumberId", "Token" })
            Assert.Equal("", whatsApp.GetProperty(key).GetString());
        Assert.Equal(("https://my.wacto.app", "v21.0"),
            (whatsApp.GetProperty("BaseUrl").GetString(), whatsApp.GetProperty("ApiVersion").GetString()));
        var templates = whatsApp.GetProperty("Templates");
        Assert.Equal(("amc_template", "monthly_template"),
            (templates.GetProperty("AMC").GetString(), templates.GetProperty("Monthly").GetString()));
        // Secrets live in machine variables (or dotnet user-secrets locally), never in the repo.
        Assert.Equal("", json.GetProperty("ConnectionStrings").GetProperty("Default").GetString());
        Assert.Equal("", json.GetProperty("Jwt").GetProperty("Secret").GetString());
        Assert.False(json.GetProperty("Reminders").TryGetProperty("TemplateName", out _));
        Assert.False(whatsApp.TryGetProperty("TemplateName", out _));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void The_old_TestMode_key_is_still_read_when_TestPhoneOnly_is_not_set(string legacy, bool expected)
    {
        using var sp = Build(Config(("WhatsApp:Provider", "Fake"), ("Reminders:TestMode", legacy)));
        var o = sp.GetRequiredService<IOptions<ReminderOptions>>().Value;
        Assert.Equal((expected, true), (o.TestPhoneOnly, o.UsedDeprecatedTestMode));
    }

    [Fact]
    public void TestPhoneOnly_wins_over_the_old_key_and_Development_always_forces_it_on()
    {
        using (var sp = Build(Config(("WhatsApp:Provider", "Fake"), ("Reminders:TestMode", "true"), ("Reminders:TestPhoneOnly", "false"))))
        {
            var o = sp.GetRequiredService<IOptions<ReminderOptions>>().Value;
            Assert.Equal((false, false), (o.TestPhoneOnly, o.UsedDeprecatedTestMode));
        }
        using (var sp = Build(Config(("Reminders:TestPhoneOnly", "false")), development: true))
            Assert.True(sp.GetRequiredService<IOptions<ReminderOptions>>().Value.TestPhoneOnly);
    }
}
