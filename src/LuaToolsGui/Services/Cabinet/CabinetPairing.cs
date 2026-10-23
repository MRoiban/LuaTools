using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.Cabinet;

internal enum CabinetConnectInputKind { Invalid, Code, Link }
internal enum CabinetConnectFailure { InvalidCode, TooManyAttempts, Unreachable, NotHttps, Server, Config }

internal sealed class CabinetConnectException(CabinetConnectFailure failure) : Exception(failure switch
{
    CabinetConnectFailure.InvalidCode => Resources.Strings.CabinetCloud_InvalidCode,
    CabinetConnectFailure.TooManyAttempts => Resources.Strings.CabinetCloud_TooManyAttempts,
    CabinetConnectFailure.Unreachable => Resources.Strings.CabinetCloud_Unreachable,
    CabinetConnectFailure.NotHttps => Resources.Strings.CabinetCloud_NotHttps,
    CabinetConnectFailure.Config => Resources.Strings.CabinetCloud_ConfigError,
    _ => Resources.Strings.CabinetCloud_ConnectError
})
{
    internal CabinetConnectFailure Failure { get; } = failure;
}

internal static class CabinetConnectInput
{
    internal static bool TryNormalizeCode(string? input, out string code)
    {
        var result = new StringBuilder();
        foreach (char c in (input ?? "").ToUpperInvariant())
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            result.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        code = result.ToString();
        return Regex.IsMatch(code, "^[0-9A-HJKMNP-TV-Z]{12}$");
    }

    internal static string FormatCode(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    internal static CabinetConnectInputKind Classify(string input)
    {
        if (TryNormalizeCode(input, out _)) return CabinetConnectInputKind.Code;
        try { CabinetEngine.CloudClient.ParseLink(input.Trim(), out _, out _); return CabinetConnectInputKind.Link; }
        catch (FormatException) { return CabinetConnectInputKind.Invalid; }
    }

    internal static string NormalizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return AppConfig.CabinetDefaultServer;
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CabinetConnectException(CabinetConnectFailure.NotHttps);
        if (uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new FormatException(Resources.Strings.CabinetCloud_InvalidLink);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    internal static string DeviceName(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? "Windows PC" : trimmed[..Math.Min(100, trimmed.Length)];
    }
}

internal sealed class CabinetPairingClient : IDisposable
{
    private readonly HttpClient _http;

    // Cabinet accepts a user-selected HTTPS origin, just like the engine's existing transport.
    // Do not redirect pairing credentials or reuse any of the app's account headers/cookies.
    internal CabinetPairingClient(HttpMessageHandler? transport = null)
    {
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 64 * 1024
        };
    }

    internal async Task<string> RedeemAsync(string server, string input, CancellationToken ct = default)
    {
        server = CabinetConnectInput.NormalizeServer(server);
        if (!CabinetConnectInput.TryNormalizeCode(input, out string code))
            throw new CabinetConnectException(CabinetConnectFailure.InvalidCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, server + "/api/pair");
        request.Headers.UserAgent.ParseAdd("LuaTools/" + typeof(AppConfig).Assembly.GetName().Version?.ToString(3) + " (Windows)");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            code, deviceName = CabinetConnectInput.DeviceName(Environment.MachineName)
        }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new CabinetConnectException(CabinetConnectFailure.InvalidCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new CabinetConnectException(CabinetConnectFailure.TooManyAttempts);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CabinetConnectException(CabinetConnectFailure.Server);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = json.RootElement;
            if (!root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(token.GetString()!, @"\Acab_[A-Za-z0-9_-]{43}\z")
                || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !user.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new CabinetConnectException(CabinetConnectFailure.Server);
            return token.GetString()!;
        }
        catch (HttpRequestException) { throw new CabinetConnectException(CabinetConnectFailure.Unreachable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CabinetConnectException(CabinetConnectFailure.Unreachable); }
        catch (JsonException) { throw new CabinetConnectException(CabinetConnectFailure.Server); }
        catch (InvalidOperationException) { throw new CabinetConnectException(CabinetConnectFailure.Server); }
    }

    public void Dispose() => _http.Dispose();
}
