# Migration notes: FastAPI to .NET 10

Source: tag `python-fastapi-final`, branch `dotnet-migration`. Everything lives in `backend/dotnet/` (the git repo root is `backend/`; `CLAUDE.md` sits one level up in `AMC/.claude/`, outside the repo).

## 0. The headline finding

**The Python backend was mostly stubs.** Only these were implemented: `GET /api/health`, the whole auth flow (`login`, `refresh`, `logout`, `me`), the `Users`/`UserDevices` tables, and `scripts/create_user.py`. Every other router (`customers`, `plans`, `reminders`, `dashboard`, `reports`) is `# TODO`, and every file in `app/services/` (except `auth.py`), `app/jobs/reminder_job.py` and `tests/test_plan_dates.py` is a docstring or placeholder.

So the .NET version is not a port of those parts. **Customers, plans, renewals, reminders, dashboard, reports and exports were built from CLAUDE.md**, and the auth flow was ported from the Python code. The Flutter app's customers/plans/reminders/reports repositories are also empty stubs, so CLAUDE.md section 6 is the only contract for those routes.

## 1. Module map

| Python | .NET equivalent |
|---|---|
| `app/main.py` (router registration, CORS, error handlers) | `Api/Program.cs`, `Api/Infrastructure/ApiErrors.cs` |
| `app/config.py` (pydantic-settings, `.env`) | `appsettings.json` + environment variables, bound to `JwtOptions`, `AuthOptions`, `WhatsAppOptions`, `ReminderJobOptions`, `ClockOptions` |
| `app/db.py` (SQLAlchemy engine/session) | `Infrastructure/Persistence/AppDbContext.cs` (also the `IUnitOfWork`) |
| `app/models/user.py` (`User`, `UserDevice`) | `Domain/Entities/User.cs`, `UserDevice.cs` + EF configuration |
| `app/security.py` (argon2, JWT, refresh helpers) | `Infrastructure/Services/CoreServices.cs`: `IdentityPasswordHasher` (`PasswordHasher<T>`), `JwtTokenService` |
| `app/dependencies.py` (`get_current_user`) | JWT Bearer handler in `Program.cs` (`OnTokenValidated`, `OnChallenge`) + fallback authorization policy |
| `app/errors.py` | `Api/Infrastructure/ApiErrors.cs` (`GlobalExceptionHandler`, model-state factory, status-code pages) |
| `app/schemas/auth.py` | `Application/Dtos/Dtos.cs`, `Application/Validators/Validators.cs` (FluentValidation) |
| `app/services/auth.py` | `Application/Services/AuthService.cs` |
| `app/routers/auth.py` | `Api/Controllers/AuthController.cs` |
| `app/routers/{customers,plans,dashboard,reports,reminders}.py` (stubs) | `CustomersController`, `PlansController`, `DashboardController`, `ReportsController`, `RemindersController` |
| `app/services/{plan_dates,plan_status,renewals}.py` (stubs) | `Domain/Services/PlanDateCalculator.cs`, `PlanStatusCalculator.cs`, `Domain/Entities/Plan.cs` (`Create`, `Renew`) |
| `app/services/whatsapp.py` (stub) | `Infrastructure/Services/WhatsAppSender.cs` (typed `HttpClient`) |
| `app/services/exports.py` (stub) | `Infrastructure/Services/Exporters.cs` (ClosedXML, QuestPDF) |
| `app/jobs/reminder_job.py` (stub; scheduler never started) | `Application/Services/ReminderService.cs` + `Infrastructure/Jobs/ReminderBackgroundService.cs` |
| `migrations/` (Alembic `0001`: Users + UserDevices only) | `Infrastructure/Persistence/Migrations/…_InitialCreate` (all tables) |
| `scripts/create_user.py` | `dotnet AmcTracker.Api.dll create-user <username> [full name]` (`Api/Infrastructure/CreateUserCommand.cs`) |
| `tests/test_auth.py` | `UnitTests/Application/AuthServiceTests.cs` + `IntegrationTests/ApiTests.cs` (`AuthTests`) |
| `tests/test_plan_dates.py` (placeholder) | `UnitTests/Domain/*` (real date, status, renewal and phone tests) |
| `deploy/iis/*` | see section 4, question 1, and `DEPLOY.md` |
| `.claude/skills/run-backend/` (uvicorn smoke test) | Python-specific; not ported (see section 5) |

