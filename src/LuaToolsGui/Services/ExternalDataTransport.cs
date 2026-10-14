using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LuaToolsGui.Services;

/// <summary>Public-address validation used only for scoped external inert data.</summary>
internal static class ExternalDataTransport
{
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                || b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 192 && b[1] == 88 && b[2] == 99
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0) return false;
        // Only globally routed unicast; exclude documentation and IPv4 transition tunnels.
        return (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] < 2)
            && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0)
            && !(b[0] == 0x20 && b[1] == 0x02);
    }

    internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        var addresses = await (resolver is null ? Dns.GetHostAddressesAsync(host, ct) : resolver(host, ct));
        if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            throw new HttpRequestException("External data destination must resolve exclusively to public addresses.");
        return addresses;
    }

    internal static async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, ct);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect the addresses just checked, never ask the socket to resolve the name again.
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
