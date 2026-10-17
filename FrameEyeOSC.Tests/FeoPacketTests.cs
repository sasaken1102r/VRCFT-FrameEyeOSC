using System.Buffers.Binary;

namespace FrameEyeOSC.Tests;

public class FeoPacketTests
{
    // docs/protocol.md, "Example".
    internal const string ExampleHex =
        "46454f3101004c00070000001b00000040222018240a0600" +
        "0000803e000000bf0000603f0000003f000080400000803e" +
        "0000003e000000bf0000c03e00000000000060400000403f" +
        "c3115f2a";

    internal static byte[] ExampleBytes => Convert.FromHexString(ExampleHex);

    internal static FeoPacket ExamplePacket => new(
        7,
        FeoFlags.Tracking | FeoFlags.LeftCameraLid | FeoFlags.LeftPupil | FeoFlags.RightPupil,
        1700000000123456,
        new FeoEye(0.25f, -0.5f, 0.875f, 0.5f, 4.0f, 0.25f),
        new FeoEye(0.125f, -0.5f, 0.375f, 0.0f, 3.5f, 0.75f),
        0x2a5f11c3);

    [Fact]
    public void Example_parses_to_the_spec_values()
    {
        Assert.True(FeoPacket.TryParse(ExampleBytes, out var p, out var error));
        Assert.Null(error);

        Assert.Equal(7u, p.Sequence);
        Assert.Equal(27u, (uint)p.Flags);
        Assert.Equal(1700000000123456ul, p.TimeUs);
        Assert.Equal(new FeoEye(0.25f, -0.5f, 0.875f, 0.5f, 4.0f, 0.25f), p.Left);
        Assert.Equal(new FeoEye(0.125f, -0.5f, 0.375f, 0.0f, 3.5f, 0.75f), p.Right);
        Assert.Equal(0x2a5f11c3u, p.Session);

        Assert.True(p.Tracking);
        Assert.True(p.CameraLid(left: true));
        Assert.False(p.CameraLid(left: false));
        Assert.True(p.Pupil(left: true));
        Assert.True(p.Pupil(left: false));
    }

    [Fact]
    public void Example_encodes_to_the_spec_bytes()
    {
        Assert.Equal(ExampleHex, Convert.ToHexString(ExamplePacket.ToBytes()).ToLowerInvariant());
    }

