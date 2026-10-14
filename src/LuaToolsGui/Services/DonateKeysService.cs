using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>Legacy compatibility surface. Key donation is permanently disabled in this fork.</summary>
public partial class DonateKeysService
{
    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex AppIdRegex();
    [GeneratedRegex(@"^[a-zA-Z0-9]{64}$")]
    private static partial Regex KeyRegex();

    public Task SendPendingKeysIfEnabledAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ── VDF parsing (ported from the plugin's _parse_vdf_simple) ──────

    /// <summary>
    /// Walk a parsed config.vdf tree and collect (appid, key) for every object carrying a string
    /// "DecryptionKey" (the object's parent key is the appid). Only validated pairs are returned.
    /// </summary>
    /// <summary>Depot-id → decryption key pairs from a config.vdf. Internal so the depot downloader can
    /// reuse it as a key source for depots whose lua carries no key.</summary>
    internal static List<(string appid, string key)> ExtractKeys(string content)
    {
        var root = ParseVdf(content);
        var result = new List<(string, string)>();
        Walk(root);
        return result;

        void Walk(Dictionary<string, object> node)
        {
            foreach (var (k, v) in node)
            {
                if (v is not Dictionary<string, object> child) continue;
                if (child.TryGetValue("DecryptionKey", out var dk) && dk is string key)
                {
                    if (IsValidPair(k, key)) result.Add((k, key));
                }
                else
                {
                    Walk(child); // keep descending. DecryptionKey nests deep under depots/<appid>
                }
            }
        }
    }

    private static bool IsValidPair(string appid, string key) =>
        AppIdRegex().IsMatch(appid) && KeyRegex().IsMatch(key);

    /// <summary>
    /// Minimal recursive VDF (Valve KeyValues) reader: "quoted" tokens, { } nesting, // line comments.
    /// Nested objects → Dictionary&lt;string,object&gt;; leaf values → string. Mirrors the plugin parser.
    /// </summary>
    private static Dictionary<string, object> ParseVdf(string content)
    {
        var root = new Dictionary<string, object>();
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);
        string? pendingKey = null;

        int pos = 0, len = content.Length;
        while (pos < len)
        {
            char c = content[pos];

            if (c == '/' && pos + 1 < len && content[pos + 1] == '/')
            {
                int nl = content.IndexOf('\n', pos);
                pos = nl < 0 ? len : nl + 1;
            }
            else if (c == '"')
            {
                int end = content.IndexOf('"', pos + 1);
                if (end < 0) break;
                string token = content[(pos + 1)..end];
                pos = end + 1;

                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek()[pendingKey] = token; // key → leaf string value
                    pendingKey = null;
                }
            }
            else if (c == '{')
            {
                if (pendingKey is not null)
                {
                    var child = new Dictionary<string, object>();
                    stack.Peek()[pendingKey] = child;
                    stack.Push(child);
                    pendingKey = null;
                }
                pos++;
            }
            else if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pos++;
            }
            else
            {
                pos++;
            }
        }

        return root;
    }
}
