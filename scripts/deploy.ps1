# Builds, tests and publishes the tray app to dist\Tray, then restarts it.
# Safe to run while the app is running: it asks the running copy to exit the normal way first, which puts your network settings back.
# No administrator rights needed. Restart goes through the "NetworkAutoSwitchTray" scheduled task (see install-autostart.ps1) if it exists.
param([switch]$SkipTests, [switch]$NoStart)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet build -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not $SkipTests) {
    $out = dotnet run -c Release --no-build --project src\Cli -- --selftest
    $out | Select-String -CaseSensitive '^FAIL|ALL PASSED'
    if ($out | Where-Object { $_ -cmatch '^FAIL' }) { throw 'Self-tests failed: not deploying.' }   # case-sensitive: test NAMES may contain the word "failures"
}

# 1) stop the running copy cleanly (it restores metrics and removes its rules before exiting)
$exe = Join-Path $root 'dist\Tray\NetworkAutoSwitchTray.exe'
if (Get-Process NetworkAutoSwitchTray -ErrorAction SilentlyContinue) {
    if (Test-Path $exe) { & $exe --quit }
    for ($i = 0; $i -lt 40 -and (Get-Process NetworkAutoSwitchTray -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    if (Get-Process NetworkAutoSwitchTray -ErrorAction SilentlyContinue) { throw 'The tray did not exit. Use its Exit menu, then run this again.' }
}

# 2) publish
dotnet publish src\Tray -c Release -o dist\Tray | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
"Published to $root\dist\Tray"

# 3) start again
if (-not $NoStart) {
    if (Get-ScheduledTask -TaskName NetworkAutoSwitchTray -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName NetworkAutoSwitchTray; 'Started via the scheduled task (already elevated).' }
    else { Start-Process $exe; 'Started (Windows will ask for administrator rights).' }
}
