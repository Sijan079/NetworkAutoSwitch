using System.Runtime.InteropServices;
using System.Text;

namespace NetworkAutoSwitch.Core;

/// <summary>Finds where a running program lives, fast.</summary>
public static class ProcessPaths
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    /// <summary>Process.MainModule is slow (it walks every loaded DLL). This asks Windows for just the file path, which takes microseconds. Null when it cannot be read.</summary>
    public static string? PathOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
        finally { CloseHandle(h); }
    }

    /// <summary>The programs running right now (outside the Windows folder, this process excluded), as full paths, one per distinct path.</summary>
    public static List<string> Running(CancellationToken token = default)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int me = Environment.ProcessId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var list = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (p.Id == me) continue;
                    string? path = PathOf(p.Id);
                    if (string.IsNullOrEmpty(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) continue;
                    list.Add(path);
                }
                catch { /* not readable: not interesting */ }
            }
        }
        return list;
    }
}
