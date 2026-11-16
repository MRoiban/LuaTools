using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>How well a lua's depot keys cover what Steam installs for the game.</summary>
public enum KeyCoverageVerdict
{
    /// <summary>Every depot Steam would install for the base game on Windows has a key.</summary>
    Complete,
    /// <summary>The main depot has a key, but some smaller ones don't: parts of the game may not download.</summary>
    Partial,
    /// <summary>The game's main (largest) depot has no key: Steam cannot download the game from this lua.</summary>
    MissingMain,
    /// <summary>Steam's depot list is unavailable, so nothing can be said either way.</summary>
    Unverifiable,
}

/// <summary>The result of <see cref="LuaKeyCoverage.Check"/>.</summary>
/// <param name="MainDepot">The depot Steam needs most (the largest one), when the depot list names one.</param>
/// <param name="Missing">The depots Steam would install that have no key, largest first.</param>
public sealed record KeyCoverage(KeyCoverageVerdict Verdict, long? MainDepot, IReadOnlyList<long> Missing)
{
    /// <summary>Worth installing: everything except a lua without the main depot's key.</summary>
    public bool Usable => Verdict != KeyCoverageVerdict.MissingMain;

    /// <summary>Worth sharing with other users: only a lua known to cover every depot.</summary>
    public bool Shareable => Verdict == KeyCoverageVerdict.Complete;

    public static KeyCoverage Unverifiable { get; } = new(KeyCoverageVerdict.Unverifiable, null, []);
}

/// <summary>
/// Checks that a downloaded lua carries the decryption keys Steam needs, against Steam's own depot list.
/// </summary>
/// <remarks>
/// <para>
/// The install policy (<see cref="ManifestContentPolicy"/>) only checks a lua's grammar. Some public
/// sources produce grammatical luas that can't download the game: DexLua picks keys by appid number
/// (Slay the Spire 2's script carries keys for a neighbouring game and none for its own depot), and
/// SteamML mirrors snapshots that predate depots added since. Steam needs a key per depot, so a lua
/// without the main depot's key installs "successfully" and then never downloads.
/// </para>
/// <para>
/// The depots that count are the base game's own depots Steam ships on the public branch for Windows
/// with no language (DLC depots, shared redistributables, language packs and other OSes are left out).
/// The rule is two-level because generators legitimately differ on minor depots: on a test set of 35
/// Hubcap/Morrenus luas every one had the largest depot's key, while 2 older snapshots lacked a
/// smaller depot added later. So only a missing main depot makes a lua unusable; any other gap is
/// <see cref="KeyCoverageVerdict.Partial"/> (install, but don't share it with others).
/// </para>
/// <para>
/// A key's VALUE can't be verified without decrypting game data, so this checks presence only. Across
/// the same test set no two sources ever disagreed on a key both carried: missing keys are the failure.
/// </para>
/// </remarks>
public static partial class LuaKeyCoverage
{
    // An ACTIVE addappid with a 64-hex key, in the declaration grammar of ManifestContentPolicy. Commented
    // lines never count: OpenSteamTool only reads executed calls, and keeps only 64-character keys.
    [GeneratedRegex("""^addappid[ \t]*\([ \t]*([1-9][0-9]{0,9})[ \t]*,[ \t]*[01][ \t]*,[ \t]*(?:"([0-9a-fA-F]{64})"|'([0-9a-fA-F]{64})')[ \t]*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyLine();

    /// <summary>The ids an ACTIVE line of the lua gives a 64-hex key.</summary>
    public static IReadOnlySet<long> ReadKeyedIds(string lua)
    {
        var ids = new HashSet<long>();
        foreach (string raw in lua.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("--", StringComparison.Ordinal)) continue;
            if (KeyLine().Match(line) is { Success: true } m && long.TryParse(m.Groups[1].Value, out long id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The depots Steam installs for the base game on 64-bit Windows by default: its own (not shared, not
    /// DLC), for Windows or every OS, without a language, shipped on the public branch, and not a variant
    /// only some installs pick (32-bit, low-violence, Steam China). Generators key only what is installed,
    /// so a variant counted here could reject a correct file.
    /// </summary>
    public static IReadOnlyList<ContentDepot> RequiredDepots(AppDepotInfo info) =>
        info.Depots.Where(d => !d.IsDlc && !d.IsShared && d.PublicManifestId is not null
                && (string.IsNullOrEmpty(d.Os) || d.Os.Contains("windows", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrEmpty(d.Language)
                && d.Arch != "32" && !d.LowViolence && string.IsNullOrEmpty(d.Realm))
            .OrderByDescending(d => d.Size).ThenBy(d => d.Id).ToList();

    /// <summary>Checks a lua's keys against Steam's depot list (null: unavailable).</summary>
    public static KeyCoverage Check(string lua, AppDepotInfo? info)
    {
        if (info is null) return KeyCoverage.Unverifiable;
        var required = RequiredDepots(info);
        // Nothing to hold a lua to (unreleased, token-gated, every depot a language pack): it may install,
        // but nothing vouches for it, so it is never shared.
        if (required.Count == 0) return KeyCoverage.Unverifiable;
        var keyed = ReadKeyedIds(lua);
        var missing = required.Where(d => !keyed.Contains(d.Id)).Select(d => d.Id).ToList();
        long main = required[0].Id;
        // With no sizes at all there is no "main" depot: one keyed depot is the least a lua must carry.
        bool mainMissing = required[0].Size > 0 ? missing.Contains(main) : missing.Count == required.Count;
        var verdict = mainMissing ? KeyCoverageVerdict.MissingMain
            : missing.Count > 0 ? KeyCoverageVerdict.Partial : KeyCoverageVerdict.Complete;
        return new(verdict, required[0].Size > 0 ? main : null, missing);
    }

    /// <summary>
    /// The lua text of a downloaded file: the file itself, or the single <c>.lua</c> entry of a zip.
    /// Null when there isn't exactly one readable lua (the install path reports that case itself).
    /// </summary>
    public static string? ReadLua(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4)
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var luas = zip.Entries.Where(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
                if (luas.Count != 1) return null;
                using var stream = luas[0].Open();
                bytes = ManifestContentPolicy.ReadBounded(stream, ManifestContentPolicy.MaxLuaBytes);
            }
            else if (bytes.Length > ManifestContentPolicy.MaxLuaBytes) return null;
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { return null; }
    }

    /// <summary><see cref="ReadLua(byte[])"/> for a staged file.</summary>
    public static string? ReadLuaFile(string path)
    {
        try
        {
            ManifestContentPolicy.EnsureNoReparsePoints(path);
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            bool zip = stream.Read(head) == 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
            stream.Position = 0;
            if (!zip) return ReadLua(ManifestContentPolicy.ReadBounded(stream, ManifestContentPolicy.MaxLuaBytes));
            // A package can be tens of MB of manifests: read only its lua entry, not the whole file.
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var luas = archive.Entries.Where(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToList();
            if (luas.Count != 1) return null;
            using var entry = luas[0].Open();
            return new UTF8Encoding(false, true).GetString(ManifestContentPolicy.ReadBounded(entry, ManifestContentPolicy.MaxLuaBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException) { return null; }
    }
}
