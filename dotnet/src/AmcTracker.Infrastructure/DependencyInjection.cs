using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Options;
using AmcTracker.Infrastructure.Jobs;
using AmcTracker.Infrastructure.Persistence;
using AmcTracker.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AmcTracker.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "Default";

    /// <param name="isDevelopment">
    /// Development: reminders only ever go to Reminders:TestPhone, and WhatsApp:Provider defaults to "Fake".
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// WhatsApp:Provider is "Wacto" and a required WhatsApp key is empty (the message names the key, never a
    /// value), or the provider name is unknown.
    /// </exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config, bool isDevelopment = false)
    {
        var connection = config.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "ConnectionStrings:Default is not set (environment variable ConnectionStrings__Default).");

        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connection));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<IReminderRepository, ReminderRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IUserRepository, UserRepository>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.Configure<ClockOptions>(config.GetSection(ClockOptions.Section));
        services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.Section));
        services.Configure<ReminderJobOptions>(config.GetSection(ReminderJobOptions.Section));
        services.Configure<ReminderOptions>(config.GetSection(ReminderOptions.Section));
        services.Configure<WhatsAppTemplateOptions>(config.GetSection(WhatsAppTemplateOptions.Section));
        // Reminders:TestMode is the old name of Reminders:TestPhoneOnly: read it for one release.
        var reminders = config.GetSection(ReminderOptions.Section);
        var legacyTestMode = reminders.GetValue<bool?>("TestMode");
        var useLegacy = reminders["TestPhoneOnly"] is null && legacyTestMode is not null;
        services.PostConfigure<ReminderOptions>(o =>
        {
            if (useLegacy)
            {
                o.TestPhoneOnly = legacyTestMode!.Value;
                o.UsedDeprecatedTestMode = true;
            }
            if (isDevelopment) o.TestPhoneOnly = true;
        });

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<Application.Abstractions.IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IReportFileBuilder, ExcelReportBuilder>();
        services.AddSingleton<IReportFileBuilder, PdfReportBuilder>();

        var whatsApp = config.GetSection(WhatsAppOptions.Section).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        var provider = string.IsNullOrWhiteSpace(whatsApp.Provider)
            ? (isDevelopment ? WhatsAppProviders.Fake : WhatsAppProviders.Wacto)
            : whatsApp.Provider.Trim();
        if (provider.Equals(WhatsAppProviders.Fake, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<FakeWhatsAppSender>();
            services.AddSingleton<IWhatsAppSender>(sp => sp.GetRequiredService<FakeWhatsAppSender>());
        }
        else if (provider.Equals(WhatsAppProviders.Wacto, StringComparison.OrdinalIgnoreCase))
        {
            var missing = whatsApp.MissingKeys();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"WhatsApp:Provider is \"{WhatsAppProviders.Wacto}\" but these settings are missing: " +
                    $"{string.Join(", ", missing)}. Set them as machine variables (e.g. WhatsApp__Token), " +
                    "or set WhatsApp__Provider=Fake.");
            services.AddHttpClient<IWhatsAppSender, WhatsAppCloudSender>(http =>
                {
                    http.Timeout = WhatsAppCloudSender.Timeout;
                })
                // The default HttpClient logs print request URLs; keep provider details out of the logs.
                .RemoveAllLoggers();
        }
        else
        {
            throw new InvalidOperationException(
                $"WhatsApp:Provider must be \"{WhatsAppProviders.Wacto}\" or \"{WhatsAppProviders.Fake}\".");
        }

        services.AddHostedService<ReminderBackgroundService>();
        return services;
    }
}