## 2. Business rules found

### In the Python code (auth only)
- Access token: HS256 JWT, claims `sub`, `typ=access`, `iat`, `exp`; lifetime `JWT_EXPIRE_HOURS` (default 8). Wrong `typ`, bad signature, or expiry give 401 (`token_expired` for expiry, `invalid_token` otherwise, `not_authenticated` for no header).
- Login by username and password plus a **device fingerprint** (16–100 chars) and optional label. Response: `accessToken`, `tokenType: "bearer"`, `expiresIn` (seconds), `refreshToken`, `user {userId, username, fullName}`. `fullName` falls back to the username.
- Unknown or inactive user and wrong password return the **same** 401 `invalid_credentials`, and an unknown user still costs one hash verification (timing equalisation).
- Lockout: 5 failed attempts lock the account for 15 minutes (423 `account_locked`); an expired lock resets the counter; success resets it and sets `LastLoginAt`.
- Device binding: a fingerprint belongs to one user (409 `device_in_use` otherwise). Each device has one refresh token, stored as SHA-256 hex, 90-day **sliding** expiry, rotated on every login and refresh.
- Refresh with a stale token is treated as theft: the device is revoked and every later refresh fails. Logging in again reactivates the device. Logout revokes the caller's own device and is idempotent.
- Errors: `{ "error": { "code", "message" } }`; validation errors are 422 `validation_error` listing field names only (never values).
- CORS open for `localhost`/`127.0.0.1` origins (Flutter web), with a `TODO` to restrict it.

### From CLAUDE.md (implemented from scratch)
- Dates: `EndDate = Start + 12 months`, `NotificationDate = Start + 11 months`, calendar-month arithmetic with month-end clamping (`DateOnly.AddMonths`), always derived from the start date so clamping never accumulates.
- Status: Active before NotificationDate; DueForRenewal from NotificationDate through EndDate inclusive; Expired after EndDate; Renewed once a renewal exists.
- Renewal: new `Plan` linked via `PreviousPlanId`, old plan marked Renewed, start defaults to previous `EndDate + 1 day` (overridable). A plan can be renewed once (409 `already_renewed`).
- Phone numbers must be E.164 (`^\+[1-9]\d{7,14}$`), validated on entry; phones are masked in logs.
- Customers are soft-deleted only; foreign keys never cascade.
- Reminders: unique `(PlanId, ScheduledDate)`, never send twice, log Sent/Failed with error, retry on the next day's run only.
- Money is `decimal(12,2)`, dates are `date`, "today" is Asia/Kolkata.

## 3. Things not in CLAUDE.md (found in the Python code)

| # | Item | What I did |
|---|---|---|
| A | `UserDevices` table (device id, fingerprint, label, refresh-token hash, expiry, last used, revoked) | **Confirmed needed**: the Flutter app sends `deviceFingerprint` and calls `/auth/refresh` and `/auth/logout` (`amc_app/lib/features/auth`). Ported as entity `UserDevice`. |
| B | `Users` has extra columns: `FullName`, `FailedLoginCount`, `LockedUntil`, `IsActive`, `LastLoginAt` | Ported (needed for lockout and `fullName`). |
| C | Routes `POST /api/auth/refresh`, `POST /api/auth/logout`, `GET /api/auth/me` | Ported. They are the Python-vs-CLAUDE.md route difference you asked me to list. CLAUDE.md section 6 now lists them. |
| D | Refresh tokens (90 days) and lockout | CLAUDE.md section 4/10 say only "JWT expiry: 8 hours" and "single staff login". The 8-hour access token matches; the rest is additional behaviour the Flutter app relies on. Kept. |
| E | `scripts/create_user.py` (min 8-char password, `AMC_NEW_PASSWORD` or hidden prompt) | Equivalent `create-user` command (same rules, refuses duplicates). |
| F | `deploy/iis/` (`web.config`, `build.ps1`, `DEPLOY_IIS.md`) and an ignored `dist/amc-api.zip` | See question 1. |

