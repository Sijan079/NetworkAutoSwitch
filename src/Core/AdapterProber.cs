using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetworkAutoSwitch.Core;

public record ProbeResult(string Adapter, int IfIndex, bool Healthy, long Ms, string Detail);

/// <summary>Checks real internet on ONE adapter by pinning a socket to it (IP_UNICAST_IF).
/// No routes are added and no admin rights are needed.
/// The check is a bare TCP connect to two independent public servers AT THE SAME TIME: if either answers, the path to the internet works.
/// That takes one round trip (tens of ms) when healthy and gives up after ~1.2 s when not, so an outage is noticed within seconds.</summary>
public static class AdapterProber
{
    const int IP_UNICAST_IF = 31;
    static readonly string[] Targets = { "8.8.8.8", "9.9.9.9" };

    /// <summary>An exact name match wins; only if there is none does a name that merely STARTS with it count
    /// (so a typed "Ethernet" can never silently mean "Ethernet 2" when a real "Ethernet" exists).</summary>
    public static NetworkInterface? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var all = NetworkInterface.GetAllNetworkInterfaces();
        return all.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"adapter down" means Windows itself says the link is down: nothing to wait for, the engine switches at once.</summary>
    public const string AdapterDown = "adapter down";
    public const string NotFound = "adapter not found";
    public const string NoBackup = "no backup connected";

    /// <summary>The adapter is gone or its link is down: certain, so the engine does not wait for several failed checks.</summary>
    public static bool IsLinkDown(string detail) => detail == AdapterDown || detail == NotFound;

    public static ProbeResult Probe(string adapterName, int timeoutMs = 1200)
    {
        var nic = Find(adapterName);
        if (nic is null) return new(adapterName, 0, false, 0, string.IsNullOrWhiteSpace(adapterName) ? NoBackup : NotFound);
        if (nic.OperationalStatus != OperationalStatus.Up) return new(nic.Name, 0, false, 0, AdapterDown);
        int idx = nic.GetIPProperties().GetIPv4Properties().Index;

        using var cts = new CancellationTokenSource(timeoutMs);
        var pending = Targets.Select(t => ConnectAsync(idx, t, cts.Token)).ToList();
        string last = "timeout";
        try
        {
            while (pending.Count > 0)
            {
                var done = Task.WhenAny(pending).GetAwaiter().GetResult();
                pending.Remove(done);
                var (ok, ms, detail, ip) = done.GetAwaiter().GetResult();
                if (ok) { cts.Cancel(); return new(nic.Name, idx, true, ms, $"connected to {ip}"); }
                last = detail;
            }
        }
        catch (Exception e) { last = e.GetBaseException().GetType().Name.Replace("Exception", ""); }
        return new(nic.Name, idx, false, 0, last);
    }

    static async Task<(bool ok, long ms, string detail, string ip)> ConnectAsync(int ifIndex, string ip, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // IPv4 expects the interface index in network byte order
            s.SetRawSocketOption(0, IP_UNICAST_IF, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(ifIndex)));
            await s.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), 443), ct).ConfigureAwait(false);
            return (true, sw.ElapsedMilliseconds, "", ip);
        }
        catch (OperationCanceledException) { return (false, 0, "timeout", ip); }
        catch (Exception e) { return (false, 0, e.GetBaseException() is SocketException se ? se.SocketErrorCode.ToString() : e.GetBaseException().GetType().Name.Replace("Exception", ""), ip); }
    }
}
