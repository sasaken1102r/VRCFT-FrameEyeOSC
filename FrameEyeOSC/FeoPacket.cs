using System.Buffers.Binary;

namespace FrameEyeOSC;

/// <summary>Flag bits of a FEO1 packet (docs/protocol.md, "Flags").</summary>
[Flags]
public enum FeoFlags : uint
{
    /// <summary>No flag set: no eye data, the values are relaxed open eyes looking ahead.</summary>
    None = 0,

    /// <summary>The eye values are live.</summary>
    Tracking = 1 << 0,

    /// <summary>The left eyelid comes from the eye camera and the left <c>squint</c> is valid.</summary>
    LeftCameraLid = 1 << 1,

    /// <summary>The right eyelid comes from the eye camera and the right <c>squint</c> is valid.</summary>
    RightCameraLid = 1 << 2,

    /// <summary>The left <c>pupil_mm</c> and <c>pupil_dilation</c> are valid.</summary>
    LeftPupil = 1 << 3,

    /// <summary>The right <c>pupil_mm</c> and <c>pupil_dilation</c> are valid.</summary>
    RightPupil = 1 << 4,

    /// <summary>The left eye's brow fields are valid (used only from a packet that has the brows block).</summary>
    LeftBrow = 1 << 5,

    /// <summary>The right eye's brow fields are valid (used only from a packet that has the brows block).</summary>
    RightBrow = 1 << 6,
}

/// <summary>One eye of a FEO1 packet, as sent (no clamping).</summary>
/// <param name="GazeX">Sideways gaze, + right; 1 = 45°.</param>
/// <param name="GazeY">Up/down gaze, + up; 1 = 45°.</param>
/// <param name="Eyelid">VRCFT eyelid: 0 closed, 0.75 relaxed open, 1 fully widened.</param>
/// <param name="Squint">Eye-camera squint 0..1; only meaningful with the eye's camera-lid flag.</param>
/// <param name="PupilMm">Pupil diameter in millimetres; only meaningful with the eye's pupil flag.</param>
/// <param name="PupilDilation">Pupil dilation 0..1 for this wearer; only meaningful with the eye's pupil flag.</param>
public readonly record struct FeoEye(
    float GazeX,
    float GazeY,
    float Eyelid,
    float Squint,
    float PupilMm,
    float PupilDilation);

/// <summary>One eye's brow, as sent (no clamping); each value 0..1.</summary>
/// <param name="InnerUp">brow_inner_up, for VRCFT's <c>BrowInnerUp</c>.</param>
/// <param name="OuterUp">brow_outer_up, for VRCFT's <c>BrowOuterUp</c>.</param>
/// <param name="Lowerer">brow_lowerer, for VRCFT's <c>BrowLowerer</c>.</param>
/// <param name="Pinch">brow_pinch, for VRCFT's <c>BrowPinch</c>.</param>
public readonly record struct FeoBrow(float InnerUp, float OuterUp, float Lowerer, float Pinch);

/// <summary>The brows block of a packet (offset 76); each eye's brow is valid only with that eye's brow flag.</summary>
/// <param name="Left">The left eye's brow.</param>
/// <param name="Right">The right eye's brow.</param>
public readonly record struct FeoBrows(FeoBrow Left, FeoBrow Right);

