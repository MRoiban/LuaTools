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
        var files = new List<SubmittedFile>(); var contents = new List<byte[]>();
        string? selected = flags.GetValueOrDefault("file") ?? flags.GetValueOrDefault("diff-file");
        if (selected is not null)
        {
            long total = 0;
            foreach (string path in selected.Split('\n'))
            {
                if (files.Count >= FileSubmission.MaxFiles) throw new InvalidDataException();
                await using var input = File.OpenRead(path);
                if (input.Length <= 0 || input.Length > FileSubmission.MaxBytes || (total += input.Length) > FileSubmission.MaxBytes) throw new InvalidDataException();
                byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, ct);
                var file = new SubmittedFile(Path.GetFileName(path), bytes.Length, FileSubmission.Digest(bytes));
                if (!FileSubmission.Valid(file)) throw new InvalidDataException();
                files.Add(file); contents.Add(bytes);
            }
            string flag = flags.ContainsKey("file") ? "file" : "diff-file";
            flags[flag] = string.Join(",", files.Select(f => f.Sha256));
        }
        return parsed with { Flags = flags, Files = files.ToArray(), FileBytes = contents.ToArray() };
    }
}
