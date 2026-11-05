using System.Net;
using System.Runtime.InteropServices;

namespace NetworkAutoSwitch.Core;

/// <summary>Detects the case where the "main" and the "backup" connection are really the same network (for example both adapters joined
/// to the same Wi-Fi). Then a switch changes nothing: if that network fails, both fail. The test is the router: the same gateway address
/// AND the same hardware (MAC) address behind it. The address alone is not enough, because two different home routers can both be 192.168.1.1.</summary>
public static class SameNetwork
{
    /// <summary>Pure decision. With both hardware addresses readable they decide (two routers can share an address like 192.168.1.1).
    /// When one could not be read, the same gateway address is only enough if the network names do not say otherwise: two known names that differ mean two networks.</summary>
    public static bool Judge(string gatewayA, string macA, string gatewayB, string macB, string nameA = "", string nameB = "")
    {
        if (gatewayA.Length == 0 || gatewayB.Length == 0) return false;                 // nothing to compare (not connected)
        if (!gatewayA.Equals(gatewayB, StringComparison.Ordinal)) return false;         // different routers
        if (macA.Length > 0 && macB.Length > 0) return macA.Equals(macB, StringComparison.OrdinalIgnoreCase);
        if (nameA.Length > 0 && nameB.Length > 0 && !nameA.Equals(nameB, StringComparison.OrdinalIgnoreCase)) return false;   // lookup failed AND the names differ: do not cry wolf
        return true;
    }

    /// <summary>Asks the network for the router's hardware address, from this adapter's own address. "" when it cannot be read. Never throws.</summary>
    public static string GatewayMac(string adapterIp, string gatewayIp)
    {
        try
        {
            if (!IPAddress.TryParse(adapterIp, out var src) || !IPAddress.TryParse(gatewayIp, out var dst)) return "";
            for (int attempt = 0; attempt < 2; attempt++)       // a lookup can fail once for no reason: try again before giving up
            {
                var mac = new byte[8]; int len = mac.Length;
                if (SendARP(BitConverter.ToInt32(dst.GetAddressBytes(), 0), BitConverter.ToInt32(src.GetAddressBytes(), 0), mac, ref len) == 0 && len >= 6) return BitConverter.ToString(mac, 0, 6);
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>True when the two named adapters are connected to the same network right now. Does a quick local lookup (no internet traffic).
    /// The network names (when known) settle the case where the router's hardware address could not be read.</summary>
    public static bool Check(IReadOnlyList<AdapterInfo> all, string primaryName, string backupName, string primaryNetwork = "", string backupNetwork = "")
    {
        var p = all.FirstOrDefault(a => a.Name.Equals(primaryName, StringComparison.OrdinalIgnoreCase));
        var b = all.FirstOrDefault(a => a.Name.Equals(backupName, StringComparison.OrdinalIgnoreCase));
        if (p is null || b is null || !p.IsUp || !b.IsUp || p.Gateway.Length == 0 || b.Gateway.Length == 0 || p.Gateway != b.Gateway) return false;
        return Judge(p.Gateway, GatewayMac(p.Ipv4, p.Gateway), b.Gateway, GatewayMac(b.Ipv4, b.Gateway), primaryNetwork, backupNetwork);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int physicalAddrLen);
}
