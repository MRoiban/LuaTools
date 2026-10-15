using LuaToolsGui.Services.Cabinet;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// Source-owned UI in a CDP isolated world. Ordinary store scripts cannot access its capability queue.
/// CDP on Steam's fixed local port does not authenticate the local listening process.
/// </summary>
public class CefInjectorService : IHostedService
{
    private readonly SettingsService _settings;
    private readonly ILogger<CefInjectorService> _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HttpClient _http = AppHttp.CreateLoopback(TimeSpan.FromSeconds(10), 8080, 6767);
    private readonly Dictionary<string, TabSession> _sessions = new(); // owned only by InjectionLoop
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string _script = "";
    private const string DebugUrl = "http://127.0.0.1:8080/json";
    private const string BackendUrl = "http://127.0.0.1:6767";
    private static readonly HttpResponseBudget DiscoveryBudget = new(256 * 1024, TimeSpan.FromSeconds(5));
    private static readonly HttpResponseBudget ResultBudget = new(SteamPluginBridge.MaximumResultBytes, TimeSpan.FromSeconds(8));

    public CefInjectorService(SteamService steam, SettingsService settings, ILogger<CefInjectorService> logger)
    {
        _settings = settings;
        _log = logger;
    }

    private bool Enabled => _settings.IsExecutableWorkflowEnabled("LuaToolsPluginFrontend") && BundledPlugin.IsActivated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await ReloadPluginFilesAsync();
            if (_loopTask is { IsCompleted: false }) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => InjectionLoop(_cts.Token));
            _log.LogInformation("Source-owned Steam store UI activated");
        }
        finally { _lifecycle.Release(); }
    }

    // Resources belong to this assembly; no downloaded frontend or external polyfill fallback.
    public Task ReloadPluginFilesAsync()
    {
        _script = "";
        if (!Enabled) return Task.CompletedTask;
        try
        {
            var js = BundledPlugin.ReadText("steam-plugin.js");
            var css = BundledPlugin.ReadText("steam-plugin.css");
            _script = SteamPluginBridge.Bootstrap + "\nwindow.__LuaToolsCss = " + JsonSerializer.Serialize(css)
                + ";\nwindow.__LuaToolsStrings = " + SteamPluginStrings.BuildJson() + ";\n" + js;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Bundled Steam store UI could not be loaded"); }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            _cts?.Cancel();
            _script = "";
            if (_loopTask is { } loop)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                if (!loop.IsCompleted) return; // do not overlap generations
            }
            _loopTask = null;
            _cts?.Dispose(); _cts = null;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task InjectionLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!Enabled && !FriendLabels.Active && !_sessions.Values.Any(s => s.Friends))
                    { await Task.Delay(1000, ct); continue; }
                    using var discovery = new HttpRequestMessage(HttpMethod.Get, DebugUrl);
                    DiscoveryBudget.Apply(discovery);
                    using var response = await _http.SendAsync(discovery, ct);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 128)
                        throw new InvalidDataException("Unexpected CEF target list.");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var target in document.RootElement.EnumerateArray())
                    {
                        if (!target.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                            || !target.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String
                            || !target.TryGetProperty("webSocketDebuggerUrl", out var wsValue) || wsValue.ValueKind != JsonValueKind.String)
                            continue;
                        string id = idValue.GetString()!, url = urlValue.GetString()!, ws = wsValue.GetString()!;
                        bool friends = target.TryGetProperty("title", out var title) && title.GetString() == "SharedJSContext"
                            && IsFriendsUrl(url) && (FriendLabels.Active || _sessions.TryGetValue(id, out var old) && old.Friends);
                        if (id.Length is < 1 or > 128 || !(friends || Enabled && SteamPluginBridge.IsStoreUrl(url)) || !IsAllowedDebuggerEndpoint(ws)
                            || !seen.Add(id)) continue;
                        try
                        {
                            var session = await GetSession(id, ws, ct);
                            if (friends)
                            {
                                // Steam's own store objects live in its main world. No bridge, token, or
                                // downloaded code enters this context; only bundled hooks and sanitized JSON.
                                session.Friends = true;
                                if (DateTimeOffset.UtcNow >= session.NextLabels || !FriendLabels.Active)
                                {
                                    var tree = await session.Connection.CommandAsync("Page.getFrameTree", new { }, ct);
                                    if (!IsFriendsUrl(tree.GetProperty("frameTree").GetProperty("frame").GetProperty("url").GetString()!))
                                        throw new InvalidOperationException("Steam friends context changed.");
                                    var result = await session.Connection.CommandAsync("Runtime.evaluate", new { expression = FriendLabels.Script, returnByValue = true }, ct);
                                    if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("Steam friends hooks unavailable.");
                                    session.NextLabels = DateTimeOffset.UtcNow.AddSeconds(5);
                                }
                                if (!FriendLabels.Active) Evict(id);
                                continue;
                            }
                            await EnsureWorld(session, ct);
                            await ProcessRequests(session, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            Evict(id);
                            _log.LogDebug("CEF tab unavailable: {Message}", ex.Message);
                        }
                    }
                    foreach (var stale in _sessions.Keys.Where(k => !seen.Contains(k)).ToArray()) Evict(stale);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogDebug("CEF discovery unavailable: {Message}", ex.Message); }
                await Task.Delay(350, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Friends)
                {
                    try { await session.Connection.CommandAsync("Runtime.evaluate", new { expression = FriendLabels.BuildScript(new()), returnByValue = true }, CancellationToken.None); } catch { }
                }
                session.Connection.Dispose();
            }
            _sessions.Clear();
        }
    }

    private async Task<TabSession> GetSession(string id, string endpoint, CancellationToken ct)
    {
        if (!Enabled && !FriendLabels.Active && !_sessions.ContainsKey(id)) throw new InvalidOperationException("Steam UI features are disabled.");
        if (_sessions.TryGetValue(id, out var session))
        {
            if (session.Endpoint == endpoint) return session;
            Evict(id);
        }
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(endpoint), timeout.Token);
        }
        catch { socket.Dispose(); throw; }
        session = new TabSession(endpoint, new SteamCdpConnection(socket));
        _sessions.Add(id, session);
        return session;
    }

    private static async Task<Frame> CurrentFrame(TabSession session, CancellationToken ct)
    {
        var result = await session.Connection.CommandAsync("Page.getFrameTree", new { }, ct);
        var frame = result.GetProperty("frameTree").GetProperty("frame");
        string url = frame.GetProperty("url").GetString()!;
        if (!SteamPluginBridge.IsStoreUrl(url)) throw new InvalidOperationException("Steam store context is no longer available.");
        return new Frame(frame.GetProperty("id").GetString()!, frame.GetProperty("loaderId").GetString()!, url);
    }

    private async Task EnsureWorld(TabSession session, CancellationToken ct)
    {
        var frame = await CurrentFrame(session, ct);
        if (session.FrameId != frame.Id || session.LoaderId != frame.Loader)
        {
            var world = await session.Connection.CommandAsync("Page.createIsolatedWorld",
                new { frameId = frame.Id, worldName = "LuaTools-" + Guid.NewGuid().ToString("N"), grantUniveralAccess = false }, ct);
            session.ContextId = world.GetProperty("executionContextId").GetInt32();
            session.FrameId = frame.Id; session.LoaderId = frame.Loader;
        }
        string alive = await session.Connection.EvaluateAsync(session.ContextId, "String(window.__LuaToolsReady === true)", ct);
        if (alive != "true" && _script.Length != 0)
            await session.Connection.EvaluateAsync(session.ContextId, _script + "\n; 'injected';", ct);
    }

    private async Task ProcessRequests(TabSession session, CancellationToken ct)
    {
        var raw = await session.Connection.EvaluateAsync(session.ContextId, "window.LuaToolsBridge.__drain()", ct);
        using var batch = JsonDocument.Parse(raw);
        if (batch.RootElement.ValueKind != JsonValueKind.Array || batch.RootElement.GetArrayLength() > SteamPluginBridge.MaximumQueue)
            throw new InvalidDataException("Invalid bridge queue.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in batch.RootElement.EnumerateArray())
        {
            string id = request.GetProperty("id").GetString()!;
            if (id.Length is < 1 or > 128 || !seen.Add(id)) throw new InvalidDataException("Invalid bridge request ID.");
            object reply;
            try
            {
                var expires = request.GetProperty("expires").GetInt64();
                long remaining = expires - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining <= 0 || remaining > SteamPluginBridge.RequestLifetimeMs)
                    throw new TimeoutException("Steam plugin bridge timed out.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining, 8000)));
                // Re-read the actual top frame before EVERY dispatch, not cached /json metadata.
                var frame = await CurrentFrame(session, deadline.Token);
                if (frame.Id != session.FrameId || frame.Loader != session.LoaderId)
                    throw new InvalidOperationException("Steam store page navigated. Please retry.");
                string method = request.GetProperty("method").GetString()!;
                var args = request.GetProperty("args");
                var mapping = SteamPluginBridge.Map(method, args, frame.Url);
                if (!Enabled) throw new InvalidOperationException("Steam plugin is disabled in Settings.");
                using var httpRequest = new HttpRequestMessage(mapping.Method == "GET" ? HttpMethod.Get : HttpMethod.Post, BackendUrl + mapping.Path);
                ResultBudget.Apply(httpRequest);
                httpRequest.Headers.TryAddWithoutValidation(LocalPluginBridgePolicy.TokenHeader, LocalPluginBridgePolicy.Token);
                if (mapping.Method != "GET") httpRequest.Content = new StringContent(args.GetRawText(), Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(httpRequest, deadline.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("Steam plugin is disabled in Settings.");
                var text = await response.Content.ReadAsStringAsync(deadline.Token);
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(text);
                reply = new { id, value = result.RootElement.Clone() };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { reply = new { id, error = "Steam plugin bridge timed out." }; }
            catch (Exception ex) { reply = new { id, error = ex.Message }; }
            // Match by stable request ID, even if another promise expired or new calls arrived meanwhile.
            await session.Connection.EvaluateAsync(session.ContextId,
                "window.LuaToolsBridge.__complete([" + JsonSerializer.Serialize(reply) + "]); 'completed';", ct);
        }
    }

    public static bool IsAllowedDebuggerEndpoint(string url)
    {
        // Validate the wire form as well: Uri normalizes alternate numeric IPv4 forms and dot segments.
        const string prefix = "ws://127.0.0.1:8080/devtools/page/";
        return url.StartsWith(prefix, StringComparison.Ordinal) && url.Length > prefix.Length
            && url.Length <= prefix.Length + 256
            && url[prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    internal static bool IsFriendsUrl(string url) =>
        url == "about:blank" || url == "https://steamloopback.host/" || url == "https://steamloopback.host/index.html"
        || url == "https://steamloopback.host/friends.html";

    private void Evict(string id)
    {
        if (_sessions.Remove(id, out var session)) session.Connection.Dispose();
    }
    private sealed class TabSession(string endpoint, SteamCdpConnection connection)
    {
        internal string Endpoint { get; } = endpoint;
        internal SteamCdpConnection Connection { get; } = connection;
        internal string? FrameId { get; set; }
        internal string? LoaderId { get; set; }
        internal int ContextId { get; set; }
        internal bool Friends { get; set; }
        internal DateTimeOffset NextLabels { get; set; }
    }
    private sealed record Frame(string Id, string Loader, string Url);
}
