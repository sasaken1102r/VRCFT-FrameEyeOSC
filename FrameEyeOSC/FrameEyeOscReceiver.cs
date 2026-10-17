using System.Net;
using System.Net.Sockets;

namespace FrameEyeOSC;

/// <summary>
/// Receives FEO1 packets on a UDP port. Each <see cref="Poll"/> drains every queued datagram and returns only the newest
/// accepted packet, so a slow caller never falls behind the ~90-136 packets a second frameeyeosc sends. No VRCFT
/// dependency, so it can be tested over real UDP.
/// </summary>
public sealed class FrameEyeOscReceiver : IDisposable
{
    /// <summary>The default UDP port (next to the LiveLink module's 11111).</summary>
    public const int DefaultPort = 11112;

    /// <summary>No accepted packet for this long counts as a timeout.</summary>
    public static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A bad packet's reason is reported at most this often (a sender that mixes good and bad packets would
    /// otherwise fill the log).</summary>
    public static readonly TimeSpan BadPacketReportInterval = TimeSpan.FromSeconds(10);

    // Upper bound per Poll so a flood can't keep Update from returning.
    private const int MaxDatagramsPerPoll = 1024;

    private readonly IDatagramSocket _socket;
    private readonly byte[] _buffer = new byte[ushort.MaxValue];
    private readonly SequenceFilter _filter = new();
    private readonly TimeProvider _time;
    private readonly Action<string, IPEndPoint>? _onBadPacket;
    // Per reason: when it was last reported, and how many were left unreported since.
    private readonly Dictionary<string, (long At, int Skipped)> _reportedErrors = [];
    private long _lastAcceptedTimestamp;
    private volatile bool _disposed;

    /// <summary>Binds UDP on every IPv4 interface at <paramref name="port"/>, exclusively.</summary>
    /// <param name="port">The port; 0 picks a free one (tests).</param>
    /// <param name="onBadPacket">
    /// Called with a reason and the sender for a datagram that is not a valid packet; each reason at most once every
    /// <see cref="BadPacketReportInterval"/> (saying how many like it went unreported), so a caller can log it as is.
    /// </param>
    /// <param name="timeProvider">Clock for the timeout; the system clock when null.</param>
    /// <exception cref="SocketException">The port can't be bound (e.g. <see cref="SocketError.AddressAlreadyInUse"/>).</exception>
    public FrameEyeOscReceiver(int port, Action<string, IPEndPoint>? onBadPacket = null, TimeProvider? timeProvider = null)
        : this(new UdpDatagramSocket(port), onBadPacket, timeProvider)
    {
    }

    /// <summary>Receives on an already bound socket (tests pass a fake one).</summary>
    internal FrameEyeOscReceiver(
        IDatagramSocket socket, Action<string, IPEndPoint>? onBadPacket = null, TimeProvider? timeProvider = null)
    {
        _socket = socket;
        _onBadPacket = onBadPacket;
        _time = timeProvider ?? TimeProvider.System;
        Port = socket.Port;
    }

    /// <summary>The bound port.</summary>
    public int Port { get; }

    /// <summary>Whether any packet has been accepted.</summary>
    public bool HasReceived { get; private set; }

    /// <summary>The sender of the newest accepted packet.</summary>
    public IPEndPoint? LastSender { get; private set; }

    /// <summary>Time since the newest accepted packet, or null before the first one.</summary>
    public TimeSpan? SinceLastPacket => HasReceived ? _time.GetElapsedTime(_lastAcceptedTimestamp) : null;

    /// <summary>True before the first packet and when no packet was accepted for <see cref="PacketTimeout"/>.</summary>
    public bool IsTimedOut => SinceLastPacket is not { } since || since >= PacketTimeout;

    /// <summary>
    /// Waits up to <paramref name="wait"/> for data, then reads every queued datagram.
    /// </summary>
    /// <param name="wait">How long to wait when nothing is queued yet.</param>
    /// <returns>The newest accepted packet, or null when none arrived (or the receiver is disposed).</returns>
    public FeoPacket? Poll(TimeSpan wait)
    {
        if (_disposed)
            return null;

        try
        {
            if (_socket.Available == 0 && !_socket.Poll(wait))
                return null;

            FeoPacket? newest = null;
            var count = 0;
            var received = 0;
            var failed = 0;
            // do/while: a zero-length datagram makes Poll report readable while Available stays 0; it must still be read.
            do
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    length = _socket.ReceiveFrom(_buffer, ref from);
                    received++;
                }
                catch (SocketException e) when (!_disposed)
                {
                    // Skip that one, keep the socket: ICMP port-unreachable echoes (ConnectionReset) and oversized
                    // datagrams (MessageSize) are normal, and any other error (e.g. WSAENETRESET, WSAENOBUFS) must not
                    // stop the receiver either.
                    if (e.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.MessageSize))
                        failed++;
                    continue;
                }

                var sender = (IPEndPoint)from;
                if (!FeoPacket.TryParse(_buffer.AsSpan(0, length), out var packet, out var error))
                {
                    ReportBadPacket(error!, sender);
                    continue;
                }
                if (!_filter.Accept(packet.Sequence, IsTimedOut, packet.Session))
                    continue; // Duplicate or late: normal for UDP, not worth a log line.

                newest = packet;
                LastSender = sender;
                HasReceived = true;
                _lastAcceptedTimestamp = _time.GetTimestamp();
            }
            while (++count < MaxDatagramsPerPoll && _socket.Available > 0);

            // A socket stuck in an error state reads as ready at once; wait as if idle so the caller doesn't spin.
            if (received == 0 && failed > 0)
                Thread.Sleep(wait);

            return newest;
        }
        catch (ObjectDisposedException)
        {
            return null; // Disposed from another thread (Teardown) mid-poll.
        }
        catch (SocketException) when (_disposed)
        {
            return null;
        }
    }

    /// <summary>Closes the socket. Safe to call more than once and while another thread is in <see cref="Poll"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _socket.Dispose();
    }

    private void ReportBadPacket(string error, IPEndPoint sender)
    {
        if (_onBadPacket is null)
            return;
        if (_reportedErrors.TryGetValue(error, out var last) && _time.GetElapsedTime(last.At) < BadPacketReportInterval)
        {
            _reportedErrors[error] = (last.At, last.Skipped + 1);
            return;
        }
        _reportedErrors[error] = (_time.GetTimestamp(), 0);
        _onBadPacket(last.Skipped > 0 ? $"{error} ({last.Skipped} more like it since the last report)" : error, sender);
    }
}
