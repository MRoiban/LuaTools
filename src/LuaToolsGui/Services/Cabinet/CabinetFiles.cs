using System.IO;
using System.Text;

namespace LuaToolsGui.Services.Cabinet;

internal static class CabinetFiles
{
    // Check the leaf and every ancestor, including the volume root, before accessing state.
    internal static void EnsureSafePath(string path)
    {
        if (path.Split(new[] { '/', '\\' }).Any(part => part == "..")) throw new InvalidDataException("Parent traversal refused");
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if (WorkshopEngine.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are unsupported");
    }

    internal static byte[] ReadBytes(string path, int maximum = 32 * 1024 * 1024)
    {
        CabinetFiles.EnsureSafePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new InvalidDataException("Oversized Cabinet input");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }
    internal static string ReadText(string path, Encoding? encoding = null) =>
        (encoding ?? new UTF8Encoding(false, true)).GetString(ReadBytes(path)).TrimStart('\ufeff');
    internal static string[] ReadLines(string path) => ReadText(path).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
}
