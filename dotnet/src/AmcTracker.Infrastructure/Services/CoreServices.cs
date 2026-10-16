using System.Security.Cryptography;
using System.Text;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Options;
using AmcTracker.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AmcTracker.Infrastructure.Services;

public class ClockOptions
{
    public const string Section = "Clock";
    public string TimeZone { get; set; } = "Asia/Kolkata";
}

public static class TimeZones
{
    /// <summary>Resolves an IANA id on Linux/Windows (ICU), falling back to the Windows id for IST.</summary>
    public static TimeZoneInfo Resolve(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) when (id == "Asia/Kolkata")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }
}

public class SystemClock(IOptions<ClockOptions> options) : IClock
{
    private readonly TimeZoneInfo _tz = TimeZones.Resolve(options.Value.TimeZone);

    public DateTime UtcNow => DateTime.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz));
}

/// <summary>Wraps ASP.NET Core's PasswordHasher&lt;T&gt; (PBKDF2 with a per-hash salt).</summary>
public class IdentityPasswordHasher : Application.Abstractions.IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;
}

public class JwtTokenService : IJwtTokenService
{
    public const string AccessType = "access";
    private readonly JwtOptions _opt;
    private readonly SigningCredentials _credentials;
    private readonly IClock _clock;

    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _opt = options.Value;
        _clock = clock;
        _credentials = new SigningCredentials(SigningKey(_opt), SecurityAlgorithms.HmacSha256);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.Secret) || opt.Secret.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Secret must be set (environment variable Jwt__Secret) and at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.Secret));
    }

    public AccessToken CreateAccessToken(User user)
    {
        var now = _clock.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opt.Issuer,
            Audience = _opt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(_opt.ExpireHours),
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.UserId.ToString(),
                ["typ"] = AccessType,
            },
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), _opt.ExpireHours * 3600);
    }

    /// <summary>A random 48-byte token, URL-safe base64 encoded.</summary>
    public string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));

    /// <summary>SHA-256 hex digest. Refresh tokens are high-entropy, so no salt is needed.</summary>
    public string HashRefreshToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool RefreshTokenMatches(string token, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashRefreshToken(token)), Encoding.ASCII.GetBytes(storedHash));
}
