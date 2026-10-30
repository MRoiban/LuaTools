using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace LuaToolsCli;

internal static class WindowsGuiLauncher
{
    // No inherited standard handles: an auto-started GUI must not hold a redirected CLI stdout open.
    // CreateProcess inherits this process's token and environment, preserving elevation and AppData.
    public static void Start(string executable)
    {
        var startup = new Startup { Size = Marshal.SizeOf<Startup>() };
        if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" --minimized"), IntPtr.Zero, IntPtr.Zero,
            false, 0x00000008, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(process.Thread); CloseHandle(process.Process);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public short Show, ReservedLength; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
