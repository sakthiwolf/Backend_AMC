using AmcTracker.Application.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AmcTracker.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<AuthService>();
        services.AddScoped<AuthService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<PlanService>();
        services.AddScoped<ReminderService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();
        return services;
    }
}
