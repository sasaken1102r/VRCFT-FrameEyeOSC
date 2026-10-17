namespace FrameEyeOSC;

/// <summary>One eye as VRCFT takes it.</summary>
/// <param name="GazeX">VRCFT <c>Gaze.x</c>, -1..1.</param>
/// <param name="GazeY">VRCFT <c>Gaze.y</c>, -1..1.</param>
/// <param name="Openness">VRCFT <c>Openness</c>, 0 closed .. 1 relaxed open.</param>
/// <param name="Wide">The <c>EyeWide</c> shape, 0..1.</param>
/// <param name="Squint">The <c>EyeSquint</c> shape, 0..1.</param>
public readonly record struct MappedEye(float GazeX, float GazeY, float Openness, float Wide, float Squint)
{
    /// <summary>Relaxed open eye looking ahead.</summary>
    public static MappedEye Neutral => new(0f, 0f, 1f, 0f, 0f);
}

/// <summary>Pupil values for VRCFT: both diameters and the dilation range VRCFT normalizes their mean with.</summary>
/// <param name="LeftMm">Left <c>PupilDiameter_MM</c>.</param>
/// <param name="RightMm">Right <c>PupilDiameter_MM</c>.</param>
/// <param name="MinDilation">VRCFT <c>_minDilation</c> (mm).</param>
/// <param name="MaxDilation">VRCFT <c>_maxDilation</c> (mm), never below <paramref name="MinDilation"/>.</param>
public readonly record struct MappedPupils(float LeftMm, float RightMm, float MinDilation, float MaxDilation);

/// <summary>One eye's brow shapes for VRCFT, each 0..1.</summary>
/// <param name="InnerUp">The <c>BrowInnerUp</c> shape.</param>
/// <param name="OuterUp">The <c>BrowOuterUp</c> shape.</param>
/// <param name="Lowerer">The <c>BrowLowerer</c> shape.</param>
/// <param name="Pinch">The <c>BrowPinch</c> shape.</param>
public readonly record struct MappedBrow(float InnerUp, float OuterUp, float Lowerer, float Pinch)
{
    /// <summary>A brow at rest: every shape 0.</summary>
    public static MappedBrow Rest => new(0f, 0f, 0f, 0f);
}

/// <summary>Everything the module writes to VRCFT for one packet.</summary>
/// <param name="Left">The left eye.</param>
/// <param name="Right">The right eye.</param>
/// <param name="Pupils">The pupils, or null to leave VRCFT's pupil values as they are.</param>
/// <param name="LeftBrow">The left brow, or null when the packet has none for that eye.</param>
/// <param name="RightBrow">The right brow, or null when the packet has none for that eye.</param>
public readonly record struct EyeFrame(
    MappedEye Left,
    MappedEye Right,
    MappedPupils? Pupils,
    MappedBrow? LeftBrow = null,
    MappedBrow? RightBrow = null)
{
    /// <summary>Relaxed open eyes looking ahead; the pupils stay; no brows.</summary>
    public static EyeFrame Neutral => new(MappedEye.Neutral, MappedEye.Neutral, null);
}

/// <summary>The spec's "How the module maps it to VRCFT", as a pure function.</summary>
public static class EyeMapper
{
    /// <summary>The eyelid value of a relaxed open eye; above it the eye widens.</summary>
    public const float RelaxedEyelid = 0.75f;

    /// <summary>
    /// Default width in mm of VRCFT's dilation range: eyecam's pupil-to-iris ratio 0.60..1.05 of a 5.9 mm iris radius.
    /// </summary>
    public const float DilationRangeMm = 2.655f;

    /// <summary>Maps a packet to VRCFT eye values.</summary>
    /// <param name="packet">A parsed packet.</param>
    /// <returns>The frame to write; <see cref="EyeFrame.Neutral"/> for a packet without TRACKING.</returns>
    public static EyeFrame Map(in FeoPacket packet)
    {
        if (!packet.Tracking)
            return EyeFrame.Neutral;

        return new EyeFrame(
            MapEye(packet.Left, packet.CameraLid(left: true)),
            MapEye(packet.Right, packet.CameraLid(left: false)),
            MapPupils(packet),
            MapBrow(packet, left: true),
            MapBrow(packet, left: false));
    }

