using System.Globalization;
using LuaToolsGui.Resources;
using LuaToolsGui.Services.Cabinet;
namespace LuaToolsGui.Services;

public static class CabinetProjection
{
    public static IEnumerable<CabinetGame> OrderGames(IEnumerable<CabinetGame> games) => games.OrderByDescending(g => g.IsPlaying).ThenByDescending(g => g.LastSynced).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
    public static string FormatLastSync(DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is null) return Strings.Cabinet_Never;
        var elapsed = now - last.Value;
        if (elapsed.TotalMinutes < 1) return Strings.Cabinet_JustNow;
        if (elapsed.TotalHours < 1) return string.Format(Strings.Cabinet_MinutesAgo, (int)elapsed.TotalMinutes);
        var local = last.Value.LocalDateTime;
        if (local.Date == now.LocalDateTime.Date.AddDays(-1)) return string.Format(Strings.Cabinet_Yesterday, local.ToString("HH:mm", CultureInfo.CurrentCulture));
        return local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}
