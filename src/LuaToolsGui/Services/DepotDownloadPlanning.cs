using System.IO;
using LuaTools.Agent;
using LuaToolsGui.ViewModels;
using static LuaToolsGui.ViewModels.BuildsViewModel;
namespace LuaToolsGui.Services;

/// <summary>Same local-only selection, language fallback and size policy as the GUI picker.</summary>
public static class DepotDownloadPlanning
{
    public static List<DepotPickRow> Build(IReadOnlyList<DepotRow> rows, DepotDownloaderService tool, long appId, string? steamLanguage)
    {
        var keys = tool.ResolveKeys(appId);
        var picks = rows
            .Where(r => !r.IsDlc || r.Size > 0)
            .Select(r =>
            {
                // An ACTIVE pin means the user deliberately locked this build, so it wins outright and
                // must never be silently upgraded. Otherwise take the build Steam ships today: a
                // commented-out pin means "Auto Update Apps" is on, i.e. the user wants to track latest,
                // so downloading the build the lua originally shipped with would be the wrong version.
                // The commented pin is a last resort only, for depots with no public manifest at all
                // (beta-branch-only content), where it's the sole version we know of.
                //
                // Safe because depot decryption keys are per-DEPOT and stable across manifest versions:
                // the key already in the lua decrypts the current manifest just as well as the old one.
                string? mid = r.ManifestId ?? r.PublicManifestId ?? r.CommentedManifestId;
                string? path = mid is null ? null : tool.ResolveManifestPath(r.Id, mid);

                // Size, best source first:
                //  1. the manifest itself — authoritative, and describes the exact build being fetched;
                //  2. the lua's setManifestid size, but ONLY when an active pin is what we're downloading,
                //     because that figure belongs to the pinned build rather than the current one;
                //  3. app info, which matches the public branch;
                //  4. the lua's figure anyway, for a depot app info never listed (size would be 0).
                long size =
                    ManifestFile.TryRead(path) is { SizeOnDisk: > 0 } mf ? mf.SizeOnDisk
                    : r.ManifestId is not null && r.LuaSize > 0 ? r.LuaSize
                    : r.Size > 0 ? r.Size
                    : r.LuaSize;

                // All three checks are local — opening the picker costs zero API calls however many
                // depots the game has. Only a depot with no declared version is unreachable outright;
                // a missing manifest is now just a fetch, provided we're signed in to make it.
                // A shared depot has no gid here by design — its manifest lives under the owning app and
                // is resolved at download time, so a missing id is only fatal when there's nowhere to
                // look it up. Both checks stay local; the picker still makes zero requests.
                // The key check matters most for DLC: a DLC depot always has a public manifest id, so the
                // manifest test can never catch one whose key the lua simply doesn't carry — it would be
                // offered, ticked, and then abort the whole download at the first depot.
                //
                // Shared depots are NOT exempt, unlike the manifest test above them. A shared depot's
                // *manifest* is resolved from the owning app at download time, but nothing ever resolves
                // a *key* there — ResolveKeys reads this game's lua and config.vdf and that is all the
                // downloader will ever get. Exempting them here would only move the failure later.
                string? blocked =
                    mid is null && r.FromAppId is null ? Resources.Strings.Builds_Select_NoManifest
                    : !keys.ContainsKey(r.Id) ? Resources.Strings.Builds_Select_NoKey
                    : null;

                var pick = new DepotPickRow
                {
                    DepotId = r.Id,
                    Title = r.Title,
                    Meta = r.Meta,
                    Size = size,
                    ManifestId = mid,
                    ManifestPath = path,
                    Os = r.Os,
                    Language = r.Language,
                    IsDlc = r.IsDlc,
                    FromAppId = r.FromAppId,
                    BlockReason = blocked,
                };
                pick.IsSelected = pick.CanDownload && !pick.IsOtherPlatform && !pick.IsShared
                                  && WantedLanguage(pick.Language, steamLanguage);
                return pick;
            })
            .ToList();

        ApplyNoLanguageMatchFallback(picks);
        return picks;
    }
    private static bool WantedLanguage(string? depotLanguage, string? steamLanguage) =>
        depotLanguage is null
        || depotLanguage.Equals("english", StringComparison.OrdinalIgnoreCase)
        || (steamLanguage is not null
            && depotLanguage.Equals(steamLanguage, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// If the language rule ticked nothing, fall back to ticking every language depot.
    /// </summary>
    /// <remarks>
    /// Only when the selection is COMPLETELY empty. The obvious-looking test — "no language depot got
    /// picked" — is wrong, because plenty of games ship no English depot at all and carry English in
    /// their neutral content, offering language depots purely as extra localisations. Cyberpunk 2077
    /// (10 language depots, none English) and The Witcher 3 (8, none English) both work that way, and
    /// that test would have selected every localisation for them, which is the opposite of the point.
    ///
    /// An empty selection means the game is all language depots and none matched — a Japanese- or
    /// Chinese-only release on a machine whose Steam language it doesn't offer. Taking everything there
    /// errs toward a working install; the user can untick.
    /// </remarks>
    private static void ApplyNoLanguageMatchFallback(IReadOnlyList<DepotPickRow> picks)
    {
        // DLC is excluded from the test on purpose: it declares no language, so it is always selected,
        // and counting it here would mean a game that ships one DLC depot never gets the fallback.
        if (picks.Any(p => p.IsSelected && !p.IsDlc)) return; // neutral content (or a match) covers it

        var languageDepots = picks.Where(p => p.Language is not null).ToList();
        if (languageDepots.Count == 0) return;      // nothing language-specific to fall back to

        foreach (var p in languageDepots)
            p.IsSelected = p.CanDownload && !p.IsOtherPlatform && !p.IsShared;
    }


    public static string ValidateOutput(string path, string? steamRoot)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("OUTPUT_ABSOLUTE_REQUIRED");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Skip(2).Contains(':')) throw new ArgumentException("OUTPUT_DEVICE_OR_REMOTE_FORBIDDEN");
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new ArgumentException("OUTPUT_ROOT_FORBIDDEN");
        foreach (string root in new[] { steamRoot, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(DataPaths.Local, AppConfig.DataDirectoryName), Path.Combine(DataPaths.Roaming, AppConfig.DataDirectoryName) }.OfType<string>().Where(r => r.Length > 0))
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OUTPUT_PROTECTED");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (File.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("OUTPUT_UNSAFE");
            }
        return full;
    }
}
