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

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
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
        services.AddScoped<IUserRepository, UserRepository>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.Configure<ClockOptions>(config.GetSection(ClockOptions.Section));
        services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.Section));
        services.Configure<ReminderJobOptions>(config.GetSection(ReminderJobOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<Application.Abstractions.IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IExcelExporter, ClosedXmlExcelExporter>();
        services.AddSingleton<IPdfExporter, QuestPdfExporter>();

        services.AddHttpClient<IWhatsAppSender, WhatsAppSender>((sp, http) =>
        {
            var opt = config.GetSection(WhatsAppOptions.Section).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
            http.BaseAddress = new Uri(opt.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHostedService<ReminderBackgroundService>();
        return services;
    }
}
