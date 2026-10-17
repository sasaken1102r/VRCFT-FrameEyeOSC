namespace FrameEyeOSC.Tests;

public class SequenceFilterTests
{
    [Fact]
    public void First_packet_is_taken_whatever_its_sequence()
    {
        var f = new SequenceFilter();
        Assert.False(f.HasNewest);
        Assert.True(f.Accept(123456));
        Assert.Equal(123456u, f.Newest);
    }

    [Fact]
    public void In_order_packets_are_taken()
    {
        var f = new SequenceFilter();
        for (uint s = 0; s < 10; s++)
            Assert.True(f.Accept(s));
        Assert.True(f.Accept(15)); // a gap (lost packets) is fine
        Assert.Equal(15u, f.Newest);
    }

    [Fact]
    public void Duplicate_is_dropped()
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(5));
        Assert.False(f.Accept(5));
        Assert.Equal(5u, f.Newest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Late_within_1000_is_dropped(int behind)
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(5000));
        Assert.False(f.Accept((uint)(5000 - behind)));
        Assert.Equal(5000u, f.Newest);
    }

    [Fact]
    public void Jump_back_beyond_1000_is_a_restart_and_taken()
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(5000));
        Assert.True(f.Accept(3999)); // 1001 behind
        Assert.True(f.Accept(4000));

        var g = new SequenceFilter();
        Assert.True(g.Accept(70000));
        Assert.True(g.Accept(0)); // frameeyeosc restarted
        Assert.True(g.Accept(1));
        Assert.Equal(1u, g.Newest);
    }

    [Fact]
    public void Wrap_around_is_in_order()
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(0xFFFF_FFFEu));
        Assert.True(f.Accept(0xFFFF_FFFFu));
        Assert.True(f.Accept(0));
        Assert.True(f.Accept(1));
        Assert.False(f.Accept(0xFFFF_FFFFu)); // late across the wrap
        Assert.False(f.Accept(0xFFFF_FC19u)); // exactly 1000 behind 1 across the wrap
        Assert.True(f.Accept(0xFFFF_FC18u));  // 1001 behind: restart
    }

    [Fact]
    public void After_a_timeout_any_packet_is_taken()
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(5000));
        Assert.False(f.Accept(4990));
        Assert.True(f.Accept(4990, timedOut: true));
        Assert.Equal(4990u, f.Newest);
        Assert.False(f.Accept(4990)); // and the rule resumes from there
    }

    [Fact]
    public void A_new_session_starts_the_window_over()
    {
        // frameeyeosc started again within seconds: its sequence is back near 0, well inside the late window
        var f = new SequenceFilter();
        Assert.True(f.Accept(500, session: 7));
        Assert.True(f.Accept(501, session: 7));
        Assert.False(f.Accept(400, session: 7)); // late within the session
        Assert.True(f.Accept(0, session: 9));    // the new run
        Assert.Equal(0u, f.Newest);
        Assert.True(f.Accept(1, session: 9));
        Assert.False(f.Accept(1, session: 9));   // and the rule holds within it
        Assert.False(f.Accept(0, session: 9));
        // A straggler from the old run doesn't take over again
        Assert.False(f.Accept(502, session: 7));
        Assert.True(f.Accept(2, session: 9));
        // ...unless nothing came for the timeout
        Assert.True(f.Accept(503, timedOut: true, session: 7));
    }

    [Fact]
    public void After_two_restarts_a_straggler_from_the_first_run_is_dropped()
    {
        // A -> B -> C within seconds; a late packet from A must not retire C (C's packets would then be dropped until
        // the timeout).
        var f = new SequenceFilter();
        Assert.True(f.Accept(500, session: 10));  // A
        Assert.True(f.Accept(0, session: 11));    // B
        Assert.True(f.Accept(0, session: 12));    // C
        Assert.True(f.Accept(1, session: 12));
        Assert.False(f.Accept(501, session: 10)); // straggler from A
        Assert.False(f.Accept(1, session: 11));   // and from B
        Assert.True(f.Accept(2, session: 12));
        Assert.Equal(2u, f.Newest);

        // The last few runs are remembered; a much older one counts as new again.
        for (uint s = 13; s < 13 + SequenceFilter.RetiredSessions; s++)
            Assert.True(f.Accept(0, session: s));
        Assert.False(f.Accept(3, session: 12));
        Assert.True(f.Accept(502, session: 10));
    }

    [Fact]
    public void Without_a_session_a_restart_inside_the_window_waits_for_the_timeout()
    {
        // (the first draft: session 0 on both sides)
        var f = new SequenceFilter();
        Assert.True(f.Accept(500));
        Assert.False(f.Accept(0));
        Assert.True(f.Accept(0, timedOut: true));
    }

    [Fact]
    public void Reset_takes_the_next_packet()
    {
        var f = new SequenceFilter();
        Assert.True(f.Accept(10));
        f.Reset();
        Assert.True(f.Accept(10));
    }
}
