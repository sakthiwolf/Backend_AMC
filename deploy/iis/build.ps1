# Packages the backend into dist\amc-api.zip, ready to unzip on the IIS server.
# Run from the backend folder:
#   powershell -File deploy\iis\build.ps1 -SitePath "C:\Inetpub\vhosts\<domain>\httpdocs"
param([string]$SitePath = "C:\inetpub\amc-api")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$stage = Join-Path $root "dist\amc-api"
$zip = Join-Path $root "dist\amc-api.zip"

if (Test-Path "$root\dist") { Remove-Item "$root\dist" -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\logs" | Out-Null

foreach ($dir in "app", "migrations", "scripts") {
    Copy-Item "$root\$dir" "$stage\$dir" -Recurse
}
Copy-Item "$root\alembic.ini", "$root\requirements.txt" $stage

# web.config points at the real site folder on the server.
(Get-Content "$PSScriptRoot\web.config" -Raw).Replace("C:\inetpub\amc-api", $SitePath.TrimEnd("\")) |
    Set-Content "$stage\web.config" -Encoding UTF8
Copy-Item "$PSScriptRoot\DEPLOY_IIS.md" $stage

# Production settings become .env on the server (never the local dev .env).
if (Test-Path "$PSScriptRoot\.env.production") {
    Copy-Item "$PSScriptRoot\.env.production" "$stage\.env"
} else {
    Copy-Item "$root\.env.example" "$stage\.env"
    Write-Warning ".env.production not found; packaged .env.example. Edit .env on the server."
}

Get-ChildItem $stage -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
Write-Host "Built $zip"