    [Theory]
    [InlineData(72)]
    [InlineData(74)]
    public void A_packet_without_a_session_reads_as_session_0(int length)
    {
        // The first draft (72 bytes) had no session; a length that stops inside it doesn't count either.
        var bytes = ExampleBytes[..length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)length);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out var error));
        Assert.Null(error);
        Assert.Equal(ExamplePacket with { Session = 0 }, p);
    }

    [Fact]
    public void Trailing_bytes_are_ignored()
    {
        // A later compatible version: longer length, extra fields appended.
        var bytes = ExampleBytes.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 80);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.Equal(ExamplePacket, p);

        // Extra bytes beyond length too.
        var padded = ExampleBytes.Concat(new byte[16]).ToArray();
        Assert.True(FeoPacket.TryParse(padded, out p, out _));
        Assert.Equal(ExamplePacket, p);
    }

    [Fact]
    public void Reserved_flag_bits_are_kept_but_ignored()
    {
        var bytes = ExampleBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0xFFFF_FF00u | 1u);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.True(p.Tracking);
        Assert.False(p.CameraLid(left: true));
        Assert.False(p.Pupil(left: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void Shorter_than_24_bytes_is_rejected(int length)
    {
        AssertRejected(ExampleBytes.AsSpan(0, length).ToArray());
    }

    [Fact]
    public void Bad_magic_is_rejected()
    {
        var bytes = ExampleBytes;
        bytes[3] = (byte)'2';
        AssertRejected(bytes);
    }

    [Fact]
    public void Version_2_is_rejected()
    {
        var bytes = ExampleBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        AssertRejected(bytes);
    }

    [Fact]
    public void Length_71_is_rejected()
    {
        var bytes = ExampleBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 71);
        AssertRejected(bytes);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(71)]
    [InlineData(75)]
    public void Datagram_shorter_than_length_is_rejected(int datagramLength)
    {
        AssertRejected(ExampleBytes.AsSpan(0, datagramLength).ToArray());
    }

    [Fact]
    public void Datagram_shorter_than_a_raised_length_is_rejected()
    {
        var bytes = ExampleBytes.Concat(new byte[4]).ToArray(); // 80 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 84);
        AssertRejected(bytes);
    }

    [Theory]
    [InlineData(24, float.NaN)]               // left gaze_x
    [InlineData(32, float.PositiveInfinity)]  // left eyelid
    [InlineData(44, float.NegativeInfinity)]  // left pupil_dilation
    [InlineData(48, float.NaN)]               // right gaze_x
    [InlineData(64, float.PositiveInfinity)]  // right pupil_mm
    [InlineData(68, float.NaN)]               // right pupil_dilation
    public void Non_finite_float_is_rejected(int offset, float value)
    {
        var bytes = ExampleBytes;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        AssertRejected(bytes);
    }

    // The example with brows appended: flags + LEFT_BROW | RIGHT_BROW (123), length 108.
    internal static FeoPacket BrowPacket => ExamplePacket with
    {
        Flags = ExamplePacket.Flags | FeoFlags.LeftBrow | FeoFlags.RightBrow,
        Brows = new FeoBrows(new FeoBrow(0.5f, 0.25f, 0f, 0.125f), new FeoBrow(1f, 0.75f, 0.0625f, 0f)),
    };

    private static byte[] BrowBytes => BrowPacket.ToBytes();

    [Fact]
    public void A_108_byte_packet_parses_with_brows()
    {
        var bytes = BrowBytes;
        Assert.Equal(108, bytes.Length);
        Assert.Equal(108, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        // The first 76 bytes are the example's, apart from length and flags.
        Assert.Equal(ExampleBytes.AsSpan(16, 60).ToArray(), bytes.AsSpan(16, 60).ToArray());
        // The left brow at 76, the right at 92: inner_up, outer_up, lowerer, pinch.
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(76)));
        Assert.Equal(0.125f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(88)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(92)));
        Assert.Equal(0.0625f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(100)));

        Assert.True(FeoPacket.TryParse(bytes, out var p, out var error));
        Assert.Null(error);
        Assert.Equal(BrowPacket, p);
        Assert.True(p.Brow(left: true));
        Assert.True(p.Brow(left: false));
    }

    [Fact]
    public void A_76_byte_packet_has_no_brows()
    {
        Assert.True(FeoPacket.TryParse(ExampleBytes, out var p, out _));
        Assert.Null(p.Brows);
        Assert.Equal(76, p.EncodedLength);

        // Even with the brow flags set, a packet without the block has no brows.
        var bytes = ExampleBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 27u | 32u | 64u);
        Assert.True(FeoPacket.TryParse(bytes, out p, out _));
        Assert.Null(p.Brows);
        Assert.False(p.Brow(left: true));
        Assert.False(p.Brow(left: false));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(107)]
    public void Lengths_below_108_have_no_brows_and_ignore_the_rest(int length)
    {
        // 72..107 behave as before: what follows session is not read, even when it looks like brows.
        var bytes = BrowBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)length);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(100), float.NaN);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.Equal(BrowPacket with { Brows = null }, p);
        Assert.False(p.Brow(left: true));
    }

    [Theory]
    [InlineData(76)]
    [InlineData(92)]
    [InlineData(107)]
    public void Length_108_in_a_shorter_datagram_is_rejected(int datagramLength)
    {
        AssertRejected(BrowBytes.AsSpan(0, datagramLength).ToArray());
    }

    [Fact]
    public void Bytes_after_108_are_ignored()
    {
        var bytes = BrowBytes.Concat(new byte[] { 0xff, 0xff, 0xc0, 0x7f }).ToArray(); // a NaN after the brows
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 112);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.Equal(BrowPacket, p);
    }

    [Theory]
    [InlineData(76, float.NaN)]               // left brow_inner_up
    [InlineData(88, float.PositiveInfinity)]  // left brow_pinch
    [InlineData(92, float.NegativeInfinity)]  // right brow_inner_up
    [InlineData(104, float.NaN)]              // right brow_pinch
    public void Non_finite_brow_is_rejected(int offset, float value)
    {
        var bytes = BrowBytes;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        AssertRejected(bytes);
    }

    [Fact]
    public void A_non_finite_brow_drops_the_packet_even_without_the_brow_flags()
    {
        var bytes = BrowBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 27u);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(96), float.NaN);
        AssertRejected(bytes);
    }

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(32u, true, false)]
    [InlineData(64u, false, true)]
    [InlineData(96u, true, true)]
    public void Brow_flags_are_per_eye(uint browBits, bool left, bool right)
    {
        var bytes = BrowBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1u | browBits);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.NotNull(p.Brows);
        Assert.Equal(left, p.Brow(left: true));
        Assert.Equal(right, p.Brow(left: false));
        // The other flags don't count as brow flags, nor brow flags as others.
        Assert.False(p.CameraLid(left: true));
        Assert.False(p.Pupil(left: false));
    }

    [Fact]
    public void Reserved_bits_7_to_31_are_not_brow_flags()
    {
        var bytes = BrowBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0xFFFF_FF80u | 1u);
        Assert.True(FeoPacket.TryParse(bytes, out var p, out _));
        Assert.False(p.Brow(left: true));
        Assert.False(p.Brow(left: false));
    }

    private static void AssertRejected(byte[] bytes)
    {
        Assert.False(FeoPacket.TryParse(bytes, out var p, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(default, p);
    }
}
