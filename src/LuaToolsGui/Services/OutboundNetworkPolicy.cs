using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>Fail-closed destination and redirect policy, applied before transport for every request.</summary>
public sealed class OutboundNetworkPolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "lua.tools", "db.lua.tools", "hubcapmanifest.com", "applist.morrenus.xyz",
        "store.steampowered.com", "api.steampowered.com", "api.steamcmd.net",
        "cdn.cloudflare.steamstatic.com", "cdn.akamai.steamstatic.com",
        "shared.cloudflare.steamstatic.com", "shared.akamai.steamstatic.com", "shared.fastly.steamstatic.com",
        "steamcdn-a.akamaihd.net", "avatars.steamstatic.com",
        "github.com", "api.github.com", "raw.githubusercontent.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com"
    };
    private readonly HashSet<int> _loopbackPorts;
    private readonly bool _dns;
    private readonly string? _externalOrigin;
    internal bool IsExternalData => _externalOrigin is not null;

    internal OutboundNetworkPolicy(IEnumerable<int>? loopbackPorts = null, bool dns = false, Uri? externalDestination = null)
    {
        _loopbackPorts = new(loopbackPorts ?? []);
        _dns = dns;
        if (externalDestination is not null)
        {
            if (!externalDestination.IsAbsoluteUri || externalDestination.Scheme != Uri.UriSchemeHttps
                || externalDestination.Port != 443 || !string.IsNullOrEmpty(externalDestination.UserInfo)
                || !string.IsNullOrEmpty(externalDestination.Fragment)
                || externalDestination.HostNameType != UriHostNameType.Dns
                || !externalDestination.IdnHost.Contains('.') || externalDestination.IsLoopback
                || externalDestination.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || externalDestination.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("External data destination must be a public HTTPS DNS origin.");
            _externalOrigin = externalDestination.GetLeftPart(UriPartial.Authority);
        }
    }

    public void Validate(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new HttpRequestException("Destination is not permitted by the outbound network policy.");
        if (IsExternalData)
        {
            if (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
                && uri.GetLeftPart(UriPartial.Authority).Equals(_externalOrigin, StringComparison.OrdinalIgnoreCase)) return;
        }
        else if (_loopbackPorts.Count > 0)
        {
            if (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "127.0.0.1" || uri.Host == "[::1]")
                && _loopbackPorts.Contains(uri.Port)) return;
        }
        else if (!_dns && uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost == "uploads.online-fix.me"
            && uri.Port == 2053 && uri.AbsolutePath.StartsWith("/uploads/", StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query)) return;
        else if (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443)
        {
            if (_dns)
            {
                if (uri.Host == "1.1.1.1" && uri.AbsolutePath == "/dns-query") return;
            }
            else
            {
                if (uri.IdnHost is "ghproxy.net" or "ghfast.top" or "gh.ddlc.top")
                {
                    ValidateMirrorTarget(uri, uri.AbsolutePath.TrimStart('/'), api: false);
                    return;
                }
                if (uri.IdnHost == "lua.tools" && uri.AbsolutePath.StartsWith("/api/gh/", StringComparison.Ordinal))
                    ValidateMirrorTarget(uri, uri.AbsolutePath["/api/gh/".Length..], api: true);
                if (uri.IdnHost == new Uri(AppConfig.SteamMlCdnUrl).IdnHost
                    && string.IsNullOrEmpty(uri.Query) && uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)
                    && ApiContract.IsAppId(uri.AbsolutePath[1..^4])) return;
                if (uri.IdnHost == new Uri(AppConfig.DexLuaBaseUrl).IdnHost
                    && uri.AbsolutePath == "/api/download" && uri.Query.StartsWith("?id=", StringComparison.Ordinal)
                    && ApiContract.IsAppId(uri.Query[4..])) return;
                if (Hosts.Contains(uri.IdnHost)) return;
            }
        }
        // Do not include signed queries, tokens or arbitrary supplied URLs in exceptions/logs.
        throw new HttpRequestException("Destination is not permitted by the outbound network policy.");
    }
    private static bool IsPublicPaginationQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string parameter in query.TrimStart('?').Split('&'))
        {
            var parts = parameter.Split('=');
            if (parts.Length != 2 || parts[0] is not ("page" or "per_page") || !seen.Add(parts[0])
                || parts[1].Length is < 1 or > 3 || parts[1].Any(c => c is < '0' or > '9')
                || !int.TryParse(parts[1], out int value) || value < 1) return false;
        }
        return true;
    }

    private static void ValidateMirrorTarget(Uri mirror, string nestedUrl, bool api)
    {
        if ((api ? !IsPublicPaginationQuery(mirror.Query) : !string.IsNullOrEmpty(mirror.Query))
            || !Uri.TryCreate(nestedUrl, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps || target.Port != 443
            || !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query)
            || !string.IsNullOrEmpty(target.Fragment)
            || (api ? target.IdnHost != "api.github.com"
                : target.IdnHost is not ("github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com")))
            throw new HttpRequestException("Proxy destination is outside the original public GitHub data scope.");
    }

}

