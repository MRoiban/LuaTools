using System.Text.RegularExpressions;
using LuaToolsGui.ViewModels;
namespace LuaToolsGui.Services;
public sealed record DepotSections(IReadOnlyList<DepotRow> InLua, IReadOnlyList<DepotRow> Missing, IReadOnlyList<DepotRow> Unknown);
public static partial class DepotProjection
{
    private static void AddDeclaredButUnlisted(
        List<ContentDepot> items, Dictionary<long, LuaEntry> declared, long baseAppId,
        HashSet<long> depotDlcIds)
    {
        var known = items.Select(d => d.Id).ToHashSet();

        foreach (var (id, entry) in declared)
        {
            // Already covered, either as a real depot or as the DLC app id behind one.
            if (known.Contains(id) || depotDlcIds.Contains(id)) continue;

            // The base app's own addappid line is the app key, not a depot — downloading it is meaningless.
            if (id == baseAppId) continue;

            // A key means content; without one it's a store entitlement, which is what DlcAppId marks.
            bool isDepot = entry.HasKey;

            // Shared redistributables are only identifiable from the lua's comment here, and it matters:
            // FromAppId is what keeps them off the default selection (they're usually already installed).
            long? fromAppId = isDepot ? SharedOwnerFromComment(entry.Comment) : null;

            // The lua's own setManifestid size, when it has one: without it these rows report 0 bytes,
            // which understates the download total and leaves the free-space warning blind.
            items.Add(new ContentDepot(id, entry.SizeOnDisk ?? 0, isDepot ? null : id,
                fromAppId is not null,
                Os: null, Language: isDepot ? LanguageFromComment(entry.Comment) : null)
            { FromAppId = fromAppId });
        }
    }

