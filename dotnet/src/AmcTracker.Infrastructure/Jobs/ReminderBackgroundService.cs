using AmcTracker.Application.Services;
using AmcTracker.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AmcTracker.Infrastructure.Jobs;

public class ReminderJobOptions
{
    public const string Section = "Reminders";

    public bool Enabled { get; set; } = true;
    /// <summary>Local time of day (in the Clock time zone) for the daily run, HH:mm.</summary>
    public string RunAt { get; set; } = "09:00";
}

/// <summary>Runs <see cref="ReminderService"/> once a day at 09:00 Asia/Kolkata (configurable).</summary>
public class ReminderBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<ReminderJobOptions> jobOptions,
    IOptions<ClockOptions> clockOptions,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = jobOptions.Value;
        if (!opt.Enabled)
        {
            logger.LogInformation("Reminder job is disabled (Reminders:Enabled=false).");
            return;
        }
        var tz = TimeZones.Resolve(clockOptions.Value.TimeZone);
        var runAt = TimeOnly.ParseExact(opt.RunAt, "HH:mm");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextRun(DateTime.UtcNow, tz, runAt) - DateTime.UtcNow;
            logger.LogInformation("Next reminder run in {Delay}.", delay);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReminderService>().RunDailyAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Swallow so the loop survives; the next day's run retries (no retry loop).
                logger.LogError(ex, "Daily reminder run failed.");
            }
        }
    }

    /// <summary>The next occurrence of <paramref name="runAt"/> in <paramref name="tz"/>, as UTC.</summary>
    public static DateTime NextRun(DateTime utcNow, TimeZoneInfo tz, TimeOnly runAt)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var target = local.Date + runAt.ToTimeSpan();
        if (target <= local) target = target.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(target, DateTimeKind.Unspecified), tz);
    }
}
