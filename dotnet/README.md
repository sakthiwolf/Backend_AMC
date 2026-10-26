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

`appsettings.json` holds placeholders only. `appsettings.Development.json` points at local SQL Server Express (`AMCTracker_DotnetDev`) with a clearly-labelled dev-only JWT key. Machine-specific overrides go in `appsettings.*.local.json` or `.env` (both git-ignored). In production, set **environment variables** (double underscore = nesting):

| Variable | Required | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | yes | SQL Server connection string |
| `Jwt__Secret` | yes | HMAC signing key, 32+ random characters |
| `Jwt__ExpireHours` | no (8) | access-token lifetime |
| `Jwt__Issuer`, `Jwt__Audience` | no | default `amc-tracker` / `amc-tracker-app` |
| `Auth__RefreshTokenDays` | no (90) | sliding refresh-token life |
| `Auth__MaxFailedLogins`, `Auth__LockoutMinutes` | no (5, 15) | login lockout |
| `WhatsApp__Provider` | no (`Fake` in Development, `Wacto` elsewhere) | `Wacto` sends for real; `Fake` records sends in memory |
| `WhatsApp__BaseUrl` | with Wacto | provider host, https only (from the Wacto console) |
| `WhatsApp__ApiVersion` | with Wacto | path segment before the phone number id, e.g. `v21.0` |
| `WhatsApp__PhoneNumberId` | with Wacto | sender phone-number id |
| `WhatsApp__Token` | with Wacto | **secret**: access token; machine variable only, never in a file in the repo |
| `WhatsApp__AuthHeaderName`, `WhatsApp__AuthScheme` | no (`Authorization`, `Bearer`) | how the token is sent; an empty scheme sends the token as-is |
| `WhatsApp__Templates__AMC`, `WhatsApp__Templates__Monthly` | no (`amc_yearly_reminder`, `amc_monthly_reminder`) | approved template per plan type (body: customer name, project name, end date "12 Sep 2027") |
| `WhatsApp__TemplateLanguage` | no (`en`) | template language |
| `Reminders__Enabled`, `Reminders__RunAt` | no (**false**, `09:00`) | daily job, local time in `Clock__TimeZone`; nothing is sent until Enabled is true |
| `Reminders__TestPhoneOnly`, `Reminders__TestPhone` | no (**true**; always true in Development) | every reminder goes to `TestPhone` (E.164), never to customers; with no `TestPhone` nothing is sent. `Reminders__TestMode` is the old name, read for one release |
| `Reminders__AdminUsernames__0`, `__1` ... | for `POST /api/reminders/run` and `/test-send` | usernames allowed to trigger a run or a test send (403 for everyone else) |

With `WhatsApp__Provider=Wacto` (the default outside Development) the API **refuses to start** if `BaseUrl`, `ApiVersion`, `PhoneNumberId` or `Token` is missing; the error names the missing keys. Set `WhatsApp__Provider=Fake` on a server that should not send at all.
| `Clock__TimeZone` | no | default `Asia/Kolkata` |
| `Database__MigrateOnStartup` | no (false) | apply EF migrations at startup |
| `Swagger__Enabled` | no (false) | expose `/swagger` |
| `Kestrel__Endpoints__Http__Url` | no | default `http://127.0.0.1:5000` |

Logs: Serilog rolling daily files in `logs/amc-api-YYYYMMDD.log` (30 days kept, 50 MB cap per file). Reminder logs carry plan and reminder ids only; phone numbers, message text and tokens are never logged.

Before the first real send: keep `Reminders__TestPhoneOnly=true`, set `Reminders__TestPhone` to your own number and `Reminders__AdminUsernames__0` to your username, then call `POST /api/reminders/test-send` (and `?planType=Monthly`). Only after that works, set `Reminders__Enabled=true`; customers are messaged only once you also set `Reminders__TestPhoneOnly=false`.

## Integration tests

They run against a real SQL Server using `ConnectionStrings:Test` from `tests/AmcTracker.IntegrationTests/appsettings.json` (default: local `SQLEXPRESS`, database `AMCTracker_Test`), overridable with the environment variable `ConnectionStrings__Test`. **The database is dropped and recreated on every run**, so its name must contain `Test`; the fixture refuses anything else.
