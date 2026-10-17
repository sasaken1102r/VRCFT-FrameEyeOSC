using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace FrameEyeOSC.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// A socket that hands out queued datagrams and socket errors in order, for errors real UDP on loopback can't produce.
/// </summary>
internal sealed class FakeDatagramSocket : IDatagramSocket
{
    private readonly Queue<object> _queue = new(); // byte[] datagrams and SocketExceptions

    public static readonly IPEndPoint Sender = new(IPAddress.Loopback, 40000);

    /// <summary>When set, every <see cref="Poll"/> throws it.</summary>
    public SocketException? PollError { get; set; }

    /// <summary>When set, the socket always reads as ready, nothing is queued, and every read throws it.</summary>
    public SocketException? StuckError { get; set; }

    /// <summary>When set, the socket always says data is queued, and every read throws it.</summary>
    public SocketException? FloodError { get; set; }

    public int Reads { get; private set; }

    public int Port => 11112;

    public int Available => FloodError is not null ? 1 : _queue.Count;

    public void Enqueue(params object[] items)
    {
        foreach (var item in items)
            _queue.Enqueue(item);
    }

    public bool Poll(TimeSpan wait)
    {
        if (PollError is not null)
            throw PollError;
        return StuckError is not null || FloodError is not null || _queue.Count > 0;
    }

    public int ReceiveFrom(byte[] buffer, out IPEndPoint from)
    {
        Reads++;
        from = Sender;
        if ((StuckError ?? FloodError) is { } error)
            throw error;
        switch (_queue.Dequeue())
        {
            case SocketException e:
                throw e;
            case byte[] datagram:
                datagram.CopyTo(buffer, 0);
                return datagram.Length;
            default:
                throw new InvalidOperationException();
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Keeps every log line.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception)));
}