/// <summary>
/// A FEO1 version 1 packet from frameeyeosc. Parsing and encoding follow docs/protocol.md; this type has no
/// VRCFaceTracking dependency so it can be tested on its own.
/// </summary>
/// <param name="Sequence">+1 per packet, wraps; 0 when frameeyeosc starts.</param>
/// <param name="Flags">The flag bits, reserved bits included as received.</param>
/// <param name="TimeUs">Sender's wall clock in microseconds since the Unix epoch (diagnostics only).</param>
/// <param name="Left">The left eye.</param>
/// <param name="Right">The right eye.</param>
/// <param name="Session">
/// Random and not 0, new each time frameeyeosc starts; 0 when the packet has none (a 72-byte first-draft packet).
/// </param>
/// <param name="Brows">The brows block; null when the packet has none (<c>length</c> below 108).</param>
public readonly record struct FeoPacket(
    uint Sequence,
    FeoFlags Flags,
    ulong TimeUs,
    FeoEye Left,
    FeoEye Right,
    uint Session = 0,
    FeoBrows? Brows = null)
{
    /// <summary>The ASCII magic <c>FEO1</c> as a little-endian u32.</summary>
    public const uint Magic = 0x314F4546;

    /// <summary>The only version this module understands.</summary>
    public const ushort Version = 1;

    /// <summary>Size of a version 1 packet without the brows block, as frameeyeosc sends it today.</summary>
    public const int Size = 76;

    /// <summary>Size of a version 1 packet with the brows block.</summary>
    public const int SizeWithBrows = 108;

    /// <summary>The smallest <c>length</c> accepted: the first draft, without <c>session</c>.</summary>
    public const int MinSize = 72;

    /// <summary>Bytes needed before <c>length</c> can be checked (the spec's minimum datagram).</summary>
    public const int HeaderSize = 24;

    private const int EyeSize = 24;
    private const int LeftOffset = 24;
    private const int RightOffset = 48;
    private const int SessionOffset = 72;
    private const int BrowSize = 16;
    private const int LeftBrowOffset = 76;
    private const int RightBrowOffset = 92;

    /// <summary>The <see cref="FeoFlags.Tracking"/> bit: the eye values are live.</summary>
    public bool Tracking => (Flags & FeoFlags.Tracking) != 0;

    /// <summary>Whether an eye's eyelid comes from the eye camera (its squint is valid).</summary>
    /// <param name="left">True for the left eye, false for the right.</param>
    /// <returns>True when that eye's camera-lid flag is set.</returns>
    public bool CameraLid(bool left) => (Flags & (left ? FeoFlags.LeftCameraLid : FeoFlags.RightCameraLid)) != 0;

    /// <summary>Whether an eye's pupil values are valid.</summary>
    /// <param name="left">True for the left eye, false for the right.</param>
    /// <returns>True when that eye's pupil flag is set.</returns>
    public bool Pupil(bool left) => (Flags & (left ? FeoFlags.LeftPupil : FeoFlags.RightPupil)) != 0;

    /// <summary>Whether an eye's brow values are valid: the packet has the brows block and the eye's brow flag is set.</summary>
    /// <param name="left">True for the left eye, false for the right.</param>
    /// <returns>True when that eye's brow can be used.</returns>
    public bool Brow(bool left) =>
        Brows is not null && (Flags & (left ? FeoFlags.LeftBrow : FeoFlags.RightBrow)) != 0;

    /// <summary>Bytes this packet encodes to: <see cref="SizeWithBrows"/> with brows, else <see cref="Size"/>.</summary>
    public int EncodedLength => Brows is null ? Size : SizeWithBrows;

    /// <summary>
    /// Parses one datagram by the spec's receiving rules: at least 24 bytes, magic <c>FEO1</c>, version 1,
    /// <c>length</c> at least 72, the datagram at least <c>length</c> long, every float finite. <c>session</c> is
    /// read when <c>length</c> covers it (else 0), the brows block when <c>length</c> is at least 108 (else null);
    /// bytes after the first 108 are ignored.
    /// </summary>
    /// <param name="datagram">The whole UDP payload.</param>
    /// <param name="packet">The packet when parsing succeeds, else default.</param>
    /// <param name="error">A short, stable reason when parsing fails (suitable for rate-limited logging), else null.</param>
    /// <returns>True when the datagram is a valid packet.</returns>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out FeoPacket packet, out string? error)
    {
        packet = default;

        if (datagram.Length < HeaderSize)
        {
            error = $"datagram shorter than {HeaderSize} bytes";
            return false;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic)
        {
            error = "not a FEO1 packet (bad magic)";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(datagram[4..]);
        if (version != Version)
        {
            error = $"unsupported FEO1 version {version} (this module reads version {Version})";
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]);
        if (length < MinSize)
        {
            error = $"FEO1 length {length} is below {MinSize}";
            return false;
        }
        if (datagram.Length < length)
        {
            error = "datagram shorter than its FEO1 length";
            return false;
        }

        if (!TryReadEye(datagram.Slice(LeftOffset, EyeSize), out var left) ||
            !TryReadEye(datagram.Slice(RightOffset, EyeSize), out var right))
        {
            error = "FEO1 packet with a non-finite value";
            return false;
        }

        FeoBrows? brows = null;
        if (length >= SizeWithBrows)
        {
            if (!TryReadBrow(datagram.Slice(LeftBrowOffset, BrowSize), out var leftBrow) ||
                !TryReadBrow(datagram.Slice(RightBrowOffset, BrowSize), out var rightBrow))
            {
                error = "FEO1 packet with a non-finite value";
                return false;
            }
            brows = new FeoBrows(leftBrow, rightBrow);
        }

        packet = new FeoPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]),
            (FeoFlags)BinaryPrimitives.ReadUInt32LittleEndian(datagram[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[16..]),
            left,
            right,
            length >= SessionOffset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(datagram[SessionOffset..]) : 0,
            brows);
        error = null;
        return true;
    }

    /// <summary>
    /// Writes this packet as a version 1 datagram (used by tests and the sender tool): 76 bytes, or 108 with brows.
    /// </summary>
    /// <param name="destination">At least <see cref="EncodedLength"/> bytes.</param>
    /// <exception cref="ArgumentException">The destination is shorter than <see cref="EncodedLength"/>.</exception>
    public void WriteTo(Span<byte> destination)
    {
        var length = EncodedLength;
        if (destination.Length < length)
            throw new ArgumentException($"Need {length} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], TimeUs);
        WriteEye(destination.Slice(LeftOffset, EyeSize), Left);
        WriteEye(destination.Slice(RightOffset, EyeSize), Right);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SessionOffset..], Session);
        if (Brows is { } brows)
        {
            WriteBrow(destination.Slice(LeftBrowOffset, BrowSize), brows.Left);
            WriteBrow(destination.Slice(RightBrowOffset, BrowSize), brows.Right);
        }
    }

    /// <summary>Encodes this packet into a new array of <see cref="EncodedLength"/> bytes.</summary>
    /// <returns>The datagram bytes.</returns>
    public byte[] ToBytes()
    {
        var bytes = new byte[EncodedLength];
        WriteTo(bytes);
        return bytes;
    }

    private static bool TryReadEye(ReadOnlySpan<byte> bytes, out FeoEye eye)
    {
        Span<float> v = stackalloc float[6];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            if (!float.IsFinite(v[i]))
            {
                eye = default;
                return false;
            }
        }
        eye = new FeoEye(v[0], v[1], v[2], v[3], v[4], v[5]);
        return true;
    }

    private static bool TryReadBrow(ReadOnlySpan<byte> bytes, out FeoBrow brow)
    {
        Span<float> v = stackalloc float[4];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            if (!float.IsFinite(v[i]))
            {
                brow = default;
                return false;
            }
        }
        brow = new FeoBrow(v[0], v[1], v[2], v[3]);
        return true;
    }

    private static void WriteBrow(Span<byte> bytes, FeoBrow brow)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, brow.InnerUp);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], brow.OuterUp);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], brow.Lowerer);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], brow.Pinch);
    }

    private static void WriteEye(Span<byte> bytes, FeoEye eye)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, eye.GazeX);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], eye.GazeY);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], eye.Eyelid);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], eye.Squint);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], eye.PupilMm);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], eye.PupilDilation);
    }
}
