using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

internal static class Http
{
    /// <summary>
    /// Creating a customer also creates their first plan. Tests that exercise POST /customers/{id}/plans
    /// with their own type and amount remove that plan first.
    /// </summary>
    public static Task<int> DropAutoPlan(this ApiFixture api, int customerId) =>
        api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM Plans WHERE CustomerId = {0}", customerId));

    public static async Task<JsonElement> Json(this HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> ErrorCode(this HttpResponseMessage r) =>
        (await r.Json()).GetProperty("error").GetProperty("code").GetString()!;

    public static object CustomerBody(DateOnly installed, string phone = "+919876543210", string name = "Asha Traders") => new
    {
        customerName = name, phone, location = "Chennai", installationDate = installed.ToString("yyyy-MM-dd"),
        projectName = "CCTV", projectValue = 50000.00m, whatsAppOptOut = false,
    };
}

[Collection("Api")]
public class AuthTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public AuthTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Fp2 => "22222222-2222-4222-8222-222222222222";

    private Task<HttpResponseMessage> Login(HttpClient c, string user = "alice", string pw = ApiFixture.Password, string fp = ApiFixture.Fingerprint) =>
        c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pw, deviceFingerprint = fp });

    [Fact]
    public async Task Health_is_public_and_returns_ok()
    {
        var res = await _api.Anonymous().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ok", (await res.Json()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Login_returns_camelCase_tokens_and_me_works()
    {
        await _api.AddUserAsync("alice", "Alice A");
        var c = _api.Anonymous();
        var res = await Login(c);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Json();
        Assert.Equal("bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(8 * 3600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal("Alice A", body.GetProperty("user").GetProperty("fullName").GetString());
        Assert.True(body.GetProperty("refreshToken").GetString()!.Length >= 64);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        var me = await c.GetAsync("/api/auth/me");
        Assert.Equal("alice", (await me.Json()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Refresh_token_is_stored_only_as_a_hash()
    {
        await _api.AddUserAsync("alice");
        var token = (await (await Login(_api.Anonymous())).Json()).GetProperty("refreshToken").GetString()!;
        var stored = await _api.WithDbAsync(db => db.UserDevices.Select(d => d.RefreshTokenHash).SingleAsync());
        Assert.NotEqual(token, stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_look_identical()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var a = await Login(c, pw: "nope-nope-nope");
        var b = await Login(c, user: "ghost");
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        Assert.Equal((await a.Json()).ToString(), (await b.Json()).ToString());
        Assert.Equal("invalid_credentials", await (await Login(c, user: "ghost")).ErrorCode());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, pw: "wrong-wrong")).StatusCode);
        var locked = await Login(c);
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("account_locked", await locked.ErrorCode());
    }

    [Fact]
    public async Task Refresh_rotates_and_replaying_the_old_token_revokes_the_device()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var first = await (await Login(c)).Json();
        var r1 = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = first.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var second = await r1.Json();
        Assert.NotEqual(first.GetProperty("refreshToken").GetString(), second.GetProperty("refreshToken").GetString());

        var replay = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = first.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_refresh", await replay.ErrorCode());

        var newest = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = second.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, newest.StatusCode); // whole device revoked
    }

    [Fact]
    public async Task Logout_blocks_refresh_and_needs_an_access_token()
    {
        await _api.AddUserAsync("alice");
        var c = _api.Anonymous();
        var tokens = await (await Login(c)).Json();

        var anon = await c.PostAsJsonAsync("/api/auth/logout", new { deviceFingerprint = ApiFixture.Fingerprint });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var res = await c.PostAsJsonAsync("/api/auth/logout", new { deviceFingerprint = ApiFixture.Fingerprint });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        c.DefaultRequestHeaders.Authorization = null;
        var refresh = await c.PostAsJsonAsync("/api/auth/refresh",
            new { deviceFingerprint = ApiFixture.Fingerprint, refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Device_is_bound_to_one_user()
    {
        await _api.AddUserAsync("alice");
        await _api.AddUserAsync("bob");
        var c = _api.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "alice")).StatusCode);
        var clash = await Login(c, "bob");
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("device_in_use", await clash.ErrorCode());
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "bob", fp: Fp2)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not.a.jwt")]
    public async Task Protected_routes_reject_missing_or_malformed_tokens(string? token)
    {
        var c = _api.Anonymous();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await c.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var error = (await res.Json()).GetProperty("error");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(token is null ? "not_authenticated" : "invalid_token", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validation_errors_use_the_standard_shape_and_never_echo_values()
    {
        var res = await _api.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "s3cret-value" });
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("validation_error", text);
        Assert.Contains("deviceFingerprint", text);
        Assert.DoesNotContain("s3cret-value", text);
    }

    [Fact]
    public async Task Unknown_route_returns_json_error()
    {
        var res = await (await _api.SignedInAsync()).GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not_found", await res.ErrorCode());
    }
}
