using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using static FrameEyeOSC.Tests.ReceiverTests;

namespace FrameEyeOSC.Tests;

// The tests here share UnifiedTracking.Data and the config file next to FrameEyeOSC.dll, so they stay in one class
// (xUnit runs a class's tests one at a time).
public class ModuleTests
{
    private readonly ListLogger _log = new();

    private FrameEyeOscModule NewModule(Func<int, Action<string, IPEndPoint>, FrameEyeOscReceiver>? createReceiver = null)
    {
        // No startup wait: the fake socket answers at once, so a packet is either queued already or never comes.
        var module = createReceiver is null
            ? new FrameEyeOscModule { StartupWait = TimeSpan.Zero }
            : new FrameEyeOscModule { CreateReceiver = createReceiver, StartupWait = TimeSpan.Zero };
        module.Logger = _log;
        return module;
    }

    private static string ConfigPath() => Path.Combine(
        Path.GetDirectoryName(typeof(FrameEyeOscModule).Assembly.Location)!, FrameEyeOscConfig.FileName);

    [Fact]
    public void Timeout_is_a_warning_only_while_frameeyeosc_was_tracking()
    {
        // Packets stopping mid-tracking: something went wrong
        Assert.Equal(LogLevel.Warning, FrameEyeOscModule.TimeoutLogLevel(true));
        // After "no eye data" (paused, headset off): expected
        Assert.Equal(LogLevel.Information, FrameEyeOscModule.TimeoutLogLevel(false));
        // Nothing since the last timeout (it isn't logged again then, but if it were)
        Assert.Equal(LogLevel.Warning, FrameEyeOscModule.TimeoutLogLevel(null));
    }

    [Fact]
    public void The_icon_is_embedded()
    {
        using var icon = typeof(FrameEyeOscModule).Assembly.GetManifestResourceStream(FrameEyeOscModule.IconResource);
        Assert.NotNull(icon);
        Assert.True(icon.Length > 0);
    }

    [Fact]
    public void Without_an_eye_slot_it_stays_off_with_a_warning()
    {
        var module = NewModule();
        Assert.Equal((false, false), module.Initialize(false, true));
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("no eye tracking slot"));
        Assert.Equal("FrameEyeOSC", module.ModuleInformation.Name);
    }

    [Fact]
    public void A_port_in_use_fails_initialize_with_an_error()
    {
        using var other = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)other.Client.LocalEndPoint!).Port;

        // LoadConfig reads the config next to FrameEyeOSC.dll, which is the test output folder here.
        var configPath = ConfigPath();
        Assert.False(File.Exists(configPath), $"{configPath} is left over");
        File.WriteAllText(configPath, $$"""{ "port": {{port}} }""");
        try
        {
            var module = NewModule();
            Assert.Equal((false, false), module.Initialize(true, false));
            Assert.Contains(_log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains($"UDP port {port} is already in use"));

            // Update with no receiver just waits; Teardown is safe.
            module.Update();
            module.Teardown();
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    /// <summary>Runs <paramref name="body"/> with the config file next to FrameEyeOSC.dll set to <paramref name="json"/>.</summary>
    private static void WithConfig(string json, Action body)
    {
        var configPath = ConfigPath();
        Assert.False(File.Exists(configPath), $"{configPath} is left over");
        File.WriteAllText(configPath, json);
        try
        {
            body();
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void Without_claimWithoutPackets_and_packets_at_startup_it_leaves_the_eye_slot_to_other_modules() =>
        WithConfig("""{ "claimWithoutPackets": false }""", () =>
        {
            var socket = new FakeDatagramSocket();
            var module = NewModule((_, onBadPacket) => new FrameEyeOscReceiver(socket, onBadPacket));

            Assert.Equal((false, false), module.Initialize(true, false));
            Assert.Contains(_log.Lines, l => l.Level == LogLevel.Information && l.Message.StartsWith("No packets from frameeyeosc within"));

            // Packets arriving later are not read: the receiver is closed, and Update just waits.
            socket.Enqueue(Packet(1, 0.5f));
            module.Update();
            Assert.Equal(1, socket.Available);
            module.Teardown();
        });

    [Fact]
    public void Without_claimWithoutPackets_a_packet_at_startup_claims_the_eyes_and_is_applied() =>
        WithConfig("""{ "claimWithoutPackets": false }""", () =>
        {
            var socket = new FakeDatagramSocket();
            socket.Enqueue(Packet(1, 0.5f));
            var module = NewModule((_, onBadPacket) => new FrameEyeOscReceiver(socket, onBadPacket));

            Assert.Equal((true, false), module.Initialize(true, false));
            Assert.Equal(0.5f, UnifiedTracking.Data.Eye.Left.Gaze.x);
            Assert.Contains(_log.Lines, l => l.Message.StartsWith("Receiving from frameeyeosc"));
            module.Teardown();
        });

    [Fact]
    public void By_default_it_claims_the_eyes_without_waiting()
    {
        Assert.False(File.Exists(ConfigPath()), $"{ConfigPath()} is left over");
        var socket = new FakeDatagramSocket();
        var module = NewModule((_, onBadPacket) => new FrameEyeOscReceiver(socket, onBadPacket));
        Assert.Equal((true, false), module.Initialize(true, false));
        Assert.Equal(0f, UnifiedTracking.Data.Eye.Left.Gaze.x); // relaxed until frameeyeosc sends

        socket.Enqueue(Packet(1, 0.25f));
        module.Update();
        Assert.Equal(0.25f, UnifiedTracking.Data.Eye.Left.Gaze.x);
        module.Teardown();
    }

    [Fact]
    public void A_receiver_error_still_relaxes_the_eyes_after_the_timeout()
    {
        var socket = new FakeDatagramSocket();
        var time = new ManualTimeProvider();
        var module = NewModule((_, onBadPacket) => new FrameEyeOscReceiver(socket, onBadPacket, time));
        Assert.Equal((true, false), module.Initialize(true, false));

        socket.Enqueue(Packet(1, 0.5f));
        module.Update();
        Assert.Equal(0.5f, UnifiedTracking.Data.Eye.Left.Gaze.x);

        // The socket breaks: Poll throws every time.
        socket.PollError = new SocketException((int)SocketError.NetworkDown);
        module.Update();
        Assert.Equal(0.5f, UnifiedTracking.Data.Eye.Left.Gaze.x); // not timed out yet
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Error);

        time.Advance(FrameEyeOscReceiver.PacketTimeout);
        module.Update();
        Assert.Equal(0f, UnifiedTracking.Data.Eye.Left.Gaze.x);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.StartsWith("No packets from frameeyeosc"));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Error); // the same error is logged once

        // Relaxed once, not on every Update.
        module.Update();
        Assert.Single(_log.Lines, l => l.Message.StartsWith("No packets from frameeyeosc"));

        // When the socket recovers, the eyes follow again.
        socket.PollError = null;
        socket.Enqueue(Packet(2, 0.25f));
        module.Update();
        Assert.Equal(0.25f, UnifiedTracking.Data.Eye.Left.Gaze.x);
        module.Teardown();
    }
}
