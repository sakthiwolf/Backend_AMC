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
    /// Development: reminders run in test mode (only Reminders:TestPhone is ever messaged) and WhatsApp uses
    /// the in-memory fake unless WhatsApp:UseFake is set to false.
    /// </param>
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
        if (isDevelopment) services.PostConfigure<ReminderOptions>(o => o.TestMode = true);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<Application.Abstractions.IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IReportFileBuilder, ExcelReportBuilder>();
        services.AddSingleton<IReportFileBuilder, PdfReportBuilder>();

        var whatsApp = config.GetSection(WhatsAppOptions.Section).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        if (whatsApp.UseFake ?? isDevelopment)
        {
            services.AddSingleton<FakeWhatsAppSender>();
            services.AddSingleton<IWhatsAppSender>(sp => sp.GetRequiredService<FakeWhatsAppSender>());
        }
        else
        {
            services.AddHttpClient<IWhatsAppSender, WhatsAppCloudSender>(http =>
            {
                http.BaseAddress = new Uri(whatsApp.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(30);
            });
        }

        services.AddHostedService<ReminderBackgroundService>();
        return services;
    }
}
