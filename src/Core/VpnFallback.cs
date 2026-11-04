using System.Net.NetworkInformation;

namespace NetworkAutoSwitch.Core;

/// <summary>Some VPNs (Cloudflare WARP in full-tunnel mode, for one) refuse every connection that is forced onto one physical adapter, so the
/// per-adapter check fails with "AccessDenied" even though the internet works. Then the app falls back to what it CAN know:
/// the connection currently in use is checked the normal way (through the VPN, which follows the active adapter), and the idle one is judged
/// by its link and its gateway. Pure decision logic here; the network calls are passed in, so it is easy to test.</summary>
public static class VpnFallback
{
    public const string Denied = "AccessDenied";
    public const string ViaVpn = "checked through the VPN";
    public const string GatewayOnly = "gateway reachable (the VPN hides the internet check)";

    public static bool IsBlocked(ProbeResult r) => !r.Healthy && r.Detail == Denied;

    /// <summary>Replaces the "AccessDenied" results with the best available stand-in. Returns the (possibly new) results and whether the fallback was used.</summary>
    /// <param name="activeIsPrimary">Which connection Windows is using right now.</param>
    /// <param name="unpinned">A normal internet check (not forced onto an adapter). Called at most once.</param>
    /// <param name="gateway">Pings an adapter's gateway: (reachable, round trip ms).</param>
    public static (ProbeResult Primary, ProbeResult Backup, bool Degraded) Apply(ProbeResult primary, ProbeResult backup, bool activeIsPrimary,
        Func<ProbeResult> unpinned, Func<string, (bool Ok, long Ms)> gateway)
    {
        bool pb = IsBlocked(primary), bb = IsBlocked(backup);
        if (!pb && !bb) return (primary, backup, false);
        ProbeResult? net = null;
        ProbeResult Fix(ProbeResult r, bool active)
        {
            if (active)
            {
                net ??= unpinned();
                return net.Healthy ? r with { Healthy = true, Ms = net.Ms, Detail = ViaVpn } : r with { Healthy = false, Ms = 0, Detail = "no internet" };
            }
            var (ok, ms) = gateway(r.Adapter);
            return ok ? r with { Healthy = true, Ms = ms, Detail = GatewayOnly } : r with { Healthy = false, Ms = 0, Detail = "gateway not reachable" };
        }
        return (pb ? Fix(primary, activeIsPrimary) : primary, bb ? Fix(backup, !activeIsPrimary) : backup, true);
    }

    /// <summary>Without a real internet check on the idle connection, moving back to the main one is a guess. If it fails again soon after,
    /// the wait before the next try doubles (up to 5 minutes), so a Wi-Fi that is up but has no internet cannot make the app bounce back and forth.</summary>
    public sealed class Backoff
    {
        readonly Func<int> _base; int _mult = 1; DateTime _backAt = DateTime.MinValue;
        public Backoff(Func<int> baseSeconds) { _base = baseSeconds; }
        /// <summary>Seconds of good checks required before moving back (never below what you set in Settings).</summary>
        public int Seconds => Math.Max(_base(), Math.Min(300, _base() * _mult));

        /// <summary>Call on every switch.</summary>
        public void OnSwitch(Active to, DateTime now, bool degraded)
        {
            if (to == Active.Primary) _backAt = now;
            else if (degraded && now - _backAt < TimeSpan.FromSeconds(90)) _mult = Math.Min(64, _mult * 2);     // failed soon after moving back
        }

        /// <summary>Call every check: after 5 quiet minutes on the main connection the wait goes back to normal.</summary>
        public void Settle(DateTime now, Active active) { if (active == Active.Primary && now - _backAt > TimeSpan.FromMinutes(5)) _mult = 1; }
    }

    /// <summary>Ping an adapter's default gateway (it is on the adapter's own network, so the ping leaves through that adapter).</summary>
    public static (bool Ok, long Ms) PingGateway(string adapterName, int timeoutMs = 800)
    {
        try
        {
            var nic = AdapterProber.Find(adapterName);
            var gw = nic?.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (nic is null || nic.OperationalStatus != OperationalStatus.Up) return (false, 0);
            if (gw is null || gw.Equals(System.Net.IPAddress.Any)) return (true, 0);   // no gateway to ask (some tethers): the link being up is all we know
            using var p = new Ping(); var r = p.Send(gw, timeoutMs);
            return (r.Status == IPStatus.Success, r.RoundtripTime);
        }
        catch { return (false, 0); }
    }
}
