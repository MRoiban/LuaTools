namespace LuaToolsGui.Services.OnlineFixMe;

/// <summary>Stable identifiers shared by the queue, applied-fix index and revert records.</summary>
public static class OnlineFixMeFixIds
{
    private const string Prefix = "onlinefixme:";

    public static string ForArchive(string archiveName)
    {
        ArgumentNullException.ThrowIfNull(archiveName);
        return Prefix + string.Concat(archiveName.Take(128 - Prefix.Length)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
    }

    public static bool IsOnlineFixMe(string fixId) => fixId.StartsWith(Prefix, StringComparison.Ordinal);
}