internal sealed class OutboundPolicyHandler(OutboundNetworkPolicy policy, HttpMessageHandler transport, HttpResponseBudget? defaultBudget = null, HttpCredentialPurpose? defaultCredentialPurpose = null) : DelegatingHandler(transport)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        policy.Validate(request.RequestUri);
        if (request.Headers.Host is not null)
            throw new HttpRequestException("Overriding the destination Host header is prohibited.");
        if (request.Headers.Contains(LocalPluginBridgePolicy.TokenHeader)
            && (request.RequestUri!.Scheme != Uri.UriSchemeHttp || request.RequestUri.Host != "127.0.0.1"
                || request.RequestUri.Port != LocalPluginBridgePolicy.Port || request.RequestUri.UserInfo.Length != 0))
            throw new HttpRequestException("Local bridge credentials are not permitted for this destination.");
        if (policy.IsExternalData)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head || request.Content is not null
                || request.Headers.Any(h => !h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                    && !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                    && !h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)))
                throw new HttpRequestException("External data requests cannot carry account credentials or custom headers.");
        }
        var budget = request.Options.TryGetValue(HttpResponseBudget.RequestOption, out var specifiedBudget)
            ? specifiedBudget : defaultBudget ?? HttpResponseBudget.Json;
        budget.Validate();
        var current = request;
        try
        {
            for (int hop = 0; ; hop++)
            {
                if (!policy.IsExternalData && (current.Headers.Authorization is not null
                    || current.Headers.Contains("apikey") || current.Headers.Contains("Cookie")
                    || HasCredentialQuery(current.RequestUri!)))
                {
                    var purpose = current.Options.TryGetValue(HttpCredentialPurposeExtensions.RequestOption, out var explicitPurpose)
                        ? explicitPurpose : defaultCredentialPurpose;
                    HttpCredentialPurposeExtensions.Validate(current, purpose);
                }
                var response = await base.SendAsync(current, ct);
                int status = (int)response.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308))
                {
                    try { response.Content = new BoundedResponseContent(response.Content, budget, ct); }
                    catch { response.Dispose(); throw; }
                    return response;
                }
                try
                {
                    if (current.Headers.Contains(LocalPluginBridgePolicy.TokenHeader))
                        throw new HttpRequestException("Local bridge redirects are prohibited.");
                    if (policy.IsExternalData) throw new HttpRequestException("External data redirects are prohibited.");
                    if (hop >= 5 || response.Headers.Location is null)
                        throw new HttpRequestException("Invalid or excessive redirect.");
                    var target = new Uri(current.RequestUri!, response.Headers.Location);
                    policy.Validate(target);
                    // Never replay credential-bearing bodies; auth/token endpoints must respond directly.
                    if (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head || current.Content is not null)
                        throw new HttpRequestException("Redirects of requests with a body are prohibited.");
                    bool sameOrigin = current.RequestUri!.GetLeftPart(UriPartial.Authority)
                        .Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
                    if (!sameOrigin && (HasCredentialQuery(current.RequestUri!)
                        || current.Headers.Authorization is not null || current.Headers.Contains("apikey")
                        || current.Headers.Contains("Cookie")))
                        throw new HttpRequestException("Redirect of a credential-bearing query is prohibited.");
                    var next = new HttpRequestMessage(current.Method, target);
                    foreach (var option in current.Options) next.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                    foreach (var header in current.Headers)
                    {
                        if (sameOrigin || header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                            || header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                            || header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    if (!ReferenceEquals(current, request)) current.Dispose();
                    current = next;
                }
                finally { response.Dispose(); }
            }
        }
        finally { if (!ReferenceEquals(current, request)) current.Dispose(); }
    }
    private static bool HasCredentialQuery(Uri uri)
    {
        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
        {
            string key = Uri.UnescapeDataString(parameter.Split('=')[0]);
            if (key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                || key.Equals("token", StringComparison.OrdinalIgnoreCase)
                || key.Equals("access_token", StringComparison.OrdinalIgnoreCase)
                || key.Equals("refresh_token", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

}
