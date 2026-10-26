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
    public void Every_missing_Wacto_key_is_named()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Config(("WhatsApp:Provider", "Wacto"))));
        foreach (var key in new[] { "WhatsApp:BaseUrl", "WhatsApp:ApiVersion", "WhatsApp:PhoneNumberId", "WhatsApp:Token" })
            Assert.Contains(key, ex.Message);
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
        Assert.Equal(("amc_yearly_reminder", "amc_monthly_reminder"), (templates.AMC, templates.Monthly));
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
        var whatsApp = json.GetProperty("WhatsApp");
        foreach (var key in new[] { "BaseUrl", "PhoneNumberId", "Token" })
            Assert.Equal("", whatsApp.GetProperty(key).GetString());
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
