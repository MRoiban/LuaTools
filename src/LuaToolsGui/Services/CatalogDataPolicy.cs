using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services;

internal static class CatalogDataPolicy
{
    internal const int JsonBytes = 8 * 1024 * 1024;
    internal static bool IsId(long value) => value > 0 && value <= uint.MaxValue;
    internal static void Validate(JsonElement element, int maxItems = 10000, int maxString = 1024 * 1024)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            int count = 0;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (++count > maxItems || property.Name.Length > 256 || !keys.Add(property.Name)) throw new InvalidDataException();
                Validate(property.Value, maxItems, maxString);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > maxItems) throw new InvalidDataException();
            foreach (var item in element.EnumerateArray()) Validate(item, maxItems, maxString);
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > maxString) throw new InvalidDataException();
        else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var number) || !double.IsFinite(number))) throw new InvalidDataException();
    }
    internal static void CheckLocal(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException();
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
    }
    internal static byte[] Read(string path, int limit)
    {
        CheckLocal(path);
        using var input = File.OpenRead(path);
        if (input.Length > limit) throw new InvalidDataException();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    internal static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        CheckLocal(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckLocal(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // CacheService also calls this under its synchronous lock on the UI thread.
            // Neither the write nor async disposal may require that caller's context.
            var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
                await output.WriteAsync(bytes, ct).ConfigureAwait(false);
            CheckLocal(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
