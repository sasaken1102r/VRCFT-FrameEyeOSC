using System.Diagnostics;
using System.Net.Sockets;
using static FrameEyeOSC.Tests.ReceiverTests;

namespace FrameEyeOSC.Tests;

/// <summary>The receiver over a fake socket, for socket errors that real UDP on loopback can't produce.</summary>
public sealed class ReceiverSocketErrorTests
{
    private readonly FakeDatagramSocket _socket = new();
    private readonly FrameEyeOscReceiver _receiver;

    public ReceiverSocketErrorTests()
    {
        _receiver = new FrameEyeOscReceiver(_socket, timeProvider: new ManualTimeProvider());
    }

    [Theory]
    [InlineData(SocketError.NetworkReset)]         // WSAENETRESET
    [InlineData(SocketError.NoBufferSpaceAvailable)] // WSAENOBUFS
    [InlineData(SocketError.ConnectionReset)]      // ICMP port unreachable
    [InlineData(SocketError.MessageSize)]          // oversized datagram
    public void A_socket_error_skips_that_datagram_and_keeps_receiving(SocketError error)
    {
        _socket.Enqueue(Packet(1, 0.1f), new SocketException((int)error), Packet(2, 0.2f));
        Assert.Equal(2u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);

        _socket.Enqueue(new SocketException((int)error), Packet(3, 0.3f));
        Assert.Equal(3u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);

        _socket.Enqueue(Packet(4, 0.4f));
        Assert.Equal(4u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
    }

    [Fact]
    public void A_socket_stuck_in_an_error_waits_instead_of_spinning()
    {
        _socket.StuckError = new SocketException((int)SocketError.NetworkReset);

        var sw = Stopwatch.StartNew();
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.ElapsedMilliseconds >= 150, $"returned after {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, _socket.Reads);

        // Once it recovers, packets come through again.
        _socket.StuckError = null;
        _socket.Enqueue(Packet(1));
        Assert.Equal(1u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
    }

    [Fact]
    public void Errors_on_every_read_stop_at_the_per_poll_bound()
    {
        // The socket keeps saying data is queued, and every read fails.
        _socket.FloodError = new SocketException((int)SocketError.NoBufferSpaceAvailable);

        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(1024, _socket.Reads);
    }

    [Fact]
    public void Errors_outside_the_read_still_reach_the_caller()
    {
        // Update catches these and runs the timeout itself (ModuleTests).
        _socket.PollError = new SocketException((int)SocketError.NetworkDown);
        var e = Assert.Throws<SocketException>(() => _receiver.Poll(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(SocketError.NetworkDown, e.SocketErrorCode);
    }
}
