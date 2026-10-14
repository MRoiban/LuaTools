using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>Single caller CDP transport with correlated commands and bounded messages and time.</summary>
internal sealed class SteamCdpConnection(WebSocket socket) : IDisposable
{
    internal const int MaximumMessageBytes = 2 * 1024 * 1024;
    private int _nextId;

    internal async Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        int id = checked(++_nextId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("CEF command exceeds size limit.");
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token);
        for (int skipped = 0; skipped < 128; skipped++)
        {
            var response = await ReadMessageAsync(socket, deadline.Token);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out int actual) || actual != id) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("CEF command failed.");
            if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("CEF response has no result.");
            return result.Clone();
        }
        throw new InvalidDataException("Too many unrelated CEF messages.");
    }

    internal async Task<string> EvaluateAsync(int contextId, string expression, CancellationToken ct)
    {
        var result = await CommandAsync("Runtime.evaluate", new { contextId, expression, returnByValue = true }, ct);
        if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("Steam plugin script failed.");
        if (!result.TryGetProperty("result", out var value) || !value.TryGetProperty("value", out var raw)
            || raw.ValueKind != JsonValueKind.String) throw new InvalidDataException("Unexpected Steam plugin result.");
        return raw.GetString()!;
    }

    internal static async Task<byte[]> ReadMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (received.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected a CEF text message.");
            if (message.Length + received.Count > MaximumMessageBytes) throw new InvalidDataException("CEF response exceeds size limit.");
            message.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return message.ToArray();
    }

    public void Dispose() => socket.Dispose();
}
