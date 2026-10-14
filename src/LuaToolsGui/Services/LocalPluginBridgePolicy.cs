using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>Per-process authentication for the explicitly activated, local-only plugin bridge.</summary>
public static class LocalPluginBridgePolicy
{
    public const string TokenHeader = "X-LuaTools-Bridge-Token";
    public const int Port = 6767;
    internal const int MaximumBodyBytes = 64 * 1024;
    public static string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool IsAuthorized(bool enabled, Uri? url, string? hostHeader,
        IPAddress? remoteAddress, bool isLocal, string? origin, string? suppliedToken)
    {
        if (!enabled || !isLocal || url is null || url.Scheme != "http"
            || url.Host != "127.0.0.1" || url.Port != Port || url.UserInfo.Length != 0
            || hostHeader != $"127.0.0.1:{Port}" || !string.IsNullOrEmpty(origin)
            || remoteAddress is null
            || remoteAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork && !remoteAddress.IsIPv4MappedToIPv6
            || !remoteAddress.MapToIPv4().Equals(IPAddress.Loopback)
            || suppliedToken is null || suppliedToken.Length != Token.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(suppliedToken), Encoding.ASCII.GetBytes(Token));
    }

    internal static async Task<string> ReadBodyAsync(Stream input, long declaredLength, CancellationToken cancellation,
        TimeSpan? timeout = null)
    {
        if (declaredLength > MaximumBodyBytes) throw new InvalidDataException("Bridge request exceeds its size limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token).AsTask().WaitAsync(deadline.Token)) > 0)
        {
            if (output.Length + read > MaximumBodyBytes) throw new InvalidDataException("Bridge request exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (declaredLength >= 0 && output.Length != declaredLength) throw new InvalidDataException("Incomplete bridge request.");
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
