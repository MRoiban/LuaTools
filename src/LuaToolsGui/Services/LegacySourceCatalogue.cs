using System.IO;
using System.Net.Http;
using System.Text.Json;
namespace LuaToolsGui.Services;
internal static class LegacySourceCatalogue
{
    public static List<ApiSource> Read()
    {
        var sources = new List<ApiSource>();
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "api.json"),
            Path.Combine(AppContext.BaseDirectory, "api.json"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("api_list", out var list))
                    {
                        sources = new();
                        foreach (var entry in list.EnumerateArray())
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            var successCode = entry.TryGetProperty("success_code", out var sc) ? sc.GetInt32() : 200;
                            var enabled = !entry.TryGetProperty("enabled", out var en) || en.GetBoolean();
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) && enabled)
                            {
                                try
                                {
                                    new OutboundNetworkPolicy().Validate(new Uri(url.Replace("<appid>", "1")));
                                    sources.Add(new ApiSource(name, url, successCode));
                                }
                                catch (HttpRequestException) { }
                                catch (UriFormatException) { }
                            }
                        }
                    }
                    return sources;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        return [];
    }
}
