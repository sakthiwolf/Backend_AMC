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

public class AuthOptions
{
    public const string Section = "Auth";

    public int RefreshTokenDays { get; set; } = 90;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
