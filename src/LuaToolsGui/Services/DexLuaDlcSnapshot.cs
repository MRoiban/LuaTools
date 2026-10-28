using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>One validated parent script and depot map, retained for a single DLC batch.</summary>
public sealed class DexLuaDlcSnapshot(AppDepotInfo info, IReadOnlyDictionary<long, string> keys)
{
    internal static List<ContentDepot> SelectDepots(AppDepotInfo info, string appid)
    {
        ApiContract.RequireAppId(appid);
        long dlcId = long.Parse(appid, CultureInfo.InvariantCulture);
        var depots = info.Depots.Where(d => d.DlcAppId == dlcId).ToList();
        if (depots.Count == 0 && !info.DlcIds.Contains(dlcId))
            throw new ApiException(Resources.Strings.Add_Err_BaseGame);
        return depots;
    }

    public DlcInfo GetInfo(string appid)
    {
        var depots = SelectDepots(info, appid);
        return new DlcInfo
        {
            AppId = info.AppId.ToString(CultureInfo.InvariantCulture), Type = "dlc", DepotCount = depots.Count,
            HaveCount = depots.Count(d => keys.ContainsKey(d.Id)),
            MissingCount = depots.Count(d => !keys.ContainsKey(d.Id)),
            Depots = depots.Select(d => new DlcDepot
            {
                DepotId = d.Id.ToString(CultureInfo.InvariantCulture), Language = d.Language,
                OsList = d.Os, Included = keys.ContainsKey(d.Id),
            }).ToList(),
        };
    }

    public async Task<DownloadedFile> GenerateAsync(string appid, IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var depots = SelectDepots(info, appid);
        if (depots.Count > 0 && !depots.Any(d => keys.ContainsKey(d.Id)))
            throw new ApiException(Resources.Strings.DexLua_NoDlcKeys);
        var lua = new StringBuilder($"addappid({appid})\n");
        foreach (var depot in depots)
            if (keys.TryGetValue(depot.Id, out var key))
                lua.Append($"addappid({depot.Id},0,\"{key}\")\n");
        // Public metadata is not a provider pin. Generate only this DLC's active keys.
        var bytes = Encoding.UTF8.GetBytes(lua.ToString());
        ManifestContentPolicy.ValidateLua(bytes, long.Parse(appid, CultureInfo.InvariantCulture));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return await HttpFileDownloader.SaveResponseAsync(response, $"{appid}.lua", progress, ct);
    }
}
