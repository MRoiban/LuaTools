using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LuaTools.Agent;

/// <summary>Only discovery and launcher records live here. Business state is never opened by the client.</summary>
public static class LocalDiscovery
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuaToolsGui-SecurityHardened", "bin");
    public static string Record => Path.Combine(Root, "agent-v1.json");
    public static string Launcher => Path.Combine(Root, "gui-path.json");

    public static void SecureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        CheckReparse(path);
        var sid = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (Directory.Exists(path)) Verify(path, true);
        else new DirectoryInfo(path).Create(security);
        Verify(path, true);
    }

    public static void CheckReparse(string path)
    {
        for (var p = new DirectoryInfo(Path.GetFullPath(path)); p is not null; p = p.Parent)
            if ((File.Exists(p.FullName) || Directory.Exists(p.FullName)) && (File.GetAttributes(p.FullName) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Unsafe discovery path.");
    }

    public static void Verify(string path, bool directory = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        CheckReparse(path);
        var sid = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Discovery owner mismatch.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(rule.IdentityReference)) throw new UnauthorizedAccessException("Discovery must be owner-only.");
    }

    public static void Write<T>(string path, T record)
    {
        SecureDirectory(Root);
        if (File.Exists(path)) Verify(path);
        string temp = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".new");
        try { File.WriteAllText(temp, JsonSerializer.Serialize(record, Protocol.Json)); Verify(temp); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Read<T>(string path)
    {
        Verify(Root, true); Verify(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 8192) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(file, Protocol.Json) ?? throw new InvalidDataException();
    }
    public static void VerifyProcess(Discovery d)
    {
        if (d.Protocol != Protocol.Version || !d.PipeName.StartsWith("LuaTools.Agent.v1.", StringComparison.Ordinal) || d.PipeName.Length > 180 || d.PipeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.')))
            throw new InvalidDataException();
        using var process = Process.GetProcessById(d.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != d.StartTime || process.SessionId != d.SessionId || process.SessionId != Process.GetCurrentProcess().SessionId) throw new UnauthorizedAccessException();
        var launcher = Read<LauncherRecord>(Launcher);
        if (!string.Equals(process.MainModule?.FileName, launcher.Executable, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
    }
    public static byte[] Protect(byte[] bytes, bool decrypt = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        Blob output = default;
        try
        {
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException();
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    public static void VerifyServer(NamedPipeClientStream pipe, Discovery record)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != record.Pid) throw new UnauthorizedAccessException();
        VerifyProcess(record);
        VerifyToken(record.Pid);
    }
    public static void VerifyClient(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // CurrentUserOnly enforces account AND elevation. Independently reject remote/session clients
        // before reading a capability, using kernel-reported computer name and client PID/session.
        var computer = new StringBuilder(512);
        // Local NPFS clients return ERROR_PIPE_LOCAL (229). A successful lookup means a
        // remote SMB client, even if its supplied computer name matches this machine.
        if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, computer, 512) || Marshal.GetLastWin32Error() != 229) throw new UnauthorizedAccessException();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new UnauthorizedAccessException();
        using var client = Process.GetProcessById(checked((int)pid));
        if (client.SessionId != Process.GetCurrentProcess().SessionId) throw new UnauthorizedAccessException();
        VerifyToken(client.Id);
    }
    private static void VerifyToken(int pid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = Process.GetProcessById(pid);
        if (!OpenProcessToken(process.SafeHandle, 8, out var token)) throw new UnauthorizedAccessException();
        using (token)
        {
            using var peer = new WindowsIdentity(token.DangerousGetHandle());
            if (!Equals(peer.User, WindowsIdentity.GetCurrent().User) || !GetTokenInformation(token, 20, out int elevated, 4, out _) || elevated != 0)
                throw new UnauthorizedAccessException();
        }
    }
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle handle, StringBuilder name, uint length);
}
public sealed record LauncherRecord(string Executable);
