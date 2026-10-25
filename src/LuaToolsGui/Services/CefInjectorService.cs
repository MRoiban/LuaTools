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
/// CDP listeners are matched to the selected installation and current Windows session.
/// </summary>
public class CefInjectorService : IHostedService
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly SteamDebugEndpointDiscovery _discovery = new();
    private readonly ILogger<CefInjectorService> _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HttpServerService _backend;
    private readonly Dictionary<string, TabSession> _sessions = new(); // owned only by InjectionLoop
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string _script = "";
    private static readonly HttpResponseBudget DiscoveryBudget = new(256 * 1024, TimeSpan.FromSeconds(5));

    public CefInjectorService(SteamService steam, SettingsService settings, ILogger<CefInjectorService> logger, HttpServerService backend)
    {
        _backend = backend;
        _steam = steam;
        _settings = settings;
        _log = logger;
    }

    private bool Enabled => _settings.IsExecutableWorkflowEnabled("LuaToolsPluginFrontend") && BundledPlugin.IsActivated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!Enabled || _loopTask is { IsCompleted: false }) return;
            await ReloadPluginFilesAsync();
            if (_script.Length == 0) return;
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
            while (!ct.IsCancellationRequested && Enabled)
            {
                try
                {
                    string? installation = _steam.EffectivePath;
                    string? debugUrl = _discovery.Find(installation);
                    foreach (var stale in _sessions.Where(s => s.Value.Installation != installation || s.Value.DebugUrl != debugUrl).Select(s => s.Key).ToArray()) Evict(stale);
                    if (debugUrl is null) { await Task.Delay(350, ct); continue; }
                    using var discovery = new HttpRequestMessage(HttpMethod.Get, debugUrl);
                    DiscoveryBudget.Apply(discovery);
                    using var http = AppHttp.CreateLoopback(TimeSpan.FromSeconds(10), new Uri(debugUrl).Port);
                    using var response = await http.SendAsync(discovery, ct);
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (_steam.EffectivePath != installation) continue;
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
                        if (id.Length is < 1 or > 128 || !SteamPluginBridge.IsStoreUrl(url) || !IsAllowedDebuggerEndpoint(ws, debugUrl)
                            || !seen.Add(id)) continue;
                        try
                        {
                            var session = await GetSession(id, ws, installation!, debugUrl, ct);
                            EnsureSelection(session);
                            await EnsureWorld(session, ct);
                            await ProcessRequests(session, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            Evict(id);
                            PluginLog.Log($"CEF tab reconnect: {ex.GetType().Name}: {ex.Message}");
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
            foreach (var session in _sessions.Values) session.Connection.Dispose();
            _sessions.Clear();
        }
    }

    private async Task<TabSession> GetSession(string id, string endpoint, string installation, string debugUrl, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Steam plugin is disabled in Settings.");
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
        session = new TabSession(endpoint, new SteamCdpConnection(socket), installation, debugUrl);
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
        EnsureSelection(session);
        var frame = await CurrentFrame(session, ct);
        EnsureSelection(session);
        if (session.FrameId != frame.Id || session.LoaderId != frame.Loader)
        {
            var world = await session.Connection.CommandAsync("Page.createIsolatedWorld",
                // Reuse the same isolated world when the local app reconnects. A fresh random
                // world leaves the visible controls attached to a bridge we no longer service.
                new { frameId = frame.Id, worldName = "LuaTools-StorePlugin-v2", grantUniveralAccess = false }, ct);
            session.ContextId = world.GetProperty("executionContextId").GetInt32();
            session.FrameId = frame.Id; session.LoaderId = frame.Loader;
        }
        string alive = await session.Connection.EvaluateAsync(session.ContextId, "String(window.__LuaToolsReady === true)", ct);
        EnsureSelection(session);
        if (alive != "true" && _script.Length != 0)
            await session.Connection.EvaluateAsync(session.ContextId, _script + "\n; 'injected';", ct);
    }

    private async Task ProcessRequests(TabSession session, CancellationToken ct)
    {
        EnsureSelection(session);
        var raw = await session.Connection.EvaluateAsync(session.ContextId, "window.LuaToolsBridge.__drain()", ct);
        EnsureSelection(session);
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
                EnsureSelection(session);
                var response = await _backend.DispatchAsync(mapping.Method, mapping.Path,
                    mapping.Method == "GET" ? "" : args.GetRawText(), session.Installation, deadline.Token)
                    .WaitAsync(deadline.Token);
                if (response.Status is < 200 or >= 300) throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_RequestRejected);
                var text = response.Body;
                if (Encoding.UTF8.GetByteCount(text) > SteamPluginBridge.MaximumResultBytes)
                    throw new InvalidDataException(LuaToolsGui.Resources.Strings.SteamIsolation_ResultTooLarge);
                using var result = JsonDocument.Parse(text);
                reply = new { id, value = result.RootElement.Clone() };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { reply = new { id, error = "Steam plugin bridge timed out." }; }
            catch (Exception ex) { reply = new { id, error = ex.Message }; }
            // Match by stable request ID, even if another promise expired or new calls arrived meanwhile.
            EnsureSelection(session);
            await session.Connection.EvaluateAsync(session.ContextId,
                "window.LuaToolsBridge.__complete([" + JsonSerializer.Serialize(reply) + "]); 'completed';", ct);
        }
    }

    public static bool IsAllowedDebuggerEndpoint(string url) => IsAllowedDebuggerEndpoint(url, "http://127.0.0.1:8080/json");

    internal static bool IsAllowedDebuggerEndpoint(string url, string discoveryUrl)
    {
        if (!Uri.TryCreate(discoveryUrl, UriKind.Absolute, out var discovery)
            || discovery.Scheme != "http" || discovery.Host is not ("127.0.0.1" or "[::1]")
            || discovery.UserInfo.Length != 0 || discovery.AbsolutePath != "/json"
            || discovery.Query.Length != 0 || discovery.Fragment.Length != 0 || discovery.Port < 1)
            return false;
        string host = discovery.Host;
        if (host == "[::1]")
        {
            if (url.StartsWith("ws://[0000:0000:0000:0000:0000:0000:0000:0001]:", StringComparison.Ordinal))
                host = "[0000:0000:0000:0000:0000:0000:0000:0001]";
            else if (url.StartsWith("ws://[0:0:0:0:0:0:0:1]:", StringComparison.Ordinal)) host = "[0:0:0:0:0:0:0:1]";
        }
        string prefix = $"ws://{host}:{discovery.Port}/devtools/page/";
        return url.StartsWith(prefix, StringComparison.Ordinal) && url.Length > prefix.Length
            && url.Length <= prefix.Length + 256
            && url[prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private void Evict(string id)
    {
        if (_sessions.Remove(id, out var session)) session.Connection.Dispose();
    }
    private void EnsureSelection(TabSession session)
    {
        if (!Enabled || !string.Equals(_steam.EffectivePath, session.Installation, StringComparison.OrdinalIgnoreCase)
            || _discovery.Find(session.Installation, refresh: true) != session.DebugUrl)
            throw new InvalidOperationException(LuaToolsGui.Resources.Strings.SteamIsolation_SelectionChanged);
    }

    private sealed class TabSession(string endpoint, SteamCdpConnection connection, string installation, string debugUrl)
    {
        internal string Installation { get; } = installation;
        internal string DebugUrl { get; } = debugUrl;
        internal string Endpoint { get; } = endpoint;
        internal SteamCdpConnection Connection { get; } = connection;
        internal string? FrameId { get; set; }
        internal string? LoaderId { get; set; }
        internal int ContextId { get; set; }
    }
    private sealed record Frame(string Id, string Loader, string Url);
}
