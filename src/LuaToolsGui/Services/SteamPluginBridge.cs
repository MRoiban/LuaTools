using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>The entire source-owned store UI capability surface. No raw paths, URLs or HTTP methods from JavaScript.</summary>
internal static class SteamPluginBridge
{
    internal const int MaximumQueue = 16;
    internal const int MaximumArgsBytes = 4096;
    internal const int MaximumResultBytes = 512 * 1024;
    internal const int RequestLifetimeMs = 20000;

    internal static bool IsStoreUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == "https" && u.IdnHost == "store.steampowered.com" && u.Port == 443
        && u.UserInfo.Length == 0;

    internal static (string Method, string Path) Map(string method, JsonElement args, string currentUrl)
    {
        if (!IsStoreUrl(currentUrl)) throw new InvalidOperationException("Steam store context is no longer available.");
        if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > MaximumArgsBytes)
            throw new InvalidDataException("Invalid bridge arguments.");
        var route = method switch
        {
            "HasLuaToolsForApp" => ("GET", "/has/{appid}"),
            "StartLuaToolsAdd" => ("POST", "/add/{appid}"),
            "GetLuaToolsAddStatus" => ("GET", "/add-status/{appid}"),
            "PickLuaToolsAddSource" => ("POST", "/add-source/{appid}"),
            "CancelAddViaLuaTools" => ("POST", "/cancel/{appid}"),
            "DeleteLuaToolsForApp" => ("POST", "/remove/{appid}"),
            "OpenFix" => ("POST", "/open/fix/{appid}"),
            "OpenSettings" => ("POST", "/open/settings"),
            "RestartSteam" => ("POST", "/restart-steam"),
            "CheckForUpdatesNow" => ("POST", "/check-updates"),
            "ReadLoadedApps" => ("GET", "/loaded-apps"),
            "DismissLoadedApps" => ("POST", "/loaded-apps"),
            "OpenExternalUrl" => ("POST", "/open-url"),
            "GetIconDataUrl" => ("GET", "/icon"),
            _ => throw new InvalidOperationException("Unknown Steam plugin method.")
        };
        var expected = new HashSet<string>(StringComparer.Ordinal);
        if (route.Item2.Contains("{appid}", StringComparison.Ordinal))
        {
            expected.Add("appid");
            if (!args.TryGetProperty("appid", out var id) || id.ValueKind != JsonValueKind.Number
                || !id.TryGetUInt32(out uint appid) || appid == 0)
                throw new InvalidDataException("appid must be a positive numeric Steam app ID.");
            var segments = new Uri(currentUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || segments[0] != "app"
                || !uint.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint pageApp)
                || pageApp != appid)
                throw new InvalidOperationException("The app does not match the current Steam store page.");
            route.Item2 = route.Item2.Replace("{appid}", appid.ToString(CultureInfo.InvariantCulture));
        }
        if (method == "StartLuaToolsAdd")
        {
            expected.Add("name");
            RequireString(args, "name", 512, allowEmpty: true);
        }
        if (method == "PickLuaToolsAddSource")
        {
            expected.Add("source");
            RequireString(args, "source", 128);
        }
        if (method == "OpenExternalUrl")
        {
            expected.Add("url");
            RequireString(args, "url", 2048);
            if (!IsAllowedExternalUrl(args.GetProperty("url").GetString()))
                throw new InvalidDataException("External URL is not permitted.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in args.EnumerateObject())
            if (!expected.Contains(prop.Name) || !seen.Add(prop.Name))
                throw new InvalidDataException("Unexpected bridge argument.");
        return route;
    }

    internal static bool IsAllowedExternalUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Port != 443
            || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0) return false;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (u.IdnHost == "steamdb.info" && parts.Length == 2 && parts[0] == "app"
                && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            || (u.IdnHost == "discord.gg" && parts.Length == 1 && parts[0].Length is >= 2 and <= 64
                && parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static void RequireString(JsonElement args, string name, int limit, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } s || s.Length > limit || (!allowEmpty && s.Length == 0)
            || s.Any(char.IsControl)) throw new InvalidDataException("Invalid " + name + ".");
    }

    // This script is evaluated ONLY in a CDP isolated world. Neither its queue nor the token is
    // published in the page world. Shared DOM is untrusted; frontend must require trusted input events.
    internal const string Bootstrap = """
        (() => {
          if (window.LuaToolsBridge) return;
          const pending = new Map(); let serial = 0;
          const prefix = Math.random().toString(36).slice(2) + ':';
          const bridge = Object.freeze({
            call(method, args = {}) {
              return new Promise((resolve, reject) => {
                if (pending.size >= 16) { reject(new Error('Steam plugin queue is full.')); return; }
                let raw;
                try { raw = JSON.stringify(args); } catch { reject(new Error('Invalid bridge arguments.')); return; }
                if (typeof method !== 'string' || method.length > 64 || typeof raw !== 'string' || raw.length > 4096) {
                  reject(new Error('Invalid bridge request.')); return;
                }
                const id = prefix + (++serial), expires = Date.now() + 20000;
                const timer = setTimeout(() => {
                  pending.delete(id); reject(new Error('Steam plugin bridge timed out.'));
                }, 20000);
                pending.set(id, { id, method, args: JSON.parse(raw), expires, resolve, reject, timer, sent: false });
              });
            },
            __drain() {
              const batch = [];
              for (const r of pending.values()) {
                if (r.sent) continue;
                r.sent = true; batch.push({id:r.id, method:r.method, args:r.args, expires:r.expires});
              }
              return JSON.stringify(batch);
            },
            __complete(batch) {
              for (const r of batch) {
                const item = pending.get(r.id);
                if (!item) continue;
                pending.delete(r.id); clearTimeout(item.timer);
                if (typeof r.error === 'string') item.reject(new Error(r.error)); else item.resolve(r.value);
              }
            }
          });
          Object.defineProperty(window, 'LuaToolsBridge', {value:bridge, writable:false, configurable:false});
        })();
        """;
}
