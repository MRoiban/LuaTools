using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.OnlineFixMe;

public sealed record OnlineFixMeArchive(string Name, Uri Url, string? SizeLabel, string? DateLabel);
public enum OnlineFixMeLookupStatus { Found, NotFound, InvalidFolder, Unavailable }
public sealed record OnlineFixMeLookup(OnlineFixMeLookupStatus Status, Uri? FolderUrl, string? FolderName,
    IReadOnlyList<OnlineFixMeArchive> Archives);

/// <summary>Pure title matching and bounded parsing of the provider's Fix Repair autoindex.</summary>
public static class OnlineFixMeListing
{
    private const int MaxListingBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex IndexTitle = new(@"<title\b[^>]*>\s*Index\s+of\b[^<]*</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Href = new("<a\\b[^<>]*?\\s+href\\s*=\\s*\"(?<href>[^\"<>]*)\"[^<>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Columns = new(@"</a\s*>\s*(?:(?<date>\d{2}-[A-Za-z]{3}-\d{4}\s+\d{2}:\d{2})\s*)?(?<size>\d+(?:\.\d+)?[KMGTPE]?|-)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex MultiVolume = new(@"(?:\.part\d+\.rar|\.r\d{2}|\.z\d{2}|\.7z\.\d{3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    public static IReadOnlyList<string> CandidateFolderNames(string? gameName)
    {
        if (gameName is null || gameName.Length > 1024) return [];
        string cleaned = Whitespace.Replace(gameName.Replace("™", "").Replace("®", "").Replace("©", ""), " ").Trim();
        var names = new List<string>(3);
        Add(cleaned);
        string noColon = Whitespace.Replace(cleaned.Replace(":", ""), " ").Trim();
        if (cleaned.Contains(':')) Add(noColon);
        if (noColon.Contains('\'') || noColon.Contains('’')) Add(noColon.Replace("'", "").Replace("’", ""));
        return names.AsReadOnly();

        void Add(string name)
        {
            if (IsSegment(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
    }

    public static Uri? FolderFromName(string name) => IsSegment(name)
        ? new Uri(AppConfig.OnlineFixMeUploadsBase + Uri.EscapeDataString(name) + "/") : null;

    public static Uri? FolderFromUserUrl(string? url)
    {
        // Inspect the supplied path before Uri can normalize dot segments or backslashes.
        if (url is null || !url.StartsWith(AppConfig.OnlineFixMeUploadsBase, StringComparison.Ordinal)) return null;
        string[] parts = url[AppConfig.OnlineFixMeUploadsBase.Length..].Split('/');
        if (parts.Length > 3 || DecodeSegment(parts[0]) is not { } name) return null;
        string repair = Uri.EscapeDataString(AppConfig.OnlineFixMeFixRepairFolder);
        if (parts.Length == 2 && parts[1].Length != 0 && parts[1] != repair) return null;
        if (parts.Length == 3 && (parts[1] != repair
            || parts[2].Length != 0 && DecodeSegment(parts[2]) is null)) return null;
        return FolderFromName(name);
    }

    public static Uri FixRepairUrl(Uri folder)
    {
        if (!IsGameFolder(folder)) throw new ArgumentException("Invalid Online-Fix.me game folder.", nameof(folder));
        return new Uri(folder, Uri.EscapeDataString(AppConfig.OnlineFixMeFixRepairFolder) + "/");
    }

    public static bool IsEligibleArchiveName(string name) => IsSegment(name)
        && name.Contains("fix_repair", StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        && !MultiVolume.IsMatch(name);

    public static IReadOnlyList<OnlineFixMeArchive> ParseFixRepairListing(string html, Uri folder)
    {
        if (!IsGameFolder(folder) || !WithinBudget(html)) return [];
        var archives = new List<OnlineFixMeArchive>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri repair = FixRepairUrl(folder);
        try
        {
            int offset = 0;
            for (int examined = 0; examined < 512; examined++)
            {
                Match match = Href.Match(html, offset);
                if (!match.Success) break;
                offset = match.Index + match.Length;
                string? name = DecodeSegment(match.Groups["href"].Value);
                if (name is null || !IsEligibleArchiveName(name)) continue;
                int colon = name.IndexOf(':');
                if (colon > 0 && Uri.CheckSchemeName(name[..colon]) || !seen.Add(name)) continue;
                int start = match.Index + match.Length;
                int length = Math.Min(html.Length - start, 1024);
                int end = html.AsSpan(start, length).IndexOfAny('\r', '\n');
                string line = html.Substring(start, end < 0 ? length : end);
                var columns = Columns.Match(line);
                archives.Add(new(name, new Uri(repair, Uri.EscapeDataString(name)),
                    columns.Success && columns.Groups["size"].Success && columns.Groups["size"].Value != "-" ? columns.Groups["size"].Value : null,
                    columns.Success && columns.Groups["date"].Success ? columns.Groups["date"].Value : null));
            }
        }
        catch (RegexMatchTimeoutException) { return []; }
        archives.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return archives.AsReadOnly();
    }

    public static bool LooksLikeIndex(string html)
    {
        if (!WithinBudget(html)) return false;
        try { return IndexTitle.IsMatch(html); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static bool IsGameFolder(Uri? folder) => folder is { IsAbsoluteUri: true }
        && FolderFromUserUrl(folder.OriginalString) is { } canonical
        && folder.AbsoluteUri.Equals(canonical.AbsoluteUri, StringComparison.Ordinal);

    internal static string? DecodeSegment(string value)
    {
        if (value.Length is 0 or > 2400) return null;
        var decoded = new StringBuilder();
        try
        {
            for (int i = 0; i < value.Length;)
            {
                if (value[i] != '%') { decoded.Append(value[i++]); continue; }
                var bytes = new List<byte>();
                while (i < value.Length && value[i] == '%')
                {
                    if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return null;
                    bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                    i += 3;
                }
                decoded.Append(StrictUtf8.GetString(bytes.ToArray()));
            }
            string name = decoded.ToString();
            return IsSegment(name) ? name : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or ".."
            || name.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#')) return false;
        try { StrictUtf8.GetByteCount(name); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool WithinBudget(string html)
    {
        if (html.Length > MaxListingBytes) return false;
        try { return StrictUtf8.GetByteCount(html) <= MaxListingBytes; }
        catch (EncoderFallbackException) { return false; }
    }
}
