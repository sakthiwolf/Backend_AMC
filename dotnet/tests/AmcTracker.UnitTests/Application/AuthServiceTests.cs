using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Options;
using AmcTracker.Application.Services;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Entities;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class AuthServiceTests
{
    private const string Fp = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTime Now = new(2026, 10, 15, 4, 0, 0, DateTimeKind.Utc);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IJwtTokenService _tokens = Substitute.For<IJwtTokenService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly User _alice = new() { UserId = 1, Username = "alice", PasswordHash = "hash", IsActive = true };
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Hash(Arg.Any<string>()).Returns("dummy");
        _hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1) == "right-password");
        _tokens.CreateAccessToken(Arg.Any<User>()).Returns(new AccessToken("jwt", 8 * 3600));
        _tokens.GenerateRefreshToken().Returns("new-refresh");
        _tokens.HashRefreshToken(Arg.Any<string>()).Returns(ci => "h:" + ci.ArgAt<string>(0));
        _tokens.RefreshTokenMatches(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => "h:" + ci.ArgAt<string>(0) == ci.ArgAt<string>(1));
        _users.GetByUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(_alice);

        _auth = new AuthService(_users, Substitute.For<IUnitOfWork>(), _hasher, _tokens, _clock,
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            new LoginRequestValidator(), new RefreshRequestValidator(), new LogoutRequestValidator());
    }

    private static LoginRequest Login(string user = "alice", string pw = "right-password") =>
        new(user, pw, Fp, "Pixel");

    [Fact]
    public async Task Successful_login_returns_8_hour_token_and_refresh_token()
    {
        var r = await _auth.LoginAsync(Login());
        Assert.Equal(8 * 3600, r.ExpiresIn);
        Assert.Equal("bearer", r.TokenType);
        Assert.Equal("new-refresh", r.RefreshToken);
        Assert.Equal("alice", r.User.FullName); // falls back to username
        _users.Received(1).AddDevice(Arg.Is<UserDevice>(d =>
            d.DeviceFingerprint == Fp && d.RefreshTokenHash == "h:new-refresh"
            && d.RefreshExpiresAt == Now.AddDays(90)));
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_give_the_same_error()
    {
        var a = await Assert.ThrowsAsync<AppException>(() => _auth.LoginAsync(Login(pw: "nope")));
        var b = await Assert.ThrowsAsync<AppException>(() => _auth.LoginAsync(Login(user: "ghost")));
        Assert.Equal((401, "invalid_credentials", a.Message), (b.StatusCode, b.Code, b.Message));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_15_minutes()
    {
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<AppException>(() => _auth.LoginAsync(Login(pw: "bad")));

        Assert.Equal(Now.AddMinutes(15), _alice.LockedUntil);
        var locked = await Assert.ThrowsAsync<AppException>(() => _auth.LoginAsync(Login()));
        Assert.Equal((423, "account_locked"), (locked.StatusCode, locked.Code));
    }

    [Fact]
    public async Task Expired_lock_allows_login_again()
    {
        _alice.LockedUntil = Now.AddSeconds(-1);
        _alice.FailedLoginCount = 3;
        await _auth.LoginAsync(Login());
        Assert.Null(_alice.LockedUntil);
        Assert.Equal(0, _alice.FailedLoginCount);
    }

    [Fact]
    public async Task Device_registered_to_another_user_is_rejected()
    {
        _users.GetDeviceAsync(Fp, Arg.Any<CancellationToken>()).Returns(new UserDevice { UserId = 99, DeviceFingerprint = Fp });
        var ex = await Assert.ThrowsAsync<AppException>(() => _auth.LoginAsync(Login()));
        Assert.Equal((409, "device_in_use"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task Replaying_an_old_refresh_token_revokes_the_device()
    {
        var device = new UserDevice
        {
            UserId = 1, User = _alice, DeviceFingerprint = Fp,
            RefreshTokenHash = "h:current", RefreshExpiresAt = Now.AddDays(10),
        };
        _users.GetDeviceAsync(Fp, Arg.Any<CancellationToken>()).Returns(device);

        var ex = await Assert.ThrowsAsync<AppException>(() => _auth.RefreshAsync(new RefreshRequest(Fp, "old")));

        Assert.Equal("invalid_refresh", ex.Code);
        Assert.Equal(Now, device.RevokedAt);
    }

    [Fact]
    public async Task Valid_refresh_rotates_the_token_and_extends_expiry()
    {
        var device = new UserDevice
        {
            UserId = 1, User = _alice, DeviceFingerprint = Fp,
            RefreshTokenHash = "h:current", RefreshExpiresAt = Now.AddDays(1),
        };
        _users.GetDeviceAsync(Fp, Arg.Any<CancellationToken>()).Returns(device);

        var r = await _auth.RefreshAsync(new RefreshRequest(Fp, "current"));

        Assert.Equal("new-refresh", r.RefreshToken);
        Assert.Equal("h:new-refresh", device.RefreshTokenHash);
        Assert.Equal(Now.AddDays(90), device.RefreshExpiresAt);
    }

    [Fact]
    public async Task Expired_or_revoked_refresh_is_rejected()
    {
        var device = new UserDevice
        {
            UserId = 1, User = _alice, DeviceFingerprint = Fp,
            RefreshTokenHash = "h:current", RefreshExpiresAt = Now.AddSeconds(-1),
        };
        _users.GetDeviceAsync(Fp, Arg.Any<CancellationToken>()).Returns(device);
        await Assert.ThrowsAsync<AppException>(() => _auth.RefreshAsync(new RefreshRequest(Fp, "current")));

        device.RefreshExpiresAt = Now.AddDays(1);
        device.RevokedAt = Now;
        await Assert.ThrowsAsync<AppException>(() => _auth.RefreshAsync(new RefreshRequest(Fp, "current")));
    }

    [Fact]
    public async Task Logout_revokes_only_the_callers_device_and_is_idempotent()
    {
        var device = new UserDevice { UserId = 1, DeviceFingerprint = Fp };
        _users.GetDeviceAsync(Fp, Arg.Any<CancellationToken>()).Returns(device);

        await _auth.LogoutAsync(2, new LogoutRequest(Fp)); // someone else's device: untouched
        Assert.Null(device.RevokedAt);

        await _auth.LogoutAsync(1, new LogoutRequest(Fp));
        Assert.Equal(Now, device.RevokedAt);
        await _auth.LogoutAsync(1, new LogoutRequest(Fp)); // no error second time

        _users.GetDeviceAsync("unknown-unknown-unknown", Arg.Any<CancellationToken>()).Returns((UserDevice?)null);
        await _auth.LogoutAsync(1, new LogoutRequest("unknown-unknown-unknown"));
    }

    [Fact]
    public async Task Short_device_fingerprint_fails_validation()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _auth.LoginAsync(new LoginRequest("alice", "right-password", "short", null)));
    }
}
