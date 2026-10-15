# Deploy the API on IIS (Windows Server)

IIS terminates HTTPS and starts Uvicorn through HttpPlatformHandler.

## One-time server setup
1. Install **IIS** (Web Server role).
2. Install **HttpPlatformHandler v1.2** (Microsoft download, "HttpPlatformHandler").
3. Install **Python 3.12 or 3.13** (64-bit, for all users). Avoid betas.
4. Install **ODBC Driver 18 for SQL Server**.

## Deploy
```powershell
$site = "C:\inetpub\amc-api"
New-Item -ItemType Directory -Force $site, "$site\logs"
# Copy these from backend\ : app, migrations, scripts, alembic.ini, requirements.txt
# Do NOT copy .venv (venvs are not portable) or tests.
Copy-Item deploy\iis\web.config $site
cd $site
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```
Create `C:\inetpub\amc-api\.env` (copy from `.env.example`). Use a **new** random
`JWT_SECRET` for production and a dedicated SQL login instead of `sa`.

```powershell
.venv\Scripts\python.exe -m alembic upgrade head
.venv\Scripts\python.exe -m scripts.create_user admin "Your Name"
```

## IIS Manager
1. **Application pool**: new pool, .NET CLR version = *No Managed Code*.
   Advanced settings: *Start Mode* = AlwaysRunning, *Idle Time-out* = 0
   (keeps the daily reminder scheduler alive).
2. **Site**: physical path `C:\inetpub\amc-api`, that pool, host name
   `api.<company-domain>`.
3. **Permissions**: give `IIS AppPool\<pool name>` Modify on `logs\` and Read on
   the rest. The SQL login must allow connections from the server.
4. **HTTPS**: add a binding on 443 with a certificate (win-acme gives free
   Let's Encrypt certs). Redirect HTTP to HTTPS.
5. Firewall: open 443. Do not expose 1433 or 8000.

## Check
- `https://api.<company-domain>/api/health` returns `{"status":"ok"}`.
- If it returns 502/500, read `logs\stdout*.log`.

## Point the app at it
```
flutter build apk --release --dart-define=API_BASE_URL=https://api.<company-domain>/api
```

## Updating
Copy the new `app\` files, run `alembic upgrade head`, then recycle the pool.
