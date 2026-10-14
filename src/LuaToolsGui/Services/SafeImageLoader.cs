using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuaToolsGui.Services;

/// <summary>Images use the same outbound policy as API requests; WPF only receives memory streams.</summary>
public static class SafeImageLoader
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim Downloads = new(4);

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(SafeImageLoader), new PropertyMetadata(null, SourceChanged));
    private static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(CancellationTokenSource), typeof(SafeImageLoader));

    public static string? GetSource(DependencyObject target) => (string?)target.GetValue(SourceProperty);
    public static void SetSource(DependencyObject target, string? value) => target.SetValue(SourceProperty, value);

    private static void SourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Image && target is not ImageBrush) return;
        if (target.GetValue(RequestProperty) is CancellationTokenSource previous)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        var request = new CancellationTokenSource();
        target.SetValue(RequestProperty, request);
        SetImage(target, null);
        _ = UpdateAsync(target, args.NewValue as string, request);
    }

    private static async Task UpdateAsync(DependencyObject target, string? source, CancellationTokenSource request)
    {
        try
        {
            var image = await LoadAsync(source, request.Token).ConfigureAwait(false);
            var dispatcher = target.Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(target.GetValue(RequestProperty), request)) return;
                SetImage(target, image);
                target.ClearValue(RequestProperty);
            }).Task.ConfigureAwait(false);
        }
        catch { /* Dispatcher shutdown or a detached/frozen target must not escape a property callback. */ }
        finally { request.Dispose(); }
    }

    private static void SetImage(DependencyObject target, ImageSource? source)
    {
        if (target is Image image) image.Source = source;
        else if (target is ImageBrush brush) brush.ImageSource = source;
    }

    internal static async Task<ImageSource?> LoadAsync(string? source, CancellationToken ct = default,
        Func<Uri, HttpClient>? clientFactory = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http"))
                return await Task.Run(() => LoadLocal(source), ct);

            await Downloads.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // ResponseHeadersRead needs an explicit deadline covering the body as well as headers.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var http = clientFactory?.Invoke(uri) ?? AppHttp.CreateExternalDataClient(
                    TimeSpan.FromSeconds(15), uri, HttpResponseBudget.Image);
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes) return null;
                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var bytes = await ReadBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
                return await Task.Run(() => Decode(bytes, 1024), deadline.Token).ConfigureAwait(false);
            }
            finally { Downloads.Release(); }
        }
        catch { return null; } // A rejected destination, invalid image or interrupted request stays blank.
    }

    internal static ImageSource? LoadLocal(string? path, int decodePixelWidth = 1024)
    {
        try
        {
            if (!IsLocalPath(path)) return null;
            using var stream = File.OpenRead(path!);
            if (stream.Length > MaximumBytes) return null;
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + count > MaximumBytes) return null;
                bytes.Write(buffer, 0, count);
            }
            return Decode(bytes.ToArray(), decodePixelWidth);
        }
        catch { return null; }
    }

    internal static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return false;
            // Reject junctions/symlinks as well: a local-looking path can otherwise reach a UNC share.
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch { return false; }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("Image is too large.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    internal static ImageSource? Decode(byte[] bytes, int decodePixelWidth)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes || decodePixelWidth <= 0) return null;
        using var stream = new MemoryStream(bytes, writable: false);
        // Inspect dimensions before decoding pixels, rejecting decompression bombs.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16384
            || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000) return null;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Min(frame.PixelWidth, decodePixelWidth);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
