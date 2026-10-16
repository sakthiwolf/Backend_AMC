using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Options;
using AmcTracker.Domain.Entities;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace AmcTracker.Application.Services;

/// <summary>Login, refresh-token rotation and logout. Rules ported from the FastAPI services/auth.py.</summary>
public class AuthService(
    IUserRepository users,
    IUnitOfWork uow,
    IPasswordHasher hasher,
    IJwtTokenService tokens,
    IClock clock,
    IOptions<AuthOptions> options,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshRequest> refreshValidator,
    IValidator<LogoutRequest> logoutValidator)
{
    private readonly AuthOptions _opt = options.Value;

    // Verified against when the username is unknown, so timing does not reveal which usernames exist.
    private readonly string _dummyHash = hasher.Hash("not-a-real-password");

    private static AppException InvalidCredentials() =>
        new(401, "invalid_credentials", "Wrong username or password.");

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        await loginValidator.ValidateAndThrowAsync(req, ct);
        var now = clock.UtcNow;
        var user = await users.GetByUsernameAsync(req.Username!, ct);

        if (user is null || !user.IsActive)
        {
            hasher.Verify(_dummyHash, req.Password!);
            throw InvalidCredentials();
        }

        if (user.LockedUntil is not null)
        {
            if (user.LockedUntil > now)
                throw new AppException(423, "account_locked",
                    $"Too many failed attempts. Try again in {_opt.LockoutMinutes} minutes.");
            user.LockedUntil = null; // the lock has expired: count afresh
            user.FailedLoginCount = 0;
        }

        if (!hasher.Verify(user.PasswordHash, req.Password!))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _opt.MaxFailedLogins)
            {
                user.LockedUntil = now.AddMinutes(_opt.LockoutMinutes);
                user.FailedLoginCount = 0;
            }
            await uow.SaveChangesAsync(ct);
            throw InvalidCredentials();
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var device = await users.GetDeviceAsync(req.DeviceFingerprint!, ct);
        if (device is not null && device.UserId != user.UserId)
            throw new AppException(409, "device_in_use", "This device is registered to a different user.");
        if (device is null)
        {
            device = new UserDevice
            {
                User = user,
                DeviceFingerprint = req.DeviceFingerprint!,
                RefreshTokenHash = "",
                RefreshExpiresAt = now,
                CreatedAt = now,
            };
            users.AddDevice(device);
        }
        if (!string.IsNullOrEmpty(req.DeviceLabel)) device.DeviceLabel = req.DeviceLabel;
        return await IssueAsync(user, device, ct);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest req, CancellationToken ct = default)
    {
        await refreshValidator.ValidateAndThrowAsync(req, ct);
        var invalid = new AppException(401, "invalid_refresh", "Please sign in again.");
        var device = await users.GetDeviceAsync(req.DeviceFingerprint!, ct);
        if (device?.User is null || device.RevokedAt is not null) throw invalid;

        if (!tokens.RefreshTokenMatches(req.RefreshToken!, device.RefreshTokenHash))
        {
            // An old token was replayed: assume it leaked and revoke this device.
            device.RevokedAt = clock.UtcNow;
            await uow.SaveChangesAsync(ct);
            throw invalid;
        }
        if (device.RefreshExpiresAt <= clock.UtcNow || !device.User.IsActive) throw invalid;
        return await IssueAsync(device.User, device, ct);
    }

    /// <summary>Revokes the user's device. Idempotent: an unknown device is not an error.</summary>
    public async Task LogoutAsync(int userId, LogoutRequest req, CancellationToken ct = default)
    {
        await logoutValidator.ValidateAndThrowAsync(req, ct);
        var device = await users.GetDeviceAsync(req.DeviceFingerprint!, ct);
        if (device is not null && device.UserId == userId && device.RevokedAt is null)
        {
            device.RevokedAt = clock.UtcNow;
            await uow.SaveChangesAsync(ct);
        }
    }

    public async Task<UserDto> GetMeAsync(int userId, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(userId, ct) ?? throw AppException.NotFound("User");
        return ToDto(user);
    }

    public static UserDto ToDto(User u) => new(u.UserId, u.Username, u.FullName ?? u.Username);

    /// <summary>Rotates the device's refresh token (sliding expiry) and mints an access token.</summary>
    private async Task<TokenResponse> IssueAsync(User user, UserDevice device, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var refresh = tokens.GenerateRefreshToken();
        device.RefreshTokenHash = tokens.HashRefreshToken(refresh);
        device.RefreshExpiresAt = now.AddDays(_opt.RefreshTokenDays);
        device.LastUsedAt = now;
        device.RevokedAt = null;
        await uow.SaveChangesAsync(ct);
        var access = tokens.CreateAccessToken(user);
        return new TokenResponse(access.Token, "bearer", access.ExpiresInSeconds, refresh, ToDto(user));
    }
}
