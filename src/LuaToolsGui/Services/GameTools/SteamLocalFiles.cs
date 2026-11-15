using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CabinetEngine;
using Microsoft.Win32;

namespace LuaToolsGui.Services.GameTools;

/// <summary>Thrown when Steam starts while a closed-Steam write is in progress; nothing is committed.</summary>
public sealed class SteamStartedException() : IOException("Steam started during the write.");

/// <summary>Backup and atomic replace for files inside the Steam folder.</summary>
internal static class SteamFileWriter
{
    /// <summary>One-time copy of the original, as Cabinet does: <c>&lt;backupDir&gt;\&lt;name&gt;</c> is never
    /// overwritten, so it keeps the file as it was before LuaTools or Cabinet first changed it.</summary>
    internal static void BackupOnce(string path, string backupDir, string name)
    {
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(backupDir);
        string backup = Path.Combine(backupDir, name);
        if (File.Exists(backup)) return;
        string tmp = backup + ".tmp";
        File.Copy(path, tmp, overwrite: true);
        File.Move(tmp, backup);
    }

    /// <summary>Write next to the target, flush to disk, then swap it in. <paramref name="steamRunning"/>
    /// is checked before writing and again just before the swap (Steam rewrites these files on exit).</summary>
    internal static void WriteAtomic(string path, byte[] bytes, Func<bool>? steamRunning = null)
    {
        if (steamRunning?.Invoke() == true) throw new SteamStartedException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".luatools.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (steamRunning?.Invoke() == true) throw new SteamStartedException();
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }
}

/// <summary>localconfig.vdf playtime ("Playtime" is minutes), edited in place with Cabinet's VdfText so the
/// rest of Steam's file is unchanged.</summary>
internal static class PlaytimeConfig
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    internal static int? GetMinutes(string vdf, long appId) =>
        int.TryParse(new VdfText(vdf).GetAppValue(appId.ToString(CultureInfo.InvariantCulture), "Playtime"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : null;

    /// <summary>New file text, or null when the file has no apps section to write into. The last-two-weeks
    /// figure is capped at the new total so Steam never shows more recent time than total time.</summary>
    internal static string? SetMinutes(string vdf, long appId, int minutes)
    {
        string id = appId.ToString(CultureInfo.InvariantCulture);
        minutes = Math.Max(0, minutes);
        var text = new VdfText(vdf);
        if (!text.SetAppValue(id, "Playtime", minutes.ToString(CultureInfo.InvariantCulture))) return null;
        if (int.TryParse(text.GetAppValue(id, "Playtime2wks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int recent) && recent > minutes)
            text.SetAppValue(id, "Playtime2wks", minutes.ToString(CultureInfo.InvariantCulture));
        return text.Text;
    }

    internal static (string Text, bool Bom) Decode(byte[] bytes)
    {
        bool bom = bytes.AsSpan().StartsWith(Utf8Bom);
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    internal static byte[] Encode(string text, bool bom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        return bom ? [.. Utf8Bom, .. body] : body;
    }
}

/// <summary>The Steam account whose files the Tools page edits.</summary>
internal static partial class SteamAccounts
{
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>The logged-in user while Steam runs (ActiveProcess\ActiveUser), else the most recent login.</summary>
    internal static uint? Current(string steamPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int active && active != 0) return unchecked((uint)active);
        }
        catch { /* unreadable registry: fall back to loginusers.vdf */ }
        try
        {
            string path = Path.Combine(steamPath, "config", "loginusers.vdf");
            return File.Exists(path) ? FromLoginUsers(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The user marked MostRecent, else the newest Timestamp.</summary>
    internal static uint? FromLoginUsers(string vdf)
    {
        uint? newest = null; long newestStamp = long.MinValue;
        foreach (Match user in UserBlock().Matches(vdf))
        {
            if (!ulong.TryParse(user.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id64)
                || id64 <= SteamId64Base || id64 - SteamId64Base > uint.MaxValue) continue;
            uint account = (uint)(id64 - SteamId64Base);
            var values = Pair().Matches(user.Groups[2].Value)
                .GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            if (values.GetValueOrDefault("MostRecent") == "1") return account;
            long stamp = long.TryParse(values.GetValueOrDefault("Timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0;
            if (stamp > newestStamp) { newestStamp = stamp; newest = account; }
        }
        return newest;
    }

    [GeneratedRegex("\"(\\d{17})\"\\s*\\{([^{}]*)\\}")]
    private static partial Regex UserBlock();

    [GeneratedRegex("\"(\\w+)\"\\s*\"([^\"]*)\"")]
    private static partial Regex Pair();
}

/// <summary>
/// Which games Cabinet copies achievements into Steam for. Cabinet only ever adds unlocks (a union, locally
/// and on its server), so a lock in such a game comes back at its next sync.
/// <list type="bullet">
/// <item>Engine on: every OnlineFix game (<c>Syncer.OnlineFixAppIds</c>, OnlineFix's Achievements.ini -> Steam).</item>
/// <item>Engine on and Cabinet Cloud connected: also every game with a Lua file (<c>CloudClient.Round</c> pulls
/// the server's union into Steam's stats cache).</item>
/// <item>Standalone Cabinet running: assume both, since its cloud state isn't visible here.</item>
/// </list>
/// </summary>
internal static class CabinetSyncScope
{
    internal static bool Syncs(bool engineOn, bool cloudConnected, bool standaloneRunning, bool onlineFixGame, bool luaGame) =>
        (engineOn && (onlineFixGame || (cloudConnected && luaGame))) || (standaloneRunning && (onlineFixGame || luaGame));
}
