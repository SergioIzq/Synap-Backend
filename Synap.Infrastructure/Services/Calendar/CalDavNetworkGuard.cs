using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Synap.Infrastructure.Services.Calendar;

/// <summary>
/// Impide que la URL CalDAV que escribe un usuario apunte a la red interna del despliegue
/// (calendar-integration design.md Decisión 5). Se aplica en el <c>ConnectCallback</c>, al abrir
/// el socket y con la dirección ya resuelta: un DNS que cambia entre la validación y la petición
/// no sirve de bypass.
///
/// Si alguna de las direcciones del host está prohibida, se rechaza el host entero.
/// </summary>
public sealed class CalDavNetworkGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    public CalDavNetworkGuard()
        : this((host, ct) => Dns.GetHostAddressesAsync(host, ct), ConnectSocketAsync)
    {
    }

    /// <summary>Para los tests: el resolvedor y la apertura del socket sustituibles.</summary>
    public CalDavNetworkGuard(
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect)
    {
        _resolve = resolve;
        _connect = connect;
    }

    /// <summary>El handler del cliente CalDAV: sin redirecciones automáticas y con este callback.</summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await _resolve(host, cancellationToken);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "CalDAV: el host no resuelve");
        }

        if (addresses.Any(IsForbidden))
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "CalDAV: dirección no permitida");
        }

        return await _connect(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken);
    }

    /// <summary>Loopback, redes privadas (RFC 1918 y ULA), link-local (incluye los metadatos), CGNAT y no enrutables.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,
                100 => b[1] is >= 64 and <= 127,       // 100.64.0.0/10, CGNAT
                169 => b[1] == 254,                    // link-local y 169.254.169.254
                172 => b[1] is >= 16 and <= 31,
                192 => b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2),
                198 => b[1] is 18 or 19,               // pruebas de red
                >= 224 => true,                        // multicast y reservadas
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = address.GetAddressBytes()[0];
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (first & 0xFE) == 0xFC;             // fc00::/7, ULA (incluye fd00:ec2::254)
        }

        return true;
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
