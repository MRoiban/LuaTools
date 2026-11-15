using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.Cabinet;

/// <summary>
/// The connected Cabinet server and its bearer token. Never logged: <see cref="ToString"/> omits the
/// token, which is why this is a class rather than a record (a record would print every member).
/// </summary>
internal sealed class CabinetCredentials(string server, string token, string deviceId)
{
    public string Server { get; } = server;
    public string Token { get; } = token;
    public string DeviceId { get; } = deviceId;
    public override string ToString() => Server;
}

/// <summary>Where the Cabinet connection lives (<see cref="CabinetService"/>). Null when not connected.</summary>
internal interface ICabinetCredentialSource
{
    CabinetCredentials? Credentials { get; }
}

/// <summary>
/// A cached package's server-side description. <see cref="Pins"/> are read by the server from the
/// package's own lua; the client still re-reads them from the bytes before trusting them.
/// </summary>
public sealed record CabinetLuaMeta(
    long AppId, string Sha256, long Size, IReadOnlyDictionary<long, string> Pins,
    string Source, DateTimeOffset UploadedAt, string Uploader);

/// <summary>The Cabinet server's shared lua cache (docs/cloud-api.md, "Lua config cache").</summary>
public interface ICabinetLuaCacheClient
{
    /// <summary>True while Cabinet Cloud is connected.</summary>
    bool IsConnected { get; }
    /// <summary>The current cached package's meta, or null when the server holds none. Throws when unreachable.</summary>
    Task<CabinetLuaMeta?> GetMetaAsync(long appId, CancellationToken ct);
    /// <summary>The kept package with this SHA-256, or null when it is no longer kept. Not verified here.</summary>
    Task<byte[]?> DownloadAsync(long appId, string sha256, CancellationToken ct);
    /// <summary>Share a package. The server keeps it for every user; the same package again is a no-op.</summary>
    Task UploadAsync(long appId, byte[] package, string sha256, string source, CancellationToken ct);
}

/// <summary>
/// HTTPS transport for the lua cache. Like the engine's own transport and pairing, it talks only to the
/// user-selected Cabinet origin (outside the app host allowlist, see ENDPOINTS.md "Cabinet"), with no
/// redirects, cookies or app account headers, and sends that origin as <c>Origin</c> because Bedrock
/// refuses writes from non-browser clients without it.
/// </summary>
internal sealed class CabinetLuaCacheClient : ICabinetLuaCacheClient, IDisposable
{
    /// <summary>The server's upload ceiling, and so the largest package worth downloading.</summary>
    public const int MaxPackageBytes = 90 * 1024 * 1024;
    private const int MaxMetaBytes = 256 * 1024;
    private static readonly TimeSpan MetaTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(2);

    private readonly ICabinetCredentialSource _credentials;
    private readonly HttpClient _http;

    public CabinetLuaCacheClient(ICabinetCredentialSource credentials) : this(credentials, null) { }