## 4. Where Python and CLAUDE.md disagree, or CLAUDE.md contradicts itself

I did not pick silently. Each item shows what is **currently implemented**; tell me if you want it different.

1. **Deployment target (needs your answer).** CLAUDE.md section 12 says Linux + Plesk + nginx + systemd. The Python repo also ships a full **IIS on Windows** path (HttpPlatformHandler, `web.config`, `build.ps1`) and a built `dist/amc-api.zip`, and the live `.env` points at a remote SQL Server (`103.91.186.145`). **Which are you actually using, Plesk/Linux or IIS/Windows?** `DEPLOY.md` documents both; the IIS section is only needed if IIS stays. This also decides whether `deploy/iis/` is deleted in step 9.
2. **Password hashing.** Python uses argon2 (pwdlib); you asked for `PasswordHasher<T>` (PBKDF2). CLAUDE.md allows "pwdlib or bcrypt". **Existing argon2 hashes will not verify in .NET**, so every user must be re-created with `create-user` (or I can add an argon2 verifier for a transition period; say so if you want that).
3. **Existing database.** From the Python repo (I did not connect to the remote server to check), the DB should have `Users` (created outside Alembic), `UserDevices`, and an `alembic_version` table at revision `0001`, and none of the business tables. Please verify. The EF `InitialCreate` migration creates all six tables, so it **cannot run as-is on that database**. Cleanest path: use a new database, or drop `UserDevices`, `Users` and `alembic_version` first (no real data is lost because hashes are incompatible anyway). **I ran migrations only against a throwaway local SQL Express database and never touched the remote server.**
4. **CORS.** Python allows localhost origins in every environment; .NET enables it only in `Development` (the Python code itself had a TODO to remove it for production).
5. **Inactive users.** Python only checked that the user row exists on each request; .NET also rejects a deactivated user (and refuses to refresh for one). Deliberate hardening, one line to revert.
6. **Token claims.** .NET adds `iss`/`aud` to the JWT. Old Python tokens are not accepted (everyone signs in again once after cutover).
7. **Reminder selection (CLAUDE.md contradicts itself).** Section 4.5 says the job finds plans where `NotificationDate` *equals today*, but also says failures "retry on the next day's run". Those cannot both hold: the day after, the date no longer equals today. **Implemented:** a plan is picked up from `NotificationDate` until `EndDate` until a reminder is `Sent`; `ScheduledDate` = the plan's `NotificationDate`, so a retry updates the same row (the unique constraint stays satisfied). This also catches a missed 09:00 run (server down). Still one attempt per plan per run, no loops.
8. **"Active" on the dashboard.** UI card says "Due in 30 days"; API says "counts: active, due, expired, monthly revenue". `summary` returns `activeCount` (status Active only), `dueForRenewalCount`, `dueIn30DaysCount` (reminder date within 30 days), `expiredCount`, `monthlyRevenue`.
9. **Stored vs derived status.** The `Status` column exists, but a date-derived status goes stale. Only `Renewed` is authoritative; the API always derives the rest from today's date, and the daily job rewrites the stored value for tidiness.
10. **`POST /api/reminders/run` "admin use only"** but CLAUDE.md has no roles. Any signed-in user can call it (it cannot double-send).
11. **`GET /api/customers` default.** `active` omitted returns all customers (active and deleted); `active=true` or `false` filters.
12. **Customer + plan creation.** The add-customer screen collects plan type, but section 6 has separate `POST /customers` and `POST /customers/{id}/plans`. Kept separate; plan `startDate` defaults to the customer's `InstallationDate`.
13. **Reports.** `from`/`to` filter the plan `EndDate` by default; I added optional `status` and `basis=EndDate|NotificationDate|StartDate` so "due", "expiring" and "reminder" reports are possible. Export takes the same filters plus `format=xlsx|pdf`.
14. **Flutter default URL** (`amc_app/lib/core/constants.dart`) is `http://10.0.2.2:8000/api` / `127.0.0.1:8000`. Development config therefore listens on **8000** so the app needs no change; production listens on **5000** behind nginx as you specified.

