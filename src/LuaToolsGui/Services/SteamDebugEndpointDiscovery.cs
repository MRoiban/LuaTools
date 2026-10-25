using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>Matches local CEF listeners to the selected installation, never to the first responding client.</summary>
internal sealed class SteamDebugEndpointDiscovery
{
    internal sealed record Listener(string Address, string ExecutablePath, int SessionId = 0, int Port = 8080);
    private DateTime _expires;
    private Listener[] _snapshot = [];

    internal string? Find(string? installation, bool refresh = false)
    {
        if (refresh || DateTime.UtcNow >= _expires)
        {
            _snapshot = ReadListeners().ToArray();
            _expires = DateTime.UtcNow.AddSeconds(1);
        }
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return Select(installation, _snapshot, current.SessionId);
    }

    internal static string? Select(string? installation, IEnumerable<Listener> listeners, int sessionId = 0)
    {
        if (string.IsNullOrWhiteSpace(installation)) return null;
        string root;
        try { root = Path.GetFullPath(installation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { return null; }
        foreach (var listener in listeners)
        {
            if (listener.SessionId != sessionId || listener.Port is < 1 or > 65535) continue;
            if (listener.Address is not ("127.0.0.1" or "[::1]")) continue;
            string executable;
            try { executable = Path.GetFullPath(listener.ExecutablePath); }
            catch { continue; }
            if (executable.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(executable).Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase))
                return "http://" + listener.Address + ":" + listener.Port + "/json";
        }
        return null;
    }

    private static IEnumerable<Listener> ReadListeners()
    {
        var result = new List<Listener>();
        if (!OperatingSystem.IsWindows()) return result;
        foreach (int family in new[] { 2, 23 })
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (size <= 0 || size > 16 * 1024 * 1024) continue;
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, family, 3, 0) != 0) continue;
                int rowSize = family == 2 ? 24 : 56;
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > (size - 4) / rowSize) continue;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
                    int portOffset = family == 2 ? 8 : 20;
                    int port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                    if (port == 0) continue;
                    byte[] bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var address = new IPAddress(bytes);
                    if (family == 2 ? !address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.Any)
                        : !address.Equals(IPAddress.IPv6Loopback) && !address.Equals(IPAddress.IPv6Any)) continue;
                    int pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    if (!ProcessIdToSessionId(pid, out int session)) continue;
                    using var current = System.Diagnostics.Process.GetCurrentProcess();
                    if (session != current.SessionId) continue;
                    string? executable = ProcessPath(pid);
                    if (executable is not null) result.Add(new(family == 2 ? "127.0.0.1" : "[::1]", executable, session, port));
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return result;
    }

    private static string? ProcessPath(int pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION, without VM_READ.
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
