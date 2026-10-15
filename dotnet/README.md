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
| `WhatsApp__Token` | for reminders | Cloud API access token |
| `WhatsApp__PhoneNumberId` | for reminders | sender phone-number id |
| `WhatsApp__TemplateName`, `WhatsApp__LanguageCode` | no | approved template (body: customer name, project name, end date) |
| `WhatsApp__TestRecipient` | no | **testing**: send every reminder to this E.164 number instead of customers |
| `Reminders__Enabled`, `Reminders__RunAt` | no (true, `09:00`) | daily job, local time in `Clock__TimeZone` |
| `Clock__TimeZone` | no | default `Asia/Kolkata` |
| `Database__MigrateOnStartup` | no (false) | apply EF migrations at startup |
| `Swagger__Enabled` | no (false) | expose `/swagger` |
| `Kestrel__Endpoints__Http__Url` | no | default `http://127.0.0.1:5000` |

Logs: Serilog rolling daily files in `logs/amc-api-YYYYMMDD.log` (30 days kept, 50 MB cap per file). Phone numbers are masked in logs and tokens are never logged.

Before the first real send, set `WhatsApp__TestRecipient` to your own number and call `POST /api/reminders/run`.

## Integration tests

They run against a real SQL Server using `ConnectionStrings:Test` from `tests/AmcTracker.IntegrationTests/appsettings.json` (default: local `SQLEXPRESS`, database `AMCTracker_Test`), overridable with the environment variable `ConnectionStrings__Test`. **The database is dropped and recreated on every run**, so its name must contain `Test`; the fixture refuses anything else.
