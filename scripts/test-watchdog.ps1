# Run in a SECOND terminal while the app is running with --policy configpolicy-test.json on the backup network.
# Downloads at most ~5 MB. Expected: curl stalls/fails partway, a NetAutoSwitch-DL-CurlTest rule appears, then disappears after ~20 s.
$url = 'https://speed.cloudflare.com/__down?bytes=5000000'
function Rules { Get-NetFirewallRule -Group NetAutoSwitch -ErrorAction SilentlyContinue | Select-Object DisplayName, Action, Enabled }

"1) rules before:"; Rules | Format-Table -AutoSize
"2) downloading up to 5 MB (max 20 s)..."
$out = & "$env:WINDIR\System32\curl.exe" -s -o NUL --max-time 20 -w "bytes=%{size_download} speed=%{speed_download}B/s time=%{time_total}s exit-http=%{http_code}" $url
"   $out   (curl exit code $LASTEXITCODE)"
"3) rules right after:"; Rules | Format-Table -AutoSize
"4) waiting 25 s for the cooldown..."; Start-Sleep 25
"5) rules after cooldown (expect none):"; Rules | Format-Table -AutoSize