    /// <summary>
    /// Steam's language codes, as they appear in a depot's <c>config.language</c>. Used to recognise a
    /// synthesized depot's language from its lua comment, which is the only clue available when Steam
    /// withheld the app info — without it a "Schinese" depot reads as language-neutral and is selected
    /// for everyone.
    /// </summary>
    private static readonly HashSet<string> SteamLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "arabic", "bulgarian", "schinese", "tchinese", "czech", "danish", "dutch", "english", "finnish",
        "french", "german", "greek", "hungarian", "indonesian", "italian", "japanese", "koreana",
        "norwegian", "polish", "portuguese", "brazilian", "romanian", "russian", "spanish", "latam",
        "swedish", "thai", "turkish", "ukrainian", "vietnamese",
    };

    /// <summary>The Steam language code a lua comment names, or null if it names something else.</summary>
    private static string? LanguageFromComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        foreach (string word in comment.Split([' ', '	', '(', ')', ',', '-', ':'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SteamLanguageCodes.TryGetValue(word, out string? code)) return code.ToLowerInvariant();
        return null;
    }

    /// <summary>Owning app id from a lua comment like "VC 2019 Redist (Shared from App 228980)".</summary>
    private static long? SharedOwnerFromComment(string? comment) =>
        comment is not null && SharedFromAppRegex().Match(comment) is { Success: true } m
        && long.TryParse(m.Groups[1].Value, out long owner)
            ? owner
            : null;

    [GeneratedRegex(@"Shared\s+from\s+App\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SharedFromAppRegex();

    public static Dictionary<long, LuaEntry> Declarations(LuaContents? lua)
    {
        var all = new Dictionary<long, LuaEntry>();
        if (lua is null) return all;
        foreach (var e in lua.DisabledEntries) all[e.Id] = e;
        foreach (var e in lua.Entries) all[e.Id] = e;   // an active line wins over a commented one
        return all;
    }

    public static DepotSections BuildRows(AppDepotInfo info, LuaContents? lua, Func<long, string?> name)
    {
        var declared = Declarations(lua);
        var active = lua?.Entries.Select(e => e.Id).ToHashSet() ?? [];
        long baseAppId = lua?.BaseAppId ?? info.AppId;
        var luaNames = declared.Where(kv => kv.Value.Comment is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Comment!);

        // Unified comparison set: every real depot + every declared DLC that has no depot of its own
        // (store-only entitlements). Without the latter, keyless entitlement DLC would vanish.
        var items = new List<ContentDepot>(info.Depots);
        var depotDlcIds = info.Depots.Where(d => d.DlcAppId is not null).Select(d => d.DlcAppId!.Value).ToHashSet();
        foreach (long dlcId in info.DlcIds)
            if (!depotDlcIds.Contains(dlcId))
                items.Add(new ContentDepot(dlcId, 0, dlcId, IsShared: false, Os: null, Language: null));

        AddDeclaredButUnlisted(items, declared, baseAppId, depotDlcIds);

        bool DlcNameKnown(long dlcId) =>
            name(dlcId) is not null || luaNames.ContainsKey(dlcId);

        DepotRow Row(ContentDepot d)
        {
            // Prefer a real Steam name; fall back to the lua comment (e.g. "VC 2022 Redist").
            string? steamName = d.DlcAppId is { } dlcId ? name(dlcId) : null;
            string title =
                steamName
                ?? luaNames.GetValueOrDefault(d.Id)
                ?? (d.IsDlc ? string.Format(Resources.Strings.Manage_DlcName, d.DlcAppId)
                    : d.IsShared ? Resources.Strings.Manage_SharedDepot : Resources.Strings.Manage_Depot);

            var meta = new List<string> { d.Id.ToString() };
            if (d.Size > 0) meta.Add(FormatSize(d.Size));
            if (!string.IsNullOrWhiteSpace(d.Os)) meta.Add(PrettyOs(d.Os));
            if (!string.IsNullOrWhiteSpace(d.Language)) meta.Add(d.Language!);

            string url = d.DlcAppId is { } dlc
                ? $"https://steamdb.info/app/{dlc}/"
                : $"https://steamdb.info/depot/{d.Id}/";

            // The switches act on the id the lua actually DECLARES. For a DLC that's the DLC app id, not
            // the depot id. Toggling the depot id would rewrite a line that doesn't exist.
            long declId = declared.ContainsKey(d.Id) ? d.Id : d.DlcAppId ?? d.Id;
            declared.TryGetValue(declId, out var entry);
            bool inLua = entry is not null;

            return new DepotRow(d.Id, title, string.Join("  ·  ", meta), d.IsDlc, d.IsShared, url,
                entry?.ManifestId, entry?.CommentedManifestId, d.PublicManifestId,
                IsInLua: inLua,
                IsEnabled: active.Contains(declId),
                CanToggle: inLua,   // anything the lua declares can be switched, in any variant
                IsBaseApp: declId == baseAppId)
            {
                ToggleId = declId, Size = d.Size, Os = d.Os, Language = d.Language,
                FromAppId = d.FromAppId, LuaSize = entry?.SizeOnDisk ?? 0,
            };
        }

        // In lua = the lua declares this id (a keyed depot OR a keyless DLC entitlement) or its DLC app
        // id, including declarations the user has switched off, so they can switch them back on.
        bool IsInLua(ContentDepot d) => declared.ContainsKey(d.Id) || (d.DlcAppId is { } a && declared.ContainsKey(a));

        // Unknown = noise to tuck away: shared redists, unnamed DLC, or 0-byte/broken depots.
        bool IsUnknown(ContentDepot d) =>
            d.IsShared
            || (d.IsDlc && !DlcNameKnown(d.DlcAppId!.Value))
            || (!d.IsDlc && d.Size == 0);

        return new(items.Where(IsInLua).Select(Row).ToList(),
            items.Where(d => !IsInLua(d) && !IsUnknown(d)).Select(Row).ToList(),
            items.Where(d => !IsInLua(d) && IsUnknown(d)).Select(Row).ToList());
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        double mb = bytes / 1024d / 1024d;
        return $"{mb:0.#} MB";
    }

}
