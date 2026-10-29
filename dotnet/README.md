# AMC & Monthly Plan Tracker: .NET 10 backend

Clean Architecture REST API for the Flutter app. Business rules are in `../../.claude/CLAUDE.md` (sections 4 and 5); migration history is in [MIGRATION_NOTES.md](MIGRATION_NOTES.md); deployment is in [DEPLOY.md](DEPLOY.md).

```
src/
  AmcTracker.Domain          entities, enums, PlanDateCalculator, PlanStatusCalculator, PhoneNumber (no framework references)
  AmcTracker.Application     services, repository/exporter/sender interfaces, DTOs, FluentValidation validators
  AmcTracker.Infrastructure  EF Core (SQL Server), repositories, migrations, JWT, password hasher, WhatsApp, ClosedXML, QuestPDF, reminder BackgroundService
  AmcTracker.Api             controllers, JWT Bearer, Swagger, error handling, Serilog, Program.cs
tests/
  AmcTracker.UnitTests         Domain + Application (xUnit, NSubstitute)
  AmcTracker.IntegrationTests  real API against SQL Server (WebApplicationFactory)
```

Requires the **.NET 10 SDK**.

## Commands (run from `backend/dotnet`)

```bash
dotnet build
dotnet test                                   # unit + integration (integration needs local SQL Server, see below)
dotnet test tests/AmcTracker.UnitTests        # no database needed

# run locally (Development: local SQL Express, port 8000, Swagger at /swagger)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AmcTracker.Api --no-launch-profile

# database (EF Core migrations)
dotnet ef migrations add <Name> -p src/AmcTracker.Infrastructure -s src/AmcTracker.Api -o Persistence/Migrations
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update -p src/AmcTracker.Infrastructure -s src/AmcTracker.Api

# create the staff login (password from AMC_NEW_PASSWORD, or prompted and hidden)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AmcTracker.Api --no-launch-profile -- create-user admin "Your Name"
```

Schema changes go through EF migrations only. Never edit tables by hand in production.

## Configuration

`appsettings.json` and `appsettings.Development.json` hold no secrets: `ConnectionStrings:Default` and `Jwt:Secret` are empty there. Outside Development the API **refuses to start** if either is empty, and the error names the key (never a value). Machine-specific overrides go in `appsettings.*.local.json` or `.env` (both git-ignored). In production, set **environment variables** (double underscore = nesting):

| Variable | Required | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | yes | SQL Server connection string |
| `Jwt__Secret` | yes | HMAC signing key, 32+ random characters |
| `Jwt__ExpireHours` | no (8) | access-token lifetime |
| `Jwt__Issuer`, `Jwt__Audience` | no | default `amc-tracker` / `amc-tracker-app` |
| `Auth__RefreshTokenDays` | no (90) | sliding refresh-token life |
| `Auth__MaxFailedLogins`, `Auth__LockoutMinutes` | no (5, 15) | login lockout |
| `WhatsApp__Provider` | no (`Fake` in Development, `Wacto` elsewhere) | `Wacto` sends for real; `Fake` records sends in memory |
| `WhatsApp__BaseUrl` | no (`https://my.wacto.app`) | provider host, https only |
| `WhatsApp__ApiVersion` | no (`v21.0`) | path segment before the phone number id |
| `WhatsApp__PhoneNumberId` | with Wacto | sender phone-number id; machine variable only |
| `WhatsApp__Token` | with Wacto | **secret**: access token; machine variable only, never in a file in the repo |
| `WhatsApp__AuthHeaderName`, `WhatsApp__AuthScheme` | no (`Authorization`, `Bearer`) | how the token is sent; an empty scheme sends the token as-is |
| `WhatsApp__Templates__AMC`, `WhatsApp__Templates__Monthly` | no (`amc_template`, `monthly_template`) | approved template per plan type (body: customer name, project name or "your project", end date "12 Sep 2027") |
| `WhatsApp__TemplateLanguage` | no (`en`) | template language |
| `WhatsApp__TimeoutSeconds` | no (15) | per-send timeout (1 to 120); a timeout is retried the next day |
| `Reminders__Enabled`, `Reminders__RunAt` | no (**false**, `09:00`) | daily job, local time in `Clock__TimeZone`; nothing is sent until Enabled is true |
| `Reminders__TestPhoneOnly`, `Reminders__TestPhone` | no (**true**; always true in Development) | every reminder goes to `TestPhone` (E.164), never to customers; with no `TestPhone` nothing is sent. `Reminders__TestMode` is the old name, read for one release |
| `Reminders__AdminUsernames__0`, `__1` ... | for `POST /api/reminders/run` and `/test-send` | usernames allowed to trigger a run or a test send (403 for everyone else) |
| `Clock__TimeZone` | no | default `Asia/Kolkata` |
| `Database__MigrateOnStartup` | no (false) | apply EF migrations at startup |
| `Swagger__Enabled` | no (false) | expose `/swagger` |
| `Kestrel__Endpoints__Http__Url` | no | default `http://127.0.0.1:5000` |

With `WhatsApp__Provider=Wacto` (the default outside Development) the API **refuses to start** if `PhoneNumberId` or `Token` is missing; the error names the missing keys. Set `WhatsApp__Provider=Fake` on a server that should not send at all.

Confirmed with Wacto on 2026-10-26 (a real Monthly test send and reminder run to the test phone): `Authorization: Bearer <key>` works and a success returns the Meta shape with `messages[0].id` (a `wamid...` id). Error bodies are read as Meta's `error.code`/`error.message`; anything else is stored as text.

### Local development secrets (dotnet user-secrets)

The two secrets come from `dotnet user-secrets` on the API project (stored in your user profile, outside the repo). Run once from `backend/dotnet`, with your own values:

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost\SQLEXPRESS;Database=AMCTracker_DotnetDev;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False" --project src/AmcTracker.Api
dotnet user-secrets set "Jwt:Secret" "<32+ random characters>" --project src/AmcTracker.Api
```

User secrets are read only in the Development environment. `dotnet user-secrets list --project src/AmcTracker.Api` shows what is set.

Logs: Serilog rolling daily files in `logs/amc-api-YYYYMMDD.log` (30 days kept, 50 MB cap per file). Reminder logs carry plan and reminder ids only; phone numbers, message text and tokens are never logged.

Before the first real send: keep `Reminders__TestPhoneOnly=true`, set `Reminders__TestPhone` to your own number and `Reminders__AdminUsernames__0` to your username, then call `POST /api/reminders/test-send` with body `{"planType":"AMC"}` and then `{"planType":"Monthly"}` (409 `provider_not_configured` while the provider is Fake). Only after that works, set `Reminders__Enabled=true`; customers are messaged only once you also set `Reminders__TestPhoneOnly=false`.

## Integration tests

They run against a real SQL Server using `ConnectionStrings:Test` from `tests/AmcTracker.IntegrationTests/appsettings.json` (default: local `SQLEXPRESS`, database `AMCTracker_Test`), overridable with the environment variable `ConnectionStrings__Test`. **The database is dropped and recreated on every run**, so it must be exactly `AMCTracker_Test`; the fixture refuses any other name.