## 5. Open decisions still open (CLAUDE.md section 9)

Nothing depends on them being settled; each spot has a `TODO(open decision …)` in code.

1. **Monthly plan amount (rate vs total).** `Amount` is stored exactly as given. No ×12 or ÷12 is applied anywhere. Places that matter:
   - `Domain/Entities/Plan.cs`, the `Amount` property.
   - `Application/Services/DashboardService.cs`, `monthlyRevenue`. CLAUDE.md never defines it either; I sum `Amount` of non-renewed, non-expired Monthly plans as stored.
   - Reports/exports show `Amount` as stored; there is no "contract total" column.
2. **Amount on renewal.** `POST /plans/{id}/renew` accepts an optional `amount` and optional `startDate`; if omitted it copies the previous amount. This supports either answer. `Domain/Entities/Plan.cs`, `Renew`.

## 6. Smaller notes

- **QuestPDF** is used under its Community licence; check it fits the company's licence terms before production.
- **Not ported:** `.claude/skills/run-backend` (uvicorn/venv smoke test) is Python-specific. A .NET replacement is a 10-line script; not written yet.
- **Not built because the spec has no endpoint for it:** Payments (phase 2) has a table and entity only.
- **Scheduler caveat:** the job lives in the web process. On IIS the app pool must stay alive (AlwaysRunning, idle timeout 0), same as the Python IIS notes. If the process is down at 09:00 the next run catches up (item 7).
- `dotnet-ef` global tool is 10.0.8 while the runtime is 10.0.13 (harmless warning).
- The commit message in your step 9 brief was cut off after "Replace FastAPI backend with .NET 10 Clean"; I'll need the rest, or I'll use "Replace FastAPI backend with .NET 10 Clean Architecture backend".

## Module 4 (Dashboard): fields removed or changed

The dashboard shows counts and dates only. These old fields no longer exist; nothing in the app or the tests still reads them (searched `lib/`, `test/`, the controllers and the integration tests).

| Endpoint | Removed or changed | Replaced by |
|---|---|---|
| `GET /api/dashboard/summary` | `monthlyRevenue` (removed: no revenue anywhere on the dashboard) | nothing |
| | `dueForRenewalCount` | `dueCount` |
| | `dueIn30DaysCount` (it counted reminder dates within 30 days) | `endingIn30DaysCount` (not renewed, **end date** from today through today + 30) |
| | (new) | `customerCount`, `asOf` |
| `GET /api/dashboard/upcoming` | the reply was a bare array of full plans (with `amount`, `startDate`, `createdAt`...) and `days` defaulted to 30 | `{ "items": [...] }` with `planId, customerId, customerName, phone, projectName, planType, endDate, reminderDate, status, reminderStatus`; `days` defaults to 7 and must be 1 to 90 (else 422) |
| | selected plans by reminder date only | due plans, plus plans whose reminder date is within `days` |
| `GET /api/dashboard/monthly-renewals` | `[{ year, month, label, count }]`, a bare array | `{ "months": [{ month: "2026-10", endingCount, renewedCount, pendingCount }] }`; `months` defaults to 12 (1 to 24) |

Readers of the old shapes, now updated:
- The Module 3 reminder queue called `/dashboard/upcoming` and expected plans with `amount`. It now reads the new items (`RemindersRepository.upcoming`) and keeps only plans with a reminder date of today or later, as before.
- The header bell badge counted sample data. It now counts those same plans, from the dashboard's shared request.

New, to make a card and its list agree: `GET /api/plans?endingWithinDays=N` (1 to 366). The "Ending in 30 days" card count and the list it opens use the same rule (`PlanSpec.EndsWithin`).
