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
public sealed record AgentError(string Code, int ExitCode, string Message, string Hint = "luatools help", bool Retryable = false,
    Problem[]? Problems = null, string? RequestId = null);
public sealed record Warning(string Code, string Message, object? Details = null);
public sealed record Page(int Limit, string? NextCursor, bool HasMore, int Total);
[JsonConverter(typeof(EnvelopeConverter))]
public sealed record Envelope(bool Ok, JsonNode? Data, Page? Page, Warning[] Warnings, AgentError? Error = null)
{
    public static Envelope Success(object? data, Page? page = null, params Warning[] warnings) => new(true, Protocol.Node(data), page, warnings);
    public static Envelope Fail(string code, int exit, string message, string hint = "luatools help", Problem[]? problems = null) =>
        new(false, null, null, [], new(code, exit, message, hint, exit == 7, problems ?? []));
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
    string[] Arguments, Dictionary<string, string?> Flags, string? ExpectedSteam = null, string ClientVersion = "unknown");
public sealed record Discovery(int Protocol, string InstanceId, string PipeName, int Pid, long StartTime, int SessionId, string Capability);
