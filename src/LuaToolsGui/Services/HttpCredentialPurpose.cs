using System.Net.Http;

namespace LuaToolsGui.Services;

public enum HttpCredentialPurpose { LuaToolsApi, Supabase, Hubcap, OnlineFixMe }

public static class HttpCredentialPurposeExtensions
{
    internal static readonly HttpRequestOptionsKey<HttpCredentialPurpose> RequestOption = new("LuaTools.CredentialPurpose");
    public static void Apply(this HttpCredentialPurpose purpose, HttpRequestMessage request) =>
        request.Options.Set(RequestOption, purpose);

    internal static void Validate(HttpRequestMessage request, HttpCredentialPurpose? purpose)
    {
        var uri = request.RequestUri!;
        string path = uri.AbsolutePath;
        bool get = request.Method == HttpMethod.Get;
        bool allowed = purpose switch
        {
            HttpCredentialPurpose.LuaToolsApi => uri.IdnHost == "lua.tools" && get
                && (path is "/api/me/supporter-status" or "/api/dlc/info" or "/api/manifest/download"
                    or "/api/dlc/generate" or "/api/denuvo/download"
                    || path.StartsWith("/api/givemethemanifestpunk/", StringComparison.Ordinal)
                        && path.Split('/') is ["", "api", "givemethemanifestpunk", var depot, var manifest]
                        && IsDigits(depot) && IsDigits(manifest))
                && !request.Headers.Contains("apikey") && !request.Headers.Contains("Cookie"),
            HttpCredentialPurpose.Supabase => uri.IdnHost == "db.lua.tools"
                && (request.Method == HttpMethod.Post && path is "/auth/v1/token" or "/auth/v1/verify"
                    && request.Headers.Authorization is null
                    || request.Method == HttpMethod.Head && path == "/rest/v1/user_downloads")
                && !request.Headers.Contains("Cookie"),
            HttpCredentialPurpose.Hubcap => uri.IdnHost == "hubcapmanifest.com" && get
                && (path == "/api/v1/user/stats" || path.StartsWith("/api/v1/status/", StringComparison.Ordinal)
                    && IsDigits(path["/api/v1/status/".Length..])
                    || path.StartsWith("/api/v1/manifest/", StringComparison.Ordinal)
                    && IsDigits(path["/api/v1/manifest/".Length..]))
                && !request.Headers.Contains("apikey") && !request.Headers.Contains("Cookie"),
            HttpCredentialPurpose.OnlineFixMe => get && uri.Scheme == Uri.UriSchemeHttps
                && uri.IdnHost == "uploads.online-fix.me" && uri.Port == 2053
                && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
                && path.Split('/') is ["", "uploads", var folder, "Fix%20Repair", var file]
                && OnlineFixMe.OnlineFixMeListing.DecodeSegment(folder) is not null
                && OnlineFixMe.OnlineFixMeListing.DecodeSegment(file) is not null
                && request.Headers.Contains("Cookie")
                && request.Headers.Authorization is null && !request.Headers.Contains("apikey"),
            _ => false
        };
        if (!allowed) throw new HttpRequestException("Credentials are not permitted for this request purpose and route.");
    }

    private static bool IsDigits(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
}
