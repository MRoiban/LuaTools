using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// Local, bounded history of what the app did, as one JSON object per line. Nothing here is uploaded.
/// </summary>
/// <remarks>
/// <para>Two streams live in <c>%AppData%\LuaToolsGui-SecurityHardened\logs</c>: <c>events-*.jsonl</c> holds
/// everything for 24 hours in hourly files, and <c>important-*.jsonl</c> holds warnings and errors for
/// 7 days in daily files. Warnings are in both, so the full stream still reads in order.</para>
/// <para>The disk bound is structural: every file stops growing at <see cref="MaxSegmentBytes"/> and the
/// file count is capped, about 33 MiB in total. A flood can only fill its own hour, so it cannot evict
/// older history, and it cannot silence warnings, which have their own budget. Whatever is dropped is
/// counted and reported in the next file.</para>
/// <para>Events are written straight to disk, because the last lines before a crash are the ones worth
/// having. Logging never throws. All text is redacted here so that no call site has to remember.</para>
/// </remarks>
public sealed class AppEventLog
{
    internal const long MaxSegmentBytes = 1024 * 1024;
    private const int MaxMessageChars = 2000;
    private const int MaxExceptionChars = 8000;

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirectoryName, "logs");

    public static AppEventLog Shared { get; } = new(DefaultDirectory);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Line(string t, string l, string c, string m, string? x);

    private sealed class Stream(string prefix, string format, TimeSpan period, TimeSpan retention, int maxFiles)
    {
        public string Prefix => prefix;
        public TimeSpan Period => period;
        public TimeSpan Retention => retention;
        public int MaxFiles => maxFiles;
        public string? Current;
        public long Bytes;
        public long Dropped;
        public string NameFor(DateTimeOffset now) => $"{prefix}-{now.UtcDateTime.ToString(format, CultureInfo.InvariantCulture)}.jsonl";
        public bool TryParse(string file, out DateTime start) => DateTime.TryParseExact(
            Path.GetFileNameWithoutExtension(file)[(prefix.Length + 1)..], format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);
    }

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Stream _events = new("events", "yyyyMMdd-HH", TimeSpan.FromHours(1), TimeSpan.FromHours(24), 25);
    private readonly Stream _important = new("important", "yyyyMMdd", TimeSpan.FromDays(1), TimeSpan.FromDays(7), 8);

    public AppEventLog(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Directory => _directory;

    /// <summary>Open a session: expire old files and drop the unbounded log this replaced.</summary>
    public void Start(string message)
    {
        lock (_gate)
        {
            Prune(_events, _clock());
            Prune(_important, _clock());
            try { File.Delete(Path.Combine(Path.GetDirectoryName(_directory)!, "plugin-backend.log")); } catch { }
        }
        Write(LogLevel.Information, "App", message);
    }

    public void Write(LogLevel level, string category, string message, Exception? ex = null)
    {
        try
        {
            var now = _clock();
            string text = JsonSerializer.Serialize(new Line(
                now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                LevelName(level), category, Clip(message, MaxMessageChars),
                ex is null ? null : Clip(ex.ToString(), MaxExceptionChars)), JsonOpts);
            byte[] line = Encoding.UTF8.GetBytes(text + "\n");
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                Append(_events, now, line);
                if (level >= LogLevel.Warning) Append(_important, now, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    private void Append(Stream stream, DateTimeOffset now, byte[] line)
    {
        string name = stream.NameFor(now);
        if (name != stream.Current)
        {
            string path = Path.Combine(_directory, name);
            stream.Current = name;
            stream.Bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            Prune(stream, now);
            if (stream.Dropped > 0)
            {
                string note = JsonSerializer.Serialize(new Line(
                    now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), "warn", "EventLog",
                    $"{stream.Dropped} event(s) dropped: the previous {stream.Prefix} file was full", null), JsonOpts);
                stream.Dropped = 0;
                WriteLine(stream, Encoding.UTF8.GetBytes(note + "\n"));
            }
        }
        if (stream.Bytes + line.Length > MaxSegmentBytes) { stream.Dropped++; return; }
        WriteLine(stream, line);
    }

    private void WriteLine(Stream stream, byte[] line)
    {
        using var file = new FileStream(Path.Combine(_directory, stream.Current!), FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        file.Write(line);
        stream.Bytes += line.Length;
    }

    private void Prune(Stream stream, DateTimeOffset now)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;
            var cutoff = now.UtcDateTime - stream.Retention;
            var kept = new List<string>();
            foreach (string file in System.IO.Directory.EnumerateFiles(_directory, $"{stream.Prefix}-*.jsonl"))
            {
                if (stream.TryParse(file, out var start) && start + stream.Period > cutoff) kept.Add(file);
                else TryDelete(file);
            }
            // Names sort by time. The count also bounds files stamped ahead of a clock that was moved back.
            kept.Sort(StringComparer.Ordinal);
            foreach (string file in kept.Take(Math.Max(0, kept.Count - stream.MaxFiles))) TryDelete(file);
        }
        catch { /* the next rollover retries */ }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* in use: next rollover */ } }

    private static string Clip(string text, int max)
    {
        string clean = LogRedactor.Redact(text);
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        _ => "critical",
    };
}

/// <summary>Strips secrets and personal identifiers from text before it reaches disk.</summary>
internal static partial class LogRedactor
{
    private const string Hidden = "***";

    // A query or fragment can carry a signature or token (cabinet://connect?token=, /connect#token).
    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://[^\s""'<>?#]*)[?#][^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlTail();

    [GeneratedRegex(@"\b(api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|secret|signature|authorization)(""?\s*[:=]\s*""?)(?:bearer\s+)?[^\s""'&,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedCredential();

    [GeneratedRegex(@"\bbearer\s+[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // Hubcap keys, Cabinet tokens and JWTs (lua.tools sessions).
    [GeneratedRegex(@"\b(?:smm|cab)_[A-Za-z0-9_-]+|\beyJ[\w-]+\.[\w-]+\.[\w-]+")]
    private static partial Regex KnownToken();

    // 64 hex is a depot decryption key.
    [GeneratedRegex(@"\b[0-9a-fA-F]{40,}\b")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"\b([a-z]:[\\/]+Users[\\/]+)[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"([\\/]userdata[\\/]+)\d+|\b7656119\d{10}\b", RegexOptions.IgnoreCase)]
    private static partial Regex SteamAccount();

    public static string Redact(string text)
    {
        text = UrlTail().Replace(text, $"$1?{Hidden}");
        text = NamedCredential().Replace(text, $"$1$2{Hidden}");
        text = Bearer().Replace(text, $"Bearer {Hidden}");
        text = KnownToken().Replace(text, Hidden);
        text = LongHex().Replace(text, Hidden);
        text = WindowsProfile().Replace(text, $"$1{Hidden}");
        return SteamAccount().Replace(text, m => m.Groups[1].Value + Hidden);
    }
}

/// <summary>Routes the app's <see cref="ILogger"/> calls into <see cref="AppEventLog"/>.</summary>
public sealed class AppEventLogProvider(AppEventLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new CategoryLogger(log, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    private sealed class CategoryLogger(AppEventLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) log.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
