namespace FrameEyeOSC;

/// <summary>
/// The spec's ordering rule: a packet from another session than the newest one taken is frameeyeosc started again, and
/// is taken; from then on, one from a session before (the last <see cref="RetiredSessions"/> of them) is a straggler from
/// an old run and is dropped. Within a session, a packet up to 1000 behind the newest one taken
/// (<c>(int)(sequence - newest)</c> in -1000..0) is a duplicate or arrived late and is dropped; anything else is taken.
/// A jump back further than that is also a restart (for senders without a session), and after a timeout any packet is
/// taken.
/// </summary>
public sealed class SequenceFilter
{
    /// <summary>How far behind the newest sequence a packet still counts as late rather than a restart.</summary>
    public const int LateWindow = 1000;

    /// <summary>How many sessions before the current one still have their stragglers dropped.</summary>
    public const int RetiredSessions = 4;

    private readonly uint[] _retired = new uint[RetiredSessions];
    private int _nextRetired;
    private uint _newest;
    private uint _session;

    /// <summary>Whether any packet has been taken since construction or <see cref="Reset"/>.</summary>
    public bool HasNewest { get; private set; }

    /// <summary>The newest sequence taken (meaningful only when <see cref="HasNewest"/>).</summary>
    public uint Newest => _newest;

    /// <summary>Decides whether to take a packet and, if so, makes it the newest.</summary>
    /// <param name="sequence">The packet's sequence number.</param>
    /// <param name="timedOut">True when no packet was taken for the timeout; then any packet is taken.</param>
    /// <param name="session">The packet's session (0 when it has none).</param>
    /// <returns>True when the packet is taken.</returns>
    public bool Accept(uint sequence, bool timedOut = false, uint session = 0)
    {
        if (HasNewest && !timedOut && session != _session && session != 0 && Array.IndexOf(_retired, session) >= 0)
            return false;
        if (HasNewest && !timedOut && session == _session)
        {
            // Unsigned subtraction then a signed view handles the u32 wrap-around.
            var delta = unchecked((int)(sequence - _newest));
            if (delta is >= -LateWindow and <= 0)
                return false;
        }

        if (HasNewest && session != _session)
        {
            // Several, not just the one before: after two quick restarts, a late packet from the first run must not
            // take over from the newest.
            _retired[_nextRetired] = _session;
            _nextRetired = (_nextRetired + 1) % RetiredSessions;
        }
        _newest = sequence;
        _session = session;
        HasNewest = true;
        return true;
    }

    /// <summary>Forgets the newest sequence so the next packet is taken.</summary>
    public void Reset() => HasNewest = false;
}