    internal CabinetLuaCacheClient(ICabinetCredentialSource credentials, HttpMessageHandler? transport)
    {
        _credentials = credentials;
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per request, below
        };
    }

    public bool IsConnected => _credentials.Credentials is not null;

    public Task<CabinetLuaMeta?> GetMetaAsync(long appId, CancellationToken ct) => WithDeadline(MetaTimeout, ct, async token =>
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/apps/{Id(appId)}/lua/meta", null, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        return (CabinetLuaMeta?)ParseMeta(await ReadAsync(response, MaxMetaBytes, token).ConfigureAwait(false), appId);
    });

    public Task<byte[]?> DownloadAsync(long appId, string sha256, CancellationToken ct)
    {
        if (!IsSha256(sha256)) throw new ArgumentException("Invalid SHA-256.", nameof(sha256));
        return WithDeadline(TransferTimeout, ct, async token =>
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/apps/{Id(appId)}/lua?sha256={sha256}", null, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Ensure(response);
            return (byte[]?)await ReadAsync(response, MaxPackageBytes, token).ConfigureAwait(false);
        });
    }

    public Task UploadAsync(long appId, byte[] package, string sha256, string source, CancellationToken ct)
    {
        if (!IsSha256(sha256)) throw new ArgumentException("Invalid SHA-256.", nameof(sha256));
        if (package.Length == 0 || package.Length > MaxPackageBytes) throw new ArgumentException("Package size is out of range.", nameof(package));
        var credentials = _credentials.Credentials ?? throw new InvalidOperationException("Cabinet Cloud is not connected.");
        string query = $"sha256={sha256}&source={Uri.EscapeDataString(SourceLabel(source))}&deviceId={Uri.EscapeDataString(credentials.DeviceId)}";
        return WithDeadline(TransferTimeout, ct, async token =>
        {
            var content = new ByteArrayContent(package);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await SendAsync(HttpMethod.Put, $"/api/apps/{Id(appId)}/lua?{query}", content, token, credentials).ConfigureAwait(false);
            Ensure(response);
            return true;
        });
    }

    /// <summary>The server takes 1–64 characters with no control characters.</summary>
    internal static string SourceLabel(string? source)
    {
        string clean = new((source ?? "").Where(c => !char.IsControl(c)).ToArray());
        clean = clean.Trim();
        if (clean.Length == 0) clean = "unknown";
        return clean.Length <= 64 ? clean : clean[..64];
    }

    /// <summary>One deadline covers the request and reading its body; it surfaces as a timeout, not a cancel.</summary>
    private static async Task<T> WithDeadline<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { return await action(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Cabinet Cloud did not answer in time.");
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken ct, CabinetCredentials? credentials = null)
    {
        credentials ??= _credentials.Credentials ?? throw new InvalidOperationException("Cabinet Cloud is not connected.");
        var request = new HttpRequestMessage(method, credentials.Server + path) { Content = content };
        request.Headers.UserAgent.ParseAdd("LuaTools/" + typeof(AppConfig).Assembly.GetName().Version?.ToString(3) + " (Windows)");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        request.Headers.Add("Origin", credentials.Server);
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // The status alone: an error body is the server's, and is not worth carrying into a log line.
    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cabinet Cloud answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Cabinet Cloud response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Cabinet Cloud response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static CabinetLuaMeta ParseMeta(byte[] body, long appId)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            string Text(string name, int max)
            {
                var value = root.GetProperty(name);
                string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException();
                return text.Length <= max ? text : throw new InvalidDataException();
            }
            if (Text("appid", 10) != appId.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException();
            string sha256 = Text("sha256", 64);
            if (!IsSha256(sha256)) throw new InvalidDataException();
            long size = root.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaxPackageBytes) throw new InvalidDataException();
            var pins = new Dictionary<long, string>();
            var pinsJson = root.GetProperty("pins");
            if (pinsJson.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            foreach (var pin in pinsJson.EnumerateObject())
            {
                if (!Regex.IsMatch(pin.Name, @"\A[1-9][0-9]{0,9}\z") || pin.Value.ValueKind != JsonValueKind.String
                    || !Regex.IsMatch(pin.Value.GetString()!, @"\A[0-9]{1,20}\z"))
                    throw new InvalidDataException();
                pins[long.Parse(pin.Name, CultureInfo.InvariantCulture)] = pin.Value.GetString()!;
            }
            long uploadedAt = root.GetProperty("uploadedAt").GetInt64();
            return new CabinetLuaMeta(appId, sha256, size, pins, Text("source", 64),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(uploadedAt, 0, 253402300799999)), Text("uploader", 200));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed Cabinet lua cache meta.", ex);
        }
    }

    internal static bool IsSha256(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");

    private static string Id(long appId) =>
        appId is > 0 and <= uint.MaxValue ? appId.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(appId));

    public void Dispose() => _http.Dispose();
}
