using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LuaTools.Agent;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrame = 1024 * 1024;
    public const int MaxRequest = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static JsonNode? Node(object? value) => JsonSerializer.SerializeToNode(value, Json);

    public static async Task<T> ReadAsync<T>(Stream stream, int maximum, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximum) throw new InvalidDataException("FRAME_SIZE");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("FRAME_JSON");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, int maximum, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximum) throw new InvalidDataException("FRAME_SIZE");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

public sealed record Problem(string Field, string Message, string[]? AllowedValues = null);
public sealed record AgentError(string Code, int ExitCode, string Message, string Hint = "Inspect the error code and details, then run luatools doctor --json for recovery actions.", bool Retryable = false,
    Problem[]? Problems = null, string? RequestId = null, JsonNode? Details = null);
public sealed record Warning(string Code, string Message, object? Details = null);
public sealed record Page(int Limit, string? NextCursor, bool HasMore, int Total);
[JsonConverter(typeof(EnvelopeConverter))]
public sealed record Envelope(bool Ok, JsonNode? Data, Page? Page, Warning[] Warnings, AgentError? Error = null)
{
    public static Envelope Success(object? data, Page? page = null, params Warning[] warnings) => new(true, Protocol.Node(data), page, warnings);
    public static Envelope Fail(string code, int exit, string message, string? hint = null, Problem[]? problems = null) =>
        new(false, null, null, [], new(code, exit, message, hint ?? ErrorHints.For(code, exit), exit == 7, problems ?? []));
}

internal sealed class EnvelopeConverter : JsonConverter<Envelope>
{
    public override Envelope Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) return new(false, null, null, [], root.GetProperty("error").Deserialize<AgentError>(options));
        return new(true, JsonNode.Parse(root.GetProperty("data").GetRawText()), root.GetProperty("page").Deserialize<Page>(options), root.GetProperty("warnings").Deserialize<Warning[]>(options) ?? []);
    }
    public override void Write(Utf8JsonWriter writer, Envelope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteBoolean("ok", value.Ok);
        if (value.Ok)
        {
            writer.WritePropertyName("data"); JsonSerializer.Serialize(writer, value.Data, options);
            writer.WritePropertyName("page"); JsonSerializer.Serialize(writer, value.Page, options);
            writer.WritePropertyName("warnings"); JsonSerializer.Serialize(writer, value.Warnings, options);
        }
        else { writer.WritePropertyName("error"); JsonSerializer.Serialize(writer, value.Error, options); }
        writer.WriteEndObject();
    }
}
public sealed record AgentRequest(int Protocol, string Capability, string InstanceId, string RequestId, string Command,
    string[] Arguments, Dictionary<string, string?> Flags, string? ExpectedSteam = null, string ClientVersion = "unknown", SubmittedFile[]? Files = null)
{
    [JsonIgnore] public string? Secret { get; init; }
    [JsonIgnore] public string[] InputPaths { get; set; } = [];
    [JsonIgnore] public bool RetainInputs { get; set; }
}
public sealed record SecretFrame(string Value);
public sealed record Discovery(int Protocol, string InstanceId, string PipeName, int Pid, long StartTime, int SessionId, string Capability, bool Elevated = false, string? SteamIdentity = null);

public static class ErrorHints
{
    public static string For(string code, int exit) => exit switch
    {
        2 => "Correct the reported arguments or input fields; use luatools <resource> <verb> --help for accepted values.",
        3 => "List the resource again and use an identifier returned by that list.",
        4 => "Run luatools account login --browser --dry-run, then repeat with --yes to sign in.",
        5 => "Inspect available_actions and disabled reasons; run luatools doctor --json and follow its recovery commands.",
        6 => "Refresh the resource and rerun --dry-run to obtain a current plan before retrying.",
        7 => "Check luatools ping --json and luatools doctor --json; inspect job list before retrying an operation with an unknown outcome.",
        8 => "Review this command with --dry-run, then resubmit with --yes; add --restart-steam only if the plan requires it.",
        9 => "Start LuaTools, or omit --no-start to allow the client to start it, then retry.",
        130 => "Inspect luatools job list --json before deciding whether to retry the interrupted operation.",
        _ => "Inspect the error details and luatools history list --status failed --limit 1 --json for the failure reason."
    };
}
