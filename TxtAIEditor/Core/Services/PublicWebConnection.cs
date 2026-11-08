using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class PublicWebConnection
    {
        internal static SocketsHttpHandler CreateHandler() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, token) => ConnectAsync(
                context.DnsEndPoint, Dns.GetHostAddressesAsync, ConnectSocketAsync, token)
        };

        // Resolve once, validate the whole answer, and connect using an IP endpoint.
        // Passing the hostname to Socket.ConnectAsync would introduce a second DNS lookup.
        internal static async ValueTask<Stream> ConnectAsync(
            DnsEndPoint endpoint,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve,
            Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect,
            CancellationToken token)
        {
            IPAddress[] addresses = await resolve(endpoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(IsBlocked))
                throw new HttpRequestException("Access to internal or private addresses is blocked.");

            Exception? lastError = null;
            foreach (IPAddress address in addresses)
            {
                token.ThrowIfCancellationRequested();
                try { return await connect(new IPEndPoint(address, endpoint.Port), token); }
                catch (SocketException ex) { lastError = ex; }
            }
            throw new HttpRequestException("Unable to connect to the public address.", lastError);
        }

        private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken token)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endpoint, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal static bool IsBlocked(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                    (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                    (bytes[0] == 169 && bytes[1] == 254) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] is 0 or 168) ||
                    (bytes[0] == 198 && bytes[1] is 18 or 19);
            }
            // Only global unicast IPv6; reject transition mechanisms with embedded IPv4.
            return address.AddressFamily != AddressFamily.InterNetworkV6 ||
                (bytes[0] & 0xE0) != 0x20 ||
                (bytes[0] == 0x20 && bytes[1] == 0x02) ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
        }
    }
}
