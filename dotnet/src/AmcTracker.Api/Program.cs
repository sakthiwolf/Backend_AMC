using System.Text.Json.Serialization;
using AmcTracker.Api.Infrastructure;
using AmcTracker.Application;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Options;
using AmcTracker.Infrastructure;
using AmcTracker.Infrastructure.Persistence;
using AmcTracker.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ApiErrors.FromModelState);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.SigningKey(jwt),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                // Same checks as the FastAPI version: access-type token for a user that still exists.
                var type = ctx.Principal?.FindFirst("typ")?.Value;
                var sub = ctx.Principal?.FindFirst("sub")?.Value;
                if (type != JwtTokenService.AccessType || !int.TryParse(sub, out var userId))
                {
                    ctx.Fail("wrong token type");
                    return;
                }
                var users = ctx.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                var user = await users.GetByIdAsync(userId, ctx.HttpContext.RequestAborted);
                if (user is null || !user.IsActive) ctx.Fail("unknown user");
            },
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                var (code, message) = ctx.AuthenticateFailure switch
                {
                    null => ("not_authenticated", "Sign in to continue."),
                    SecurityTokenExpiredException => ("token_expired", "Your session has expired."),
                    _ => ("invalid_token", "Invalid session token."),
                };
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return ApiErrors.Write(ctx.HttpContext, StatusCodes.Status401Unauthorized, code, message);
            },
            OnForbidden = ctx => ApiErrors.Write(
                ctx.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Only an administrator can do this."),
        };
    });

// Every endpoint needs a valid token unless marked [AllowAnonymous].
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminHandler>();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build())
    .AddPolicy(AdminRequirement.Policy, p => p.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()));

// Development only: lets the Flutter web build (served from localhost on a random port) call the API.
builder.Services.AddCors(o => o.AddPolicy("DevWeb", p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (u.Host == "localhost" || u.Host == "127.0.0.1"))
    .AllowAnyHeader().AllowAnyMethod()));

// Production: browser origins listed in Cors:AllowedOrigins (e.g. a Flutter web build). The Android app is
// not a browser and does not send CORS preflights, so an empty list simply means "no browser origins".
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("Configured", p =>
{
    if (corsOrigins.Length > 0)
        p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "AMC & Monthly Plan Tracker API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste the accessToken from POST /api/auth/login.",
    });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
    });
});

var app = builder.Build();

// Reminders:TestMode was renamed Reminders:TestPhoneOnly; the old key is read for one release.
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AmcTracker.Application.Options.ReminderOptions>>()
        .Value.UsedDeprecatedTestMode)
    app.Logger.LogWarning("Reminders:TestMode is deprecated and will be removed; use Reminders:TestPhoneOnly.");

// CLI mode: `create-user <username> [full name]` instead of starting the server.
if (args.Length > 0 && args[0] == "create-user")
{
    Environment.ExitCode = await CreateUserCommand.RunAsync(app, args[1..]);
    return;
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseSerilogRequestLogging(); // outside the handler so it logs the final status, not the raw exception
app.UseExceptionHandler();
app.UseStatusCodePages(ctx =>
{
    var status = ctx.HttpContext.Response.StatusCode;
    var (code, message) = status switch
    {
        404 => ("not_found", "Resource not found."),
        405 => ("method_not_allowed", "Method not allowed."),
        415 => ("unsupported_media_type", "Send the body as application/json."),
        _ => ("http_error", $"Request failed with status {status}."),
    };
    return ApiErrors.Write(ctx.HttpContext, status, code, message);
});

app.UseCors(app.Environment.IsDevelopment() ? "DevWeb" : "Configured");
if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    // Relative URL so it also works when the app sits in an IIS virtual directory.
    app.UseSwaggerUI(o => o.SwaggerEndpoint("v1/swagger.json", "AMC Tracker API v1"));
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

await SeedUser.RunAsync(app);

// Warm-up: build the EF model, open the SQL connection pool and JIT the password hasher once at
// start, so the first login after an IIS app-pool start or recycle is not several seconds slower.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.AsNoTracking().AnyAsync();
        await db.Customers.AsNoTracking().Take(1).ToListAsync();
        await db.UserDevices.AsNoTracking().Take(1).ToListAsync();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        hasher.Verify(hasher.Hash("warm-up"), "warm-up");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Start-up warm-up failed; the first request will be slower.");
    }
}));

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>Marker so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
