namespace LuaToolsGui;

/// <summary>
/// Compiled-in client configuration. The Supabase URL and anon key are public
/// client values (they also ship in the lua.tools web bundle).
/// </summary>
public static class AppConfig
{
    // Isolated from upstream tokens, settings and executable caches.
    public const string DataDirectoryName = "LuaToolsGui-SecurityHardened";
    public const string SupabaseUrl = "https://db.lua.tools";

    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzYwMzkzNzYsImV4cCI6MTg5MzQ1NjAwMCwicm9sZSI6ImFub24iLCJpc3MiOiJzdXBhYmFzZSJ9.f_-K38u3odjltP-g_67FVmG32Vg-_-k-lNBvIaVUVBM";

    public const string ApiBaseUrl = "https://lua.tools";

    // Bot-provisioned (Discord /login placeholder) accounts use this email domain. Detecting it on
    // startup lets the app prompt the user to re-link their full lua.tools account.
    public const string BotAccountEmailDomain = "@bot.lua.tools";

    // Hubcap (hubcapmanifest.com): the app talks to this directly with the user's own API key
    // (no lua.tools proxy). Key + stats are managed in Settings; key-gated source downloads hit it.
    public const string HubcapBaseUrl = "https://hubcapmanifest.com";

    /// <summary>Must be registered in Supabase Auth → Redirect URLs.</summary>
    public const int OAuthCallbackPort = 53789;
    public const string OAuthCallbackUrl = "http://localhost:53789/callback";

    // The standard lua.tools daily download cap (Hubcap-keyed downloads are exempt). Hardcoded here
    // because the web app enforces it inline with no API field exposing it; change in one place if it moves.
    public const int DailyDownloadLimit = 25;

    // Public upstream APIs the app calls directly (no lua.tools proxy needed for guest browsing).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    // Steam's storefront "featured categories" (top sellers, new releases, etc.). Drives the Add page's
    // featured strips. Public, no auth.
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets). Fetched
    // via GithubProxy (raw.githubusercontent.com → mirror fallback) and cached ~14 days, to filter
    // hardware out of featured/search. Array of { "appid": <long>, "name": ... } objects.
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Original release repositories remain available for inert acquisition; execution requires independent review.
    public const string SteamlessRepo = "atom0s/Steamless";
    public const string CloudRedirectRepo = "Selectively11/CloudRedirect";
    public const string SteamAutoCrackRepo = "SteamAutoCracks/Steam-auto-crack";
    public const string DepotDownloaderRepo = "mendy-tools/DepotDownloaderMod";
    public const string ManifestBackendUrl = "";
    public const string ManifestBackendUserAgent = "secretgoonpoon";

    public const string PluginReleasesOwner = "madoiscool";
    public const string PluginReleasesRepo = "LTSP";

    // Original capability-matched proxy origins. All returned metadata/artifacts remain untrusted.
    public static readonly string[] GithubApiMirrors = ["https://lua.tools/api/gh/"];
    public static readonly string[] GithubDownloadMirrors =
        ["https://ghproxy.net/", "https://ghfast.top/", "https://gh.ddlc.top/"];
}
