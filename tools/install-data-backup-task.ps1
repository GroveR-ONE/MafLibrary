param(
  [int]$Hour = 2,
  [int]$Minute = 30,
  [int]$RetentionDays = 30,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$taskName = 'MafLibrary daily data backup'
$root = Split-Path -Parent $PSScriptRoot
$pinDirectory = Join-Path $env:LOCALAPPDATA 'MafLibrary'
$pinFile = Join-Path $pinDirectory 'backup-pin.dpapi'

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $pinFile) { Remove-Item -LiteralPath $pinFile -Force }
  Write-Output 'Daily backup task removed. Existing backups were retained.'
  exit 0
}

if ($Hour -lt 0 -or $Hour -gt 23 -or $Minute -lt 0 -or $Minute -gt 59) { throw 'Hour must be 0–23 and Minute must be 0–59.' }
$backupScript = Join-Path $PSScriptRoot 'backup-data.ps1'
if (-not (Test-Path -LiteralPath $backupScript)) { throw "Backup script missing: $backupScript" }

Write-Host 'The backup PIN will be stored encrypted for this Windows user (DPAPI), not in plaintext.' -ForegroundColor Cyan
$securePin = Read-Host 'Enter the library edit PIN' -AsSecureString
if ($securePin.Length -eq 0) { throw 'PIN cannot be empty.' }
New-Item -ItemType Directory -Force -Path $pinDirectory | Out-Null
$encryptedPin = ConvertFrom-SecureString -SecureString $securePin
[IO.File]::WriteAllText($pinFile,$encryptedPin,[Text.Encoding]::ASCII)

$powerShell = Join-Path $PSHOME 'powershell.exe'
$arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$backupScript`" -PinFile `"$pinFile`" -RetentionDays $RetentionDays"
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.AddHours($Hour).AddMinutes($Minute))
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description "Daily verified SQLite and uploads backup for MafLibrary." -Force | Out-Null
Write-Output "Installed '$taskName' at $('{0:D2}:{1:D2}' -f $Hour,$Minute), when this Windows user is logged on."
Write-Output "Backups: $(Join-Path $root 'backups\data-backups') (retention $RetentionDays days)."
Write-Output 'The library server must be running at the scheduled time. Check Task Scheduler history after the first run.'
