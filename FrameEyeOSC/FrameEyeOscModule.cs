using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;

namespace FrameEyeOSC;

/// <summary>
/// VRCFaceTracking module that takes eye tracking from frameeyeosc on the Steam Frame (FEO1 packets over UDP, see
/// docs/protocol.md): gaze, eyelids including wide eyes, squint, pupils and, when frameeyeosc sends them, brows. Eye only.
/// </summary>
public sealed class FrameEyeOscModule : ExtTrackingModule
{
    internal const string IconResource = "FrameEyeOSC.Assets.frameeyeosc-256.png";

    // Short enough that Update returns regularly with no packets; VRCFT calls it again in a loop.
    private static readonly TimeSpan PollWait = TimeSpan.FromMilliseconds(50);

    private FrameEyeOscReceiver? _receiver;
    private IPEndPoint? _loggedSender;
    private bool? _lastTracking;
    private uint? _lastSession;
    private readonly VrcftEyeWriter _writer = new();
    private bool _relaxedForTimeout = true;
    private string? _lastUpdateError;

    /// <summary>Opens the receiver on a port; tests swap in one over a fake socket.</summary>
    internal Func<int, Action<string, IPEndPoint>, FrameEyeOscReceiver> CreateReceiver { get; init; } =
        (port, onBadPacket) => new FrameEyeOscReceiver(port, onBadPacket);

    /// <inheritdoc />
    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, false);

    /// <inheritdoc />
    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        ModuleInformation.Name = "FrameEyeOSC";
        var icon = typeof(FrameEyeOscModule).Assembly.GetManifestResourceStream(IconResource);
        if (icon is not null)
            ModuleInformation.StaticImages = [icon];

        if (!eyeAvailable)
        {
            Logger.LogWarning("VRCFT has no eye tracking slot for FrameEyeOSC (another module provides the eyes, or eye tracking is off); staying off.");
            return (false, false);
        }

        var config = LoadConfig();

        // Bind now but don't wait for packets: VRCFT kills a module process that stays silent for 60 s in Initialize.
        try
        {
            _receiver = CreateReceiver(config.Port, OnBadPacket);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Logger.LogError(
                "UDP port {Port} is already in use — another FrameEyeOSC/VRCFT instance or another app is listening on it; " +
                "close it or change the port in {ConfigFile} and frameeyeosc's port setting.",
                config.Port, FrameEyeOscConfig.FileName);
            return (false, false);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AccessDenied)
        {
            // Windows also refuses ports inside its reserved ranges (Hyper-V/WSL) this way.
            Logger.LogError(
                "Windows refused UDP port {Port} (access denied): another app may hold it exclusively, or it is reserved " +
                "(see 'netsh interface ipv4 show excludedportrange protocol=udp'); change the port in {ConfigFile} and " +
                "frameeyeosc's port setting.",
                config.Port, FrameEyeOscConfig.FileName);
            return (false, false);
        }
        catch (SocketException e)
        {
            Logger.LogError("Could not listen on UDP port {Port}: {Error} ({Code}).", config.Port, e.Message, e.SocketErrorCode);
            return (false, false);
        }

        Logger.LogInformation(
            "Listening for frameeyeosc on UDP port {Port}. This PC's IPv4 addresses: {Addresses}",
            _receiver.Port, string.Join(", ", GetLocalIPv4Addresses().DefaultIfEmpty("(none found)")));

        _writer.Write(UnifiedTracking.Data, EyeFrame.Neutral);
        return (true, false);
    }

    /// <inheritdoc />
    public override void Update()
    {
        var receiver = _receiver;
        if (receiver is null)
        {
            // VRCFT runs the update loop even when Initialize failed; don't spin.
            Thread.Sleep(PollWait);
            return;
        }

        try
        {
            var packet = receiver.Poll(PollWait);
            if (packet is { } p)
                OnPacket(receiver, p);
            else if (!_relaxedForTimeout && receiver.IsTimedOut)
                OnTimeout();
        }
        catch (Exception e)
        {
            // An exception escaping Update would end VRCFT's update thread for good. Log each distinct error once.
            if (e.Message != _lastUpdateError)
                Logger.LogError(e, "FrameEyeOSC update failed.");
            _lastUpdateError = e.Message;
            // Still relax the eyes once the packets stop, instead of leaving them frozen on the last values.
            if (!_relaxedForTimeout && receiver.IsTimedOut)
                OnTimeout();
            Thread.Sleep(PollWait);
        }
    }

    /// <inheritdoc />
    public override void Teardown()
    {
        var receiver = Interlocked.Exchange(ref _receiver, null);
        receiver?.Dispose();
    }

    private void OnPacket(FrameEyeOscReceiver receiver, in FeoPacket packet)
    {
        var sender = receiver.LastSender;
        if (_relaxedForTimeout || !Equals(sender, _loggedSender))
        {
            Logger.LogInformation("Receiving from frameeyeosc at {Sender}.", sender);
            _loggedSender = sender;
        }
        _relaxedForTimeout = false;

        if (_lastSession is { } last && last != packet.Session)
            Logger.LogInformation("frameeyeosc started again (new session).");
        _lastSession = packet.Session;

        if (_lastTracking != packet.Tracking)
        {
            // The first packet only logs when it isn't tracking; "receiving from" already covers a live start.
            if (_lastTracking is not null || !packet.Tracking)
            {
                Logger.LogInformation(packet.Tracking
                    ? "frameeyeosc is tracking the eyes."
                    : "frameeyeosc has no eye data (headset off or its eye server stopped); eyes relaxed.");
            }
            _lastTracking = packet.Tracking;
        }

        _writer.Write(UnifiedTracking.Data, EyeMapper.Map(packet));
    }

    private void OnTimeout()
    {
        Logger.Log(TimeoutLogLevel(_lastTracking), "No packets from frameeyeosc for 1 s; eyes relaxed.");
        _writer.Write(UnifiedTracking.Data, EyeFrame.Neutral);
        _relaxedForTimeout = true;
        _lastTracking = null;
    }

    /// <summary>
    /// How loud the timeout is: packets stopping while frameeyeosc was tracking is a problem (a warning); after it said it
    /// had no eye data (paused, or the headset off before it stopped) it is expected.
    /// </summary>
    /// <param name="lastTracking">Whether the last packet was tracking; null when none came since the last timeout.</param>
    /// <returns>The log level.</returns>
    internal static LogLevel TimeoutLogLevel(bool? lastTracking) =>
        lastTracking == false ? LogLevel.Information : LogLevel.Warning;

    private void OnBadPacket(string reason, IPEndPoint sender) =>
        Logger.LogWarning("Ignoring a datagram from {Sender}: {Reason}.", sender, reason);

    private FrameEyeOscConfig LoadConfig()
    {
        var directory = Path.GetDirectoryName(typeof(FrameEyeOscModule).Assembly.Location);
        if (string.IsNullOrEmpty(directory))
            directory = AppContext.BaseDirectory;

        var config = FrameEyeOscConfig.Load(Path.Combine(directory, FrameEyeOscConfig.FileName), out var warning);
        if (warning is not null)
            Logger.LogWarning("{Warning}", warning);
        else if (config != FrameEyeOscConfig.Default)
            Logger.LogInformation("Using {ConfigFile}: port {Port}.", FrameEyeOscConfig.FileName, config.Port);
        return config;
    }

    private static IEnumerable<string> GetLocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return ["(unknown)"];
        }
    }
}
