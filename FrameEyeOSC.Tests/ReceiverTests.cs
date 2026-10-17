using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FrameEyeOSC.Tests;

/// <summary>The receiver over real UDP on 127.0.0.1 (a free port per test).</summary>
public sealed class ReceiverTests : IDisposable
{
    private readonly UdpClient _sender = new(AddressFamily.InterNetwork);
    private readonly List<(string Reason, IPEndPoint Sender)> _bad = [];
    private readonly ManualTimeProvider _time = new();
    private readonly FrameEyeOscReceiver _receiver;

    public ReceiverTests()
    {
        _receiver = new FrameEyeOscReceiver(0, (reason, sender) => _bad.Add((reason, sender)), _time);
    }

    public void Dispose()
    {
        _receiver.Dispose();
        _sender.Dispose();
    }

    internal static byte[] Packet(uint sequence, float gazeX = 0f, FeoFlags flags = FeoFlags.Tracking, uint session = 1) =>
        new FeoPacket(sequence, flags, 0, new FeoEye(gazeX, 0, 0.75f, 0, 0, 0), new FeoEye(gazeX, 0, 0.75f, 0, 0, 0), session)
            .ToBytes();

    private void Send(params byte[][] datagrams)
    {
        foreach (var d in datagrams)
            _sender.Send(d, d.Length, new IPEndPoint(IPAddress.Loopback, _receiver.Port));
        Thread.Sleep(150); // let loopback deliver all of them before one Poll drains the queue
    }

    [Fact]
    public void Poll_returns_the_newest_valid_packet_of_a_burst()
    {
        Assert.True(_receiver.IsTimedOut);
        Assert.False(_receiver.HasReceived);

        var badVersion = Packet(9);
        badVersion[4] = 2;

        Send(
            Packet(1, 0.1f),
            Packet(2, 0.2f),
            Packet(5, 0.5f),
            Packet(3, 0.3f),                       // late
            "hello"u8.ToArray(),                   // garbage
            badVersion,                            // version 2
            "FEO"u8.ToArray(),                     // short, same reason as "hello"
            Packet(5, 0.55f));                     // duplicate

        var p = _receiver.Poll(TimeSpan.FromSeconds(1));

        Assert.NotNull(p);
        Assert.Equal(5u, p.Value.Sequence);
        Assert.Equal(0.5f, p.Value.Left.GazeX);
        Assert.True(_receiver.HasReceived);
        Assert.False(_receiver.IsTimedOut);
        Assert.Equal(IPAddress.Loopback, _receiver.LastSender!.Address);
        Assert.Equal(((IPEndPoint)_sender.Client.LocalEndPoint!).Port, _receiver.LastSender.Port);

        // One report per distinct reason; nothing else is left queued.
        Assert.Equal(2, _bad.Count);
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void A_restarted_sender_is_taken_at_once()
    {
        Send(Packet(500, 0.1f, session: 1));
        Assert.Equal(500u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);

        // frameeyeosc started again a few seconds later: sequence 0 again, a new session, no timeout needed
        Send(Packet(0, 0.2f, session: 2), Packet(1, 0.3f, session: 2), Packet(499, 0.4f, session: 1));
        var p = _receiver.Poll(TimeSpan.FromSeconds(1));
        Assert.NotNull(p);
        Assert.Equal((1u, 2u), (p.Value.Sequence, p.Value.Session));
        Assert.Equal(0.3f, p.Value.Left.GazeX);
    }

    [Fact]
    public void Sender_endpoints_remain_snapshots_when_the_source_changes()
    {
        var target = new IPEndPoint(IPAddress.Loopback, _receiver.Port);
        using var other = new UdpClient(AddressFamily.InterNetwork);

        _sender.Send(Packet(1), target);
        Assert.Equal(1u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
        var first = _receiver.LastSender!;
        var firstPort = ((IPEndPoint)_sender.Client.LocalEndPoint!).Port;

        other.Send(Packet(2), target);
        Assert.Equal(2u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
        var second = _receiver.LastSender!;
        var secondPort = ((IPEndPoint)other.Client.LocalEndPoint!).Port;
        Assert.Equal(firstPort, first.Port);
        Assert.Equal(secondPort, second.Port);
        Assert.NotSame(first, second);

        // A rejected datagram must neither replace the accepted sender nor change an endpoint already reported.
        _sender.Send("bad"u8.ToArray(), target);
        Assert.Null(_receiver.Poll(TimeSpan.FromSeconds(1)));
        Assert.Same(second, _receiver.LastSender);
        Assert.Equal(secondPort, second.Port);
        var badSender = Assert.Single(_bad).Sender;
        Assert.Equal(firstPort, badSender.Port);

        other.Send(Packet(3), target);
        Assert.Equal(3u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
        Assert.Equal(firstPort, badSender.Port);
        Assert.Equal(secondPort, _receiver.LastSender!.Port);
    }

    [Fact]
    public void A_maximum_IPv4_datagram_keeps_compatible_appended_fields()
    {
        const int maxPayload = 65507; // IPv4's maximum packet minus its 20-byte IP and 8-byte UDP headers.
        var bytes = new byte[maxPayload];
        Packet(1).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), maxPayload);

        _sender.Send(bytes, new IPEndPoint(IPAddress.Loopback, _receiver.Port));
        Assert.Equal(1u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
        Assert.Empty(_bad);
    }

    [Fact]
    public void Late_packets_alone_give_nothing()
    {
        Send(Packet(100));
        Assert.Equal(100u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);

        Send(Packet(99), Packet(100), Packet(50));
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Bad_packets_are_reported_once_per_reason_every_10_s()
    {
        var badMagic = Packet(1);
        badMagic[0] = (byte)'X';

        Send(badMagic, badMagic, badMagic);
        Assert.Null(_receiver.Poll(TimeSpan.FromSeconds(1)));
        Assert.Single(_bad);
        Assert.Equal(IPAddress.Loopback, _bad[0].Sender.Address);

        // Good packets in between don't let the same reason through again (a sender mixing them would fill the log)
        for (uint i = 1; i <= 3; i++)
        {
            Send(Packet(i), badMagic);
            Assert.NotNull(_receiver.Poll(TimeSpan.FromSeconds(1)));
        }
        _time.Advance(TimeSpan.FromSeconds(9.9));
        Send(badMagic);
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(200)));
        Assert.Single(_bad);

        // Another reason is reported at once
        var badVersion = Packet(9);
        badVersion[4] = 2;
        Send(badVersion);
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(2, _bad.Count);

        // 10 s after the first report: once more, with how many went unreported
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Send(badMagic);
        Assert.Null(_receiver.Poll(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, _bad.Count);
        Assert.Equal(_bad[0].Reason + " (6 more like it since the last report)", _bad[2].Reason);
        Send(badMagic);
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(3, _bad.Count);
    }

    [Fact]
    public void After_a_timeout_any_packet_is_taken()
    {
        Send(Packet(5000));
        Assert.NotNull(_receiver.Poll(TimeSpan.FromSeconds(1)));

        Send(Packet(4990));
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(100)));

        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(_receiver.IsTimedOut);
        _time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.True(_receiver.IsTimedOut);

        Send(Packet(4990));
        Assert.Equal(4990u, _receiver.Poll(TimeSpan.FromSeconds(1))?.Sequence);
        Assert.False(_receiver.IsTimedOut);
    }

    [Fact]
    public void Empty_datagram_is_consumed_not_spun_on()
    {
        Send(Array.Empty<byte>());
        Assert.Null(_receiver.Poll(TimeSpan.FromSeconds(1)));
        Assert.Single(_bad);

        // Nothing queued any more: Poll waits for its full timeout again.
        var sw = Stopwatch.StartNew();
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.ElapsedMilliseconds >= 150, $"returned after {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Poll_with_nothing_returns_after_the_wait()
    {
        var sw = Stopwatch.StartNew();
        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(100)));
        Assert.InRange(sw.ElapsedMilliseconds, 50, 2000);
    }

