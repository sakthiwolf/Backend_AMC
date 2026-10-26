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

    /// <summary>Off by default: the daily run (and the manual run) does nothing until this is true.</summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// On by default: every message goes to <see cref="TestPhone"/>, never to a customer; with no TestPhone
    /// nothing is sent at all. Always on in the Development environment.
    /// </summary>
    public bool TestPhoneOnly { get; set; } = true;
    /// <summary>Deprecated name of <see cref="TestPhoneOnly"/>, read for one release when TestPhoneOnly is not set.</summary>
    public bool? TestMode { get; set; }
    /// <summary>Set at startup when the value came from the deprecated Reminders:TestMode key.</summary>
    public bool UsedDeprecatedTestMode { get; set; }
    /// <summary>E.164 number that receives every message while <see cref="TestPhoneOnly"/> is on.</summary>
    public string? TestPhone { get; set; }
}

/// <summary>The approved WhatsApp template for each plan type (section "WhatsApp:Templates").</summary>
public class WhatsAppTemplateOptions
{
    public const string Section = "WhatsApp:Templates";

    /// <summary>Body parameters: customer name, project name, EndDate ("12 Oct 2027").</summary>
    public string AMC { get; set; } = "amc_template";
    /// <summary>Same three body parameters as <see cref="AMC"/>.</summary>
    public string Monthly { get; set; } = "monthly_template";

    public string For(AmcTracker.Domain.Enums.PlanType type) =>
        type == AmcTracker.Domain.Enums.PlanType.Monthly ? Monthly : AMC;
}

public class AuthOptions
{
    public const string Section = "Auth";

    public int RefreshTokenDays { get; set; } = 90;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
