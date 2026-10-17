using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace FrameEyeOSC;

/// <summary>
/// Writes an <see cref="EyeFrame"/> into VRCFT's tracking data. It remembers which brows it has written, so a brow that
/// stops coming is set to 0 once instead of sticking; one instance per data it writes to.
/// </summary>
internal sealed class VrcftEyeWriter
{
    private bool _leftBrowWritten;
    private bool _rightBrowWritten;

    /// <summary>
    /// Writes gaze, openness, the EyeWide/EyeSquint shapes and, when present, the pupils and each eye's brow shapes.
    /// </summary>
    /// <param name="data">VRCFT's data (UnifiedTracking.Data in the module process).</param>
    /// <param name="frame">The values to write.</param>
    public void Write(UnifiedTrackingData data, in EyeFrame frame)
    {
        var eye = data.Eye;
        WriteEye(ref eye.Left, frame.Left);
        WriteEye(ref eye.Right, frame.Right);

        var shapes = data.Shapes;
        shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = frame.Left.Wide;
        shapes[(int)UnifiedExpressions.EyeWideRight].Weight = frame.Right.Wide;
        shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight = frame.Left.Squint;
        shapes[(int)UnifiedExpressions.EyeSquintRight].Weight = frame.Right.Squint;

        WriteBrow(shapes, frame.LeftBrow, left: true, ref _leftBrowWritten);
        WriteBrow(shapes, frame.RightBrow, left: false, ref _rightBrowWritten);

        if (frame.Pupils is not { } pupils)
            return; // Leave the pupils (and the dilation range) as they were.

        eye.Left.PupilDiameter_MM = pupils.LeftMm;
        eye.Right.PupilDiameter_MM = pupils.RightMm;

        // VRCFT's IPC thread reads these concurrently and drops the whole eye update while max < min. Moving the range
        // up, raise max first; moving it down, lower min first. Then every in-between state still has max >= min.
        if (pupils.MinDilation >= eye._minDilation)
        {
            eye._maxDilation = pupils.MaxDilation;
            eye._minDilation = pupils.MinDilation;
        }
        else
        {
            eye._minDilation = pupils.MinDilation;
            eye._maxDilation = pupils.MaxDilation;
        }
    }

    /// <summary>
    /// Writes one eye's 4 brow shapes. Without a brow they are left alone (invalid, so VRCFT doesn't copy them), unless
    /// this writer wrote them before: then they are set to 0 once.
    /// </summary>
    private static void WriteBrow(UnifiedExpressionShape[] shapes, MappedBrow? brow, bool left, ref bool written)
    {
        if (brow is not { } b)
        {
            if (!written)
                return;
            b = MappedBrow.Rest;
            written = false;
        }
        else
        {
            written = true;
        }

        shapes[(int)(left ? UnifiedExpressions.BrowInnerUpLeft : UnifiedExpressions.BrowInnerUpRight)].Weight = b.InnerUp;
        shapes[(int)(left ? UnifiedExpressions.BrowOuterUpLeft : UnifiedExpressions.BrowOuterUpRight)].Weight = b.OuterUp;
        shapes[(int)(left ? UnifiedExpressions.BrowLowererLeft : UnifiedExpressions.BrowLowererRight)].Weight = b.Lowerer;
        shapes[(int)(left ? UnifiedExpressions.BrowPinchLeft : UnifiedExpressions.BrowPinchRight)].Weight = b.Pinch;
    }

    private static void WriteEye(ref UnifiedSingleEyeData eye, in MappedEye mapped)
    {
        eye.Gaze.x = mapped.GazeX;
        eye.Gaze.y = mapped.GazeY;
        eye.Openness = mapped.Openness;
    }
}
