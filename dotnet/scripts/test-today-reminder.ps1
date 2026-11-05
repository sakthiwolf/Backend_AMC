<#
  Test the "reminder due today" scenario end to end, sending ONLY to a test phone (never to a customer).

  Before running:
    1. Make a reminder due today. In SSMS open sql\check_and_resend_customer_reminder.sql, set @Name to a customer
       whose Phone does not matter (their number is NOT used in test mode), run with @Resend = 1.
    2. Have your WhatsApp (Wacto) values ready: PhoneNumberId and Token.

  Run (from the dotnet folder):
    .\scripts\test-today-reminder.ps1 -PhoneNumberId "<id>" -Token "<token>"
    .\scripts\test-today-reminder.ps1 -PhoneNumberId "<id>" -Token "<token>" -Phone "9344879054" -InMinutes 1

  What it does: starts the API with Reminders:Enabled=true, TestPhoneOnly=true, TestPhone=<Phone>, and RunAt set
  to a few minutes from now (IST). When that time arrives the job sends today's due reminders to <Phone> only.
  Watch the console for "Reminder ... sent" or "failed". Press Ctrl+C to stop.
#>
param(
    [Parameter(Mandatory)] [string] $PhoneNumberId,
    [Parameter(Mandatory)] [string] $Token,
    [string] $Phone = "9344879054",
    [int] $InMinutes = 2
)

$digits = ($Phone -replace '\D', '')
if ($digits.Length -eq 10) { $digits = "91$digits" }
$e164 = "+$digits"

$ist = [TimeZoneInfo]::FindSystemTimeZoneById("India Standard Time")
$runAt = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $ist).AddMinutes($InMinutes).ToString("HH:mm")

$env:ASPNETCORE_ENVIRONMENT      = "Development"
$env:Reminders__Enabled          = "true"
$env:Reminders__TestPhoneOnly    = "true"     # customers are never messaged
$env:Reminders__TestPhone        = $e164
$env:Reminders__RunAt            = $runAt
$env:WhatsApp__Provider          = "Wacto"
$env:WhatsApp__PhoneNumberId     = $PhoneNumberId
$env:WhatsApp__Token             = $Token

Write-Host "Test phone : $e164"
Write-Host "Job runs at: $runAt IST (in about $InMinutes min)"
Write-Host "Starting API... Ctrl+C to stop."

$api = Join-Path $PSScriptRoot "..\src\AmcTracker.Api"
dotnet run --project $api --launch-profile http
