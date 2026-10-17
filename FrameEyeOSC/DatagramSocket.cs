using System.Net;
using System.Net.Sockets;

namespace FrameEyeOSC;

/// <summary>
/// The few socket calls <see cref="FrameEyeOscReceiver"/> makes, so tests can feed it socket errors that real UDP on
/// loopback can't produce. <see cref="UdpDatagramSocket"/> is the only real one.
/// </summary>
internal interface IDatagramSocket : IDisposable
{
    /// <summary>The bound port.</summary>
    int Port { get; }

    /// <summary>Bytes queued to read (<see cref="Socket.Available"/>).</summary>
    int Available { get; }

    /// <summary>Waits up to <paramref name="wait"/> until a read won't block (<see cref="Socket.Poll(TimeSpan, SelectMode)"/>).</summary>
    bool Poll(TimeSpan wait);

    /// <summary>Reads one datagram (<see cref="Socket.ReceiveFrom(byte[], ref EndPoint)"/>).</summary>
    int ReceiveFrom(byte[] buffer, ref EndPoint from);
}

/// <summary>A UDP socket bound exclusively on every IPv4 interface.</summary>
internal sealed class UdpDatagramSocket : IDatagramSocket
{
    private readonly Socket _socket;

    /// <summary>Binds <paramref name="port"/>.</summary>
    /// <param name="port">The port; 0 picks a free one.</param>
    /// <exception cref="SocketException">The port can't be bound.</exception>
    public UdpDatagramSocket(int port)
    {
        // Exclusive: a second listener on the port must fail loudly instead of silently sharing the packets.
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public int Available => _socket.Available;

    public bool Poll(TimeSpan wait) => _socket.Poll(wait, SelectMode.SelectRead);

    public int ReceiveFrom(byte[] buffer, ref EndPoint from) => _socket.ReceiveFrom(buffer, ref from);

    public void Dispose() => _socket.Dispose();
}
