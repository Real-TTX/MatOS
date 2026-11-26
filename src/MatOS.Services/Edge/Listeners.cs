using System.Net;
using System.Net.Sockets;

namespace MatOS.Services.Edge;

/// <summary>Binding of the raw listeners (edge and DNS), which Kestrel does not manage.</summary>
public static class Listeners
{
    /// <summary>
    /// Binds <paramref name="address"/>; <c>::</c> becomes a dual-mode socket for IPv4 and IPv6, and falls back to <c>0.0.0.0</c> on a host
    /// (or container) without IPv6, so the default works everywhere.
    /// </summary>
    public static Socket Bind(IPAddress address, int port, SocketType type)
    {
        var protocol = type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp;
        if (address.Equals(IPAddress.IPv6Any))
        {
            Socket? dual = null;
            try
            {
                dual = new Socket(AddressFamily.InterNetworkV6, type, protocol) { DualMode = true };
                Prepare(dual, type);
                dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return dual;
            }
            catch (SocketException)
            {
                dual?.Dispose();
                address = IPAddress.Any;
            }
        }

        var socket = new Socket(address.AddressFamily, type, protocol);
        try
        {
            Prepare(socket, type);
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void Prepare(Socket socket, SocketType type)
    {
        if (type == SocketType.Stream && !OperatingSystem.IsWindows())
        {
            // A restarted container must be able to bind again while old connections linger in TIME_WAIT (on Windows the option would
            // let two processes share the port, so it stays off there).
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
    }
}
