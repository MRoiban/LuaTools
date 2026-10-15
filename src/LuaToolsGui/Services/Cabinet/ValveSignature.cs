using Microsoft.Win32.SafeHandles;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>Offline Authenticode verification. No revocation fetches or chain URL retrieval.</summary>
internal static class ValveSignature
{
    private static readonly Guid Action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint Ui, Revocation, Choice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State, Url;
        public uint Flags, Context;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public IntPtr Certificate;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    internal static string FinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            path.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        }
        if (length == 0 || length >= path.Capacity) throw new IOException("Cannot resolve locked Steam client path");
        return path.ToString();
    }
    internal static bool Verify(string path, SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed) return false;
        CabinetFiles.EnsureSafePath(path);
        var info = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = Path.GetFullPath(path), Handle = handle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        Marshal.StructureToPtr(info, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer,
            StateAction = 1,
            // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE.
            Flags = 0x1000 | 0x10
        };
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), Action, ref data) != 0) return false;
            var provider = WTHelperProvDataFromStateData(data.State);
            if (provider == IntPtr.Zero) return false;
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return false;
            var certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return false;
            var native = Marshal.PtrToStructure<ProviderCertificate>(certificate);
            if (native.Certificate == IntPtr.Zero) return false;
            using var cert = new X509Certificate2(native.Certificate);
            return cert.GetNameInfo(X509NameType.SimpleName, false) is "Valve Corp." or "Valve Corp";
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), Action, ref data);
            Marshal.DestroyStructure<FileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(handle);
        }
    }
}
