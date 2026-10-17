// Sends synthetic FEO1 packets (docs/protocol.md) so the FrameEyeOSC module can be tried without a Steam Frame:
// a slow look left/right, a blink every 4 s, wide eyes and a squint now and then, pupils slowly changing.
//
//   dotnet run --project tools/FeoSend -- [host] [port] [seconds] [--brows | --brows=<value>]
//   (defaults: 127.0.0.1 11112, until Ctrl+C, no brows)
//
//   --brows          also send brows (108-byte packets): raised (inner and outer up), then lowered (lowerer and
//                    pinch), over 8 s
//   --brows=<value>  also send brows, raised (inner and outer up) by a constant 0..1

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FrameEyeOSC;

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var host = positional.Length > 0 ? positional[0] : "127.0.0.1";
var port = positional.Length > 1 ? int.Parse(positional[1]) : 11112;
double? seconds = positional.Length > 2 ? double.Parse(positional[2], CultureInfo.InvariantCulture) : null;

// null: no brows; NaN: moving brows; else a constant raise.
float? brows = null;
foreach (var option in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
{
    if (option == "--brows")
    {
        brows = float.NaN;
    }
    else if (option.StartsWith("--brows=", StringComparison.Ordinal))
    {
        brows = float.Parse(option["--brows=".Length..], CultureInfo.InvariantCulture);
    }
    else
    {
        Console.Error.WriteLine($"Unknown option {option}. Usage: [host] [port] [seconds] [--brows | --brows=<value>]");
        return 2;
    }
}

var target = new IPEndPoint(IPAddress.Parse(host), port);
using var udp = new UdpClient(AddressFamily.InterNetwork);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var browsText = brows switch
{
    null => "",
    float b when float.IsNaN(b) => " with moving brows",
    float b => $" with brows raised {b.ToString(CultureInfo.InvariantCulture)}",
};
Console.WriteLine($"Sending FEO1 packets to {target} at 90 Hz{browsText}{(seconds is { } s ? $" for {s} s" : "")}; Ctrl+C stops.");

const double Rate = 90;
var buffer = new byte[FeoPacket.SizeWithBrows];
var clock = Stopwatch.StartNew();
uint sequence = 0;
// New for each run, like frameeyeosc's: the module takes a restarted sender at once.
var session = (uint)Random.Shared.Next(1, int.MaxValue);

while (!cts.IsCancellationRequested)
{
    var t = clock.Elapsed.TotalSeconds;
    if (seconds is { } limit && t >= limit)
        break;

    var packet = Sample(sequence++, t, session, brows);
    packet.WriteTo(buffer);
    udp.Send(buffer, packet.EncodedLength, target);

    if (sequence % 90 == 0)
    {
        var browText = packet.Brows is { } pb ? $"  brow up {pb.Left.InnerUp:0.00} down {pb.Left.Lowerer:0.00}" : "";
        Console.Write($"\rseq {sequence,7}  gaze x {packet.Left.GazeX,6:0.00}  eyelid {packet.Left.Eyelid:0.00}  pupil {packet.Left.PupilMm:0.0} mm{browText} ");
    }

    // Pace to the rate without drifting.
    var next = TimeSpan.FromSeconds(sequence / Rate);
    var wait = next - clock.Elapsed;
    if (wait > TimeSpan.Zero)
        Thread.Sleep(wait);
}

// Like frameeyeosc when it stops: one last packet without TRACKING (relaxed eyes ahead).
var final = new FeoPacket(sequence, FeoFlags.None, UnixMicros(), Relaxed(), Relaxed(), session);
udp.Send(final.ToBytes(), FeoPacket.Size, target);
Console.WriteLine($"\nSent {sequence + 1} packets.");
return 0;

static FeoPacket Sample(uint sequence, double t, uint session, float? brows)
{
    // Look left/right over 6 s, a little up/down over 9 s.
    var gazeX = (float)(0.6 * Math.Sin(2 * Math.PI * t / 6));
    var gazeY = (float)(0.2 * Math.Sin(2 * Math.PI * t / 9));

    // Relaxed 0.75; a 0.3 s blink every 4 s; wide eyes (up to 1) from 10 s to 12 s of every 16 s.
    var eyelid = 0.75;
    var blink = t % 4;
    if (blink < 0.3)
        eyelid *= Math.Abs(blink - 0.15) / 0.15;
    var cycle = t % 16;
    if (cycle is >= 10 and < 12)
        eyelid = 0.75 + 0.25 * Math.Sin(Math.PI * (cycle - 10) / 2);

    // Squint from 13 s to 15 s of every 16 s.
    var squint = cycle is >= 13 and < 15 ? (float)Math.Sin(Math.PI * (cycle - 13) / 2) : 0f;

    // Pupils 2.5..5.5 mm over 8 s; dilation follows the same curve.
    var dilation = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 8);
    var pupilMm = (float)(2.5 + 3 * dilation);

    var flags = FeoFlags.Tracking | FeoFlags.LeftCameraLid | FeoFlags.RightCameraLid | FeoFlags.LeftPupil | FeoFlags.RightPupil;
    var eye = new FeoEye(gazeX, gazeY, (float)eyelid, squint, pupilMm, (float)dilation);
    if (brows is not { } raise)
        return new FeoPacket(sequence, flags, UnixMicros(), eye, eye, session);

    FeoBrow brow;
    if (float.IsNaN(raise))
    {
        // Raised for the first half of every 8 s, lowered and pinched for the second.
        var wave = (float)Math.Sin(2 * Math.PI * t / 8);
        var up = Math.Max(wave, 0f);
        var down = Math.Max(-wave, 0f);
        brow = new FeoBrow(up, up, down, down);
    }
    else
    {
        brow = new FeoBrow(raise, raise, 0f, 0f);
    }
    flags |= FeoFlags.LeftBrow | FeoFlags.RightBrow;
    return new FeoPacket(sequence, flags, UnixMicros(), eye, eye, session, new FeoBrows(brow, brow));
}

static FeoEye Relaxed() => new(0f, 0f, 0.75f, 0f, 0f, 0f);

static ulong UnixMicros() => (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
