#Requires -RunAsAdministrator
# probe-adapters.ps1 (v2)
# Checks each connected network adapter SEPARATELY to see if it has real internet.
#
# For each adapter it temporarily routes the probe IPs through THAT adapter's
# gateway, runs ping + HTTPS checks, then removes the routes. Nothing permanent changes.
#
# v2: uses several independent targets (8.8.8.8, 9.9.9.9) because some ISPs
# intercept 1.1.1.1, which made v1 report false negatives.

$pingTargets = @("8.8.8.8", "9.9.9.9")
$httpsUrl    = "https://8.8.8.8"     # Google DNS serves a page over HTTPS on its IP

$adapters = Get-NetIPConfiguration | Where-Object {
    $_.NetAdapter.Status -eq "Up" -and $_.IPv4Address -and $_.IPv4DefaultGateway
}

if (-not $adapters) {
    Write-Host "No connected adapters with a gateway found." -ForegroundColor Yellow
    return
}

$results = foreach ($a in $adapters) {
    $idx  = $a.InterfaceIndex
    $gw   = $a.IPv4DefaultGateway.NextHop
    $name = $a.InterfaceAlias
    $pingResults = @{}
    $latencies = @()
    $httpCode = "none"
    $routeError = $null

    try {
        # Force each probe IP out through this adapter only
        foreach ($t in $pingTargets) {
            New-NetRoute -DestinationPrefix "$t/32" -InterfaceIndex $idx -NextHop $gw `
                -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
        }

        foreach ($t in $pingTargets) {
            $out = ping.exe -n 3 -w 2000 $t
            $replies = @($out | Select-String "TTL=").Count
            $pingResults[$t] = "$replies/3"
            $m = $out | Select-String "Average = (\d+)ms"
            if ($m) { $latencies += [int]$m.Matches[0].Groups[1].Value }
        }

        $httpCode = curl.exe -s -o NUL -w '%{http_code}' --max-time 6 $httpsUrl
    }
    catch {
        $routeError = $_.Exception.Message
        Write-Host "Could not probe '$name': $routeError" -ForegroundColor Red
    }
    finally {
        foreach ($t in $pingTargets) {
            Remove-NetRoute -DestinationPrefix "$t/32" -InterfaceIndex $idx `
                -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    $pingOk = ($pingResults.Values | Where-Object { $_ -notlike "0/*" }).Count -gt 0
    $httpOk = $httpCode -match '^[23]\d\d$'
    $verdict = if ($routeError) { "PROBE ERROR" }
               elseif ($pingOk -and $httpOk) { "REAL INTERNET" }
               elseif ($pingOk -or $httpOk) { "PARTIAL (flaky?)" }
               else { "NO INTERNET" }

    [pscustomobject]@{
        Adapter   = $name
        IP        = $a.IPv4Address.IPAddress
        Gateway   = $gw
        "8.8.8.8" = $pingResults["8.8.8.8"]
        "9.9.9.9" = $pingResults["9.9.9.9"]
        HTTPS     = $httpCode
        Latency   = if ($latencies.Count) { "{0} ms" -f [int](($latencies | Measure-Object -Average).Average) } else { "-" }
        Verdict   = $verdict
    }
}

$results | Format-Table -AutoSize