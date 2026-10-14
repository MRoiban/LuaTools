using System.Net.Http;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>Unauthenticated legacy availability hints; never a download URL or execution authority.</summary>
internal static class SourceAvailabilityClient
{
    internal const string Endpoint = "http://167.235.229.108/check_apis";
    private static readonly HttpClient Http = AppHttp.CreateSourceAvailability();

    internal static async Task<Dictionary<string, string>> CheckAsync(string appid, CancellationToken ct,
        HttpClient? client = null)
    {
        ApiContract.RequireAppId(appid);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "?appid=" + appid);
        request.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await (client ?? Http).SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 4 });
            return Parse(json.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            throw new ApiException("Source availability could not be checked.");
        }
    }

    internal static Dictionary<string, string> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid source catalogue.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            // These names also appear in the plugin's page UI: reject markup/control/query syntax.
            if (result.Count >= 32 || name.Length is < 1 or > 64 || name != name.Trim()
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '(' or ')' or '-' or '_' or '.')
                || property.Value.ValueKind != JsonValueKind.String || result.ContainsKey(name))
                throw new JsonException("Invalid source catalogue.");
            var status = property.Value.GetString();
            if (status is not ("available" or "unavailable" or "unknown"))
                throw new JsonException("Invalid availability status.");
            result.Add(name, status);
        }
        return result;
    }
}

internal sealed class SourceAvailabilityHandler(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    private static readonly HttpResponseBudget Budget = new(64 * 1024, TimeSpan.FromSeconds(10));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.GetLeftPart(UriPartial.Path) != SourceAvailabilityClient.Endpoint
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.Query.StartsWith("?appid=", StringComparison.Ordinal)
            || !ApiContract.IsAppId(uri.Query[7..]) || request.Method != HttpMethod.Get || request.Content is not null
            || request.Headers.Any(h => h.Key is not ("User-Agent" or "Accept"))
            || request.Headers.Accept.ToString() != "application/json"
            || !request.Headers.TryGetValues("User-Agent", out var agents)
            || !agents.SequenceEqual(new[] { AppConfig.ManifestBackendUserAgent }))
            throw new HttpRequestException("Only the credential-free source availability lookup is permitted.");
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Source availability redirects are prohibited.");
            response.Content = new BoundedResponseContent(response.Content, Budget, ct);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