    /// <summary>Maps one eye's brow, clamped to 0..1, while the packet has the brows block and the eye's brow flag.</summary>
    /// <param name="packet">A parsed packet.</param>
    /// <param name="left">True for the left eye, false for the right.</param>
    /// <returns>The brow shapes, or null when that eye has no valid brow.</returns>
    public static MappedBrow? MapBrow(in FeoPacket packet, bool left)
    {
        if (!packet.Brow(left) || packet.Brows is not { } brows)
            return null;

        var brow = left ? brows.Left : brows.Right;
        return new MappedBrow(
            Math.Clamp(brow.InnerUp, 0f, 1f),
            Math.Clamp(brow.OuterUp, 0f, 1f),
            Math.Clamp(brow.Lowerer, 0f, 1f),
            Math.Clamp(brow.Pinch, 0f, 1f));
    }

    /// <summary>Maps one eye's gaze, eyelid and squint.</summary>
    /// <param name="eye">The eye as sent.</param>
    /// <param name="cameraLid">Whether the eye's camera-lid flag is set (its squint is valid).</param>
    /// <returns>The VRCFT values for that eye.</returns>
    public static MappedEye MapEye(in FeoEye eye, bool cameraLid)
    {
        var eyelid = Math.Clamp(eye.Eyelid, 0f, 1f);
        return new MappedEye(
            Math.Clamp(eye.GazeX, -1f, 1f),
            Math.Clamp(eye.GazeY, -1f, 1f),
            // Split so VRCFT's v2/EyeLid (Openness * 0.75 + EyeWide * 0.25) gives the eyelid back.
            Math.Clamp(eyelid / RelaxedEyelid, 0f, 1f),
            Math.Clamp((eyelid - RelaxedEyelid) / (1f - RelaxedEyelid), 0f, 1f),
            cameraLid ? Math.Clamp(eye.Squint, 0f, 1f) : 0f);
    }

    /// <summary>
    /// Maps the pupils while at least one eye has PUPIL (an eye without it takes the other eye's values). The dilation
    /// range is placed around the mean diameter A so that VRCFT's <c>(A - min) / (max - min)</c> is the mean dilation D:
    /// <c>min = A - D * S</c>, <c>max = min + S</c>. If that round trip loses precision, a power-of-two range recovers D.
    /// </summary>
    /// <param name="packet">A parsed packet.</param>
    /// <returns>The pupil values, or null when neither eye has PUPIL.</returns>
    public static MappedPupils? MapPupils(in FeoPacket packet)
    {
        var hasLeft = packet.Pupil(left: true);
        var hasRight = packet.Pupil(left: false);
        if (!hasLeft && !hasRight)
            return null;

        var left = hasLeft ? packet.Left : packet.Right;
        var right = hasRight ? packet.Right : packet.Left;

        // Match Combined's arithmetic, retaining finite bounds even if pathological input overflows its sum.
        var meanMm = (left.PupilMm + right.PupilMm) * 0.5f;
        var finiteMean = float.IsFinite(meanMm);
        if (!finiteMean)
            meanMm = left.PupilMm * 0.5f + right.PupilMm * 0.5f;
        var meanDilation = (Math.Clamp(left.PupilDilation, 0f, 1f) + Math.Clamp(right.PupilDilation, 0f, 1f)) * 0.5f;
        var min = meanMm - meanDilation * DilationRangeMm;
        var max = min + DilationRangeMm;

        if (finiteMean && meanMm >= 0f && meanDilation > 0f && (meanMm - min) / (max - min) != meanDilation)
        {
            // Scaling by a power of two preserves D's bits. Put D * range between A and 2A so min <= 0 and
            // A - min recovers that scaled value exactly; (min + range) - min then rounds back to range.
            var range = meanMm == 0f ? 1f : MathF.ScaleB(1f, MathF.ILogB(meanMm) - MathF.ILogB(meanDilation));
            var scaled = meanDilation * range;
            if (scaled < meanMm)
            {
                range *= 2f;
                scaled *= 2f;
            }
            var exactMin = meanMm - scaled;
            var exactMax = exactMin + range;
            // Extreme finite protocol inputs can overflow the required range. Keep the original valid bounds then.
            if (float.IsFinite(exactMin) && float.IsFinite(exactMax) &&
                (meanMm - exactMin) / (exactMax - exactMin) == meanDilation)
            {
                min = exactMin;
                max = exactMax;
            }
        }

        // max >= min always: VRCFT drops the whole eye update otherwise.
        return new MappedPupils(left.PupilMm, right.PupilMm, min, max);
    }
}