    [Fact]
    public void Second_receiver_on_the_same_port_fails_with_address_in_use()
    {
        var e = Assert.Throws<SocketException>(() => new FrameEyeOscReceiver(_receiver.Port));
        Assert.Equal(SocketError.AddressAlreadyInUse, e.SocketErrorCode);

        // The first one still works.
        Send(Packet(1));
        Assert.NotNull(_receiver.Poll(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Another_socket_cannot_share_the_port()
    {
        // Even with SO_REUSEADDR (Windows answers it with AccessDenied against an exclusive owner).
        var e = Assert.Throws<SocketException>(() =>
        {
            using var other = new UdpClient();
            other.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            other.Client.Bind(new IPEndPoint(IPAddress.Any, _receiver.Port));
        });
        Assert.Contains(e.SocketErrorCode, new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
    }

    [Fact]
    public void Port_held_by_another_app_fails_with_address_in_use()
    {
        using var other = new UdpClient(0);
        var port = ((IPEndPoint)other.Client.LocalEndPoint!).Port;
        var e = Assert.Throws<SocketException>(() => new FrameEyeOscReceiver(port));
        Assert.Equal(SocketError.AddressAlreadyInUse, e.SocketErrorCode);
    }

    [Fact]
    public async Task Dispose_during_and_after_poll_is_safe()
    {
        var polling = Task.Run(() => _receiver.Poll(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        _receiver.Dispose();
        Assert.Null(await polling.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Null(_receiver.Poll(TimeSpan.FromMilliseconds(10)));
        _receiver.Dispose();
    }
}
