using System.Text.Json.Nodes;
using LuaTools.Agent;
namespace LuaToolsCli;

public static class CommandInput
{
    public static async Task<Parsed> LoadAsync(Parsed parsed, CancellationToken ct)
    {
        var flags = new Dictionary<string, string?>(parsed.Flags);
        async Task<string> Read(string path)
        {
            using var reader = path == "-" ? Console.In : File.OpenText(path);
            char[] buffer = new char[Protocol.MaxRequest + 1];
            int count = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            if (count > Protocol.MaxRequest) throw new InvalidDataException("Input exceeds control-frame limit.");
            return new string(buffer, 0, count);
        }
        if (flags.GetValueOrDefault("data") is { } data)
            flags["data"] = data.StartsWith('@') ? await Read(data[1..]) : data == "-" ? await Read("-") : data;
        if (flags.Remove("ids-from", out var idsFrom)) flags["ids"] = string.Join(',', (flags.GetValueOrDefault("ids") ?? "").Split(',').Concat((await Read(idsFrom!)).Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries)).Where(x => x.Length > 0));
        string? secret = parsed.Secret;
        if (secret is null && (parsed.Has("secret-stdin") || parsed.Has("code-stdin")))
        {
            char[] buffer = new char[4097];
            int count = await Console.In.ReadBlockAsync(buffer.AsMemory(), ct);
            if (count > 4096) throw new InvalidDataException("Secret exceeds input limit.");
            secret = new string(buffer, 0, count).Trim();
            Array.Clear(buffer);
        }
        return parsed with { Flags = flags, Secret = secret };
    }
}
