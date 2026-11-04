using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace NetworkAutoSwitch.Core;

public enum AdapterKind { WiFi, Ethernet, Tether, Virtual, Other }

public record AdapterInfo(string Name, string Description, AdapterKind Kind, bool IsUp, bool HasGateway, string Ipv4);

/// <summary>Finds the network adapters on this PC and decides which one is the main connection and which the backup,
/// so nothing depends on the names "Wi-Fi" or "Ethernet 2". The deciding parts are pure functions (easy to test).</summary>
public static class AdapterDiscovery
{
    // phones and modems: these are what the backup is normally made of
    static readonly Regex TetherWords = new(@"rndis|remote ndis|apple mobile|iphone|android|mobile broadband|bluetooth|huawei|xiaomi|samsung mobile|tethering|usb.*\b(modem|lte)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // driver layers Windows lists next to every real adapter ("Wi-Fi-QoS Packet Scheduler-0000"). They are not connections.
    static readonly Regex DriverLayers = new(@"lightweight filter|packet scheduler|packet driver|filter driver|kernel debug|native wifi|-\d{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // software adapters: VPNs, virtual machines, overlay networks. Never a real internet connection of their own.
    static readonly Regex VirtualWords = new(@"zerotier|virtual|vpn|tap-|\btap\b|\btun\b|wireguard|hyper-v|vethernet|vmware|vbox|warp|tailscale|npcap|loopback|miniport|teredo|isatap|6to4|tunnel|nordlynx|openvpn|fortinet|cisco anyconnect", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AdapterKind Classify(string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return AdapterKind.Virtual;
        if (DriverLayers.IsMatch(description)) return AdapterKind.Virtual;
        if (TetherWords.IsMatch(description)) return AdapterKind.Tether;
        if (VirtualWords.IsMatch(description)) return AdapterKind.Virtual;
        return type switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Tether,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
            _ => AdapterKind.Other,
        };
    }

    public static string KindLabel(AdapterKind k) => k switch { AdapterKind.WiFi => "Wi-Fi", AdapterKind.Ethernet => "Ethernet", AdapterKind.Tether => "Phone Tether", AdapterKind.Virtual => "Virtual", _ => "Other" };

    /// <summary>Every adapter on this PC (loopback left out), connected or not.</summary>
    public static List<AdapterInfo> Detect()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var props = nic.GetIPProperties();
                string ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                result.Add(new AdapterInfo(nic.Name, nic.Description, Classify(nic.Description, nic.NetworkInterfaceType), nic.OperationalStatus == OperationalStatus.Up, gw, ip));
            }
            catch { /* an adapter that cannot be read right now: skip it */ }
        }
        return result;
    }

    /// <summary>The adapters you may choose between (real ones, not VPNs or virtual machines).</summary>
    public static List<AdapterInfo> Candidates(IEnumerable<AdapterInfo> all) => all.Where(a => a.Kind is AdapterKind.WiFi or AdapterKind.Ethernet or AdapterKind.Tether).ToList();

    /// <summary>Picks the main and backup connection.
    /// Backup: a phone/modem adapter if there is one (the one already in use, if it is still there), else the second normal adapter.
    /// Main: the one already in use if it still exists (so a Wi-Fi that drops is not swapped away), else a connected wired one, else Wi-Fi.
    /// Returns null for "none".</summary>
    public static (string? Primary, string? Backup) Suggest(IReadOnlyList<AdapterInfo> adapters, string? previousPrimary = null, string? previousBackup = null)
    {
        var real = Candidates(adapters);
        var tethers = real.Where(a => a.Kind == AdapterKind.Tether).ToList();
        var mains = real.Where(a => a.Kind != AdapterKind.Tether).ToList();

        // connected beats not, wired beats wireless, a built-in adapter beats a USB add-on, USB/RNDIS beats Bluetooth for the backup
        static int Score(AdapterInfo a) => (a.IsUp && a.HasGateway ? 100 : a.IsUp ? 50 : 0) + (a.Kind == AdapterKind.Ethernet ? 10 : 0)
            - (a.Description.Contains("usb", StringComparison.OrdinalIgnoreCase) ? 5 : 0) - (a.Description.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ? 3 : 0);
        AdapterInfo? Keep(IEnumerable<AdapterInfo> from, string? prev) => prev is null ? null : from.FirstOrDefault(a => a.Name.Equals(prev, StringComparison.OrdinalIgnoreCase));

        AdapterInfo? primary = Keep(mains, previousPrimary) ?? mains.OrderByDescending(Score).FirstOrDefault();
        AdapterInfo? backup;
        if (tethers.Count > 0) backup = Keep(tethers, previousBackup) ?? tethers.OrderByDescending(Score).First();
        else backup = Keep(mains.Where(a => a != primary), previousBackup) ?? mains.Where(a => a != primary).OrderByDescending(Score).FirstOrDefault();

        if (primary is null && tethers.Count > 0 && backup is not null) { primary = backup; backup = null; }   // only a phone is connected: it is the only connection there is
        return (primary?.Name, backup?.Name);
    }
}
