#Requires -RunAsAdministrator
# Removes the login auto-start. Does not touch the app files. If the tray is running, use its Exit menu.
$taskName = 'NetworkAutoSwitchTray'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    "Removed scheduled task '$taskName'."
} else { "Task '$taskName' was not installed." }
