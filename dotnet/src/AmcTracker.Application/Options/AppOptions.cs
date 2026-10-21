namespace AmcTracker.Application.Options;

public class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>HMAC signing key, at least 32 characters. Set via environment variable in production.</summary>
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "amc-tracker";
    public string Audience { get; set; } = "amc-tracker-app";
    public int ExpireHours { get; set; } = 8;
}

/// <summary>Reminder rules from configuration (section "Reminders").</summary>
public class ReminderOptions
{
    public const string Section = "Reminders";

    /// <summary>The approved WhatsApp template. Body parameters: customer name, project name, end date.</summary>
    public string TemplateName { get; set; } = "amc_renewal_reminder";
    /// <summary>
    /// Test mode: every message goes to <see cref="TestPhone"/>, never to a customer; with no TestPhone nothing
    /// is sent at all. Always on in the Development environment.
    /// </summary>
    public bool TestMode { get; set; }
    /// <summary>E.164 number that receives every message in test mode.</summary>
    public string? TestPhone { get; set; }
}

public class AuthOptions
{
    public const string Section = "Auth";

    public int RefreshTokenDays { get; set; } = 90;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
