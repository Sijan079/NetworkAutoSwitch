#Requires -RunAsAdministrator
# Starts the Network Auto Switch tray app at every login, already elevated (no UAC prompt).
# Remove it any time with: .\scripts\uninstall-autostart.ps1
$taskName = 'NetworkAutoSwitchTray'
$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root 'dist\Tray\NetworkAutoSwitchTray.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe`nBuild it first:  .\scripts\deploy.ps1" }

$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$trigger.Delay = 'PT30S'   # give Wi-Fi time to connect, so a slow login is not mistaken for an outage
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Get-ScheduledTask -TaskName $taskName | Select-Object TaskName, State | Format-Table -AutoSize
"Installed. It will start 30 s after your next login. To start it now: Start-ScheduledTask -TaskName $taskName"
