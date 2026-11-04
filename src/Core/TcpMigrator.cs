using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace NetworkAutoSwitch.Core;

/// <summary>Finds the TCP connections that run over the backup adapter and resets them, so the program that owns them
/// reconnects at once over the (now preferred) main connection. Resetting is what makes a running download follow the route change.</summary>
public static class TcpMigrator
{
    public record Conn(int Pid, uint LocalAddr, uint LocalPort, uint RemoteAddr, uint RemotePort);

    [StructLayout(LayoutKind.Sequential)] struct MibTcpRow { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint SetTcpEntry(ref MibTcpRow row);

    const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
    const uint ESTABLISHED = 5, DELETE_TCB = 12;

    /// <summary>All established IPv4 connections whose local address is one of <paramref name="localIps"/> (the backup adapter's addresses).</summary>
    public static List<Conn> Established(IEnumerable<IPAddress> localIps)
    {
        var mine = localIps.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(i => BitConverter.ToUInt32(i.GetAddressBytes(), 0)).ToHashSet();
        var result = new List<Conn>();
        if (mine.Count == 0) return result;

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);   // first call only asks how big the table is
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return result;
            int count = Marshal.ReadInt32(buf);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buf + 4 + i * 24;      // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid
                uint state = (uint)Marshal.ReadInt32(row, 0), la = (uint)Marshal.ReadInt32(row, 4), lp = (uint)Marshal.ReadInt32(row, 8);
                uint ra = (uint)Marshal.ReadInt32(row, 12), rp = (uint)Marshal.ReadInt32(row, 16); int pid = Marshal.ReadInt32(row, 20);
                if (state == ESTABLISHED && mine.Contains(la)) result.Add(new Conn(pid, la, lp, ra, rp));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    /// <summary>Resets the connections (needs admin). Returns how many were reset.</summary>
    public static int Reset(IEnumerable<Conn> conns)
    {
        int n = 0;
        foreach (var c in conns)
        {
            var row = new MibTcpRow { State = DELETE_TCB, LocalAddr = c.LocalAddr, LocalPort = c.LocalPort, RemoteAddr = c.RemoteAddr, RemotePort = c.RemotePort };
            if (SetTcpEntry(ref row) == 0) n++;
        }
        return n;
    }
}

/// <summary>Full file path of a running process, cheaply (Process.MainModule is slow because it walks every loaded DLL).</summary>
public static class ProcPath
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    public static string? Of(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
        finally { CloseHandle(h); }
    }
}
