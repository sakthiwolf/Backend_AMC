# Deployment

Choose one section. **Which one applies is still an open question** (see MIGRATION_NOTES.md, section 4, item 1). Both need the **.NET 10 runtime** on the server (ASP.NET Core Runtime 10.x; IIS also needs the Hosting Bundle).

Production settings are environment variables (see README). Never commit them. Create the database first, or point at an existing empty one, then apply migrations (below).

## A. Plesk on Linux (nginx + systemd), as in CLAUDE.md section 12

1. **Build** (on your machine or CI):
   ```bash
   cd backend/dotnet
   dotnet publish src/AmcTracker.Api -c Release -r linux-x64 --self-contained false -o ./publish
   ```
   `-r linux-x64` keeps only the Linux native libraries (without it the output carries every OS, about 170 MB extra). Do not zip or copy old build folders into `publish/`.
2. **Upload** the contents of `publish/` to `/var/www/vhosts/<domain>/api` (so `AmcTracker.Api.dll` is directly inside).
3. **Install the runtime**: `dotnet --list-runtimes` must show `Microsoft.AspNetCore.App 10.x`. Install it from Microsoft's package feed if not.
4. **Environment file** `/etc/amc-api.env` (outside the web folder, `chmod 600`, owned by the service user):
   ```
   ASPNETCORE_ENVIRONMENT=Production
   ConnectionStrings__Default=Server=<host>,<port>;Database=<db>;User Id=<user>;Password=<password>;TrustServerCertificate=True;Encrypt=True
   Jwt__Secret=<32+ random characters>
   WhatsApp__Token=<token>
   WhatsApp__PhoneNumberId=<id>
   ```
5. **systemd** `/etc/systemd/system/amc-api.service`:
   ```ini
   [Unit]
   Description=AMC Tracker API
   After=network.target

   [Service]
   WorkingDirectory=/var/www/vhosts/<domain>/api
   ExecStart=/usr/bin/dotnet /var/www/vhosts/<domain>/api/AmcTracker.Api.dll
   EnvironmentFile=/etc/amc-api.env
   Environment=Kestrel__Endpoints__Http__Url=http://127.0.0.1:5000
   User=<service-user>
   Restart=always
   RestartSec=5
   KillSignal=SIGINT
   SyslogIdentifier=amc-api

   [Install]
   WantedBy=multi-user.target
   ```
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now amc-api
   sudo systemctl status amc-api
   ```
   The service user needs write access to `<api folder>/logs`.
6. **nginx**: Plesk, then *Apache & nginx Settings*, then *Additional nginx directives*:
   ```
   location / {
       proxy_pass http://127.0.0.1:5000;
       proxy_set_header Host $host;
       proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
       proxy_set_header X-Forwarded-Proto $scheme;
   }
   ```
   Domain `api.<company-domain>`, SSL via Let's Encrypt (HTTPS only).
7. **Database migrations** (see "Applying migrations" below), then **create the staff user**:
   ```bash
   cd /var/www/vhosts/<domain>/api
   set -a; . /etc/amc-api.env; set +a
   dotnet AmcTracker.Api.dll create-user admin "Your Name"
   ```
8. **Check**: `curl https://api.<company-domain>/api/health` returns `{"status":"ok"}`.
9. **Update**: stop the service, upload the new `publish/`, apply migrations, start the service.
10. Point the app at it: `flutter build apk --release --dart-define=API_BASE_URL=https://api.<company-domain>/api`.

## B. IIS on Windows (only if IIS is still in use)

1. Install the **.NET 10 Hosting Bundle** (it includes the runtime and the IIS ASP.NET Core Module), then run `iisreset`.
2. `dotnet publish src/AmcTracker.Api -c Release -r win-x64 --self-contained false -o ./publish` and copy `publish/` to e.g. `C:\inetpub\amc-api`. The publish output already contains a `web.config` using the ASP.NET Core Module (in-process hosting).
3. IIS Manager: new application pool with **.NET CLR version = No Managed Code**, *Start Mode* = AlwaysRunning, *Idle Time-out* = 0 (keeps the 09:00 reminder job alive). Create the site on that pool with host name `api.<company-domain>` and an HTTPS binding (win-acme gives free Let's Encrypt certificates).
4. Give `IIS AppPool\<pool name>` Modify on `C:\inetpub\amc-api\logs` and Read on the rest.
5. Settings: set the environment variables from section A, step 4 on the pool (*Configuration Editor*, `system.webServer/aspNetCore`, `environmentVariables`) or in the machine environment. Do not leave secrets in `web.config` inside the repo.
6. Run migrations and `create-user` as in section A (use `dotnet AmcTracker.Api.dll create-user …` from the site folder with the variables set in that PowerShell session).
7. Firewall: open 443 only; do not expose 1433 or the app port.
8. Check `https://api.<company-domain>/api/health`. If it fails, read `logs\stdout*` and `logs\amc-api-*.log`.

## Applying migrations

Pick one:

- **At startup**: set `Database__MigrateOnStartup=true` for one start, then remove it.
- **From a machine that can reach the database** (needs the SDK and `dotnet-ef`):
  ```bash
  dotnet ef database update -p src/AmcTracker.Infrastructure -s src/AmcTracker.Api \
    --connection "<production connection string>"
  ```
- **SQL script** for the DBA to review and run in SSMS:
  ```bash
  dotnet ef migrations script --idempotent -p src/AmcTracker.Infrastructure -s src/AmcTracker.Api -o migrate.sql
  ```

**Existing database from the Python version:** `InitialCreate` cannot run over existing `Users`/`UserDevices` tables. Use a new database, or drop `UserDevices`, `Users` and `alembic_version` first (see MIGRATION_NOTES.md, section 4, item 3) and recreate the staff login with `create-user`.

## Backups

Back up SQL Server daily and test a restore once (CLAUDE.md section 12).
