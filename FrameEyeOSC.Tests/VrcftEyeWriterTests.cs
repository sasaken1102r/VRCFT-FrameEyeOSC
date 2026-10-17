using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace FrameEyeOSC.Tests;

/// <summary>Runs the mapping through VRCFT's own data types and pupil normalization.</summary>
public class VrcftEyeWriterTests
{
    // Like ModuleProcessMain.InitializeUnifiedTrackingToInvalidState: fields the module never sets stay invalid.
    private const float Invalid = 0xFFFFFFFF;

    private static UnifiedTrackingData InvalidData()
    {
        var data = new UnifiedTrackingData();
        data.Eye.Left = new UnifiedSingleEyeData { Gaze = new(Invalid, Invalid), Openness = Invalid, PupilDiameter_MM = Invalid };
        data.Eye.Right = data.Eye.Left;
        data.Eye._maxDilation = Invalid;
        data.Eye._minDilation = Invalid;
        for (var i = 0; i < data.Shapes.Length; i++)
            data.Shapes[i].Weight = Invalid;
        return data;
    }

    [Fact]
    public void Example_packet_written_to_VRCFT()
    {
        var data = InvalidData();
        new VrcftEyeWriter().Write(data, EyeMapper.Map(FeoPacketTests.ExamplePacket));

        Assert.Equal(0.25f, data.Eye.Left.Gaze.x);
        Assert.Equal(-0.5f, data.Eye.Left.Gaze.y);
        Assert.Equal(1f, data.Eye.Left.Openness);
        Assert.Equal(0.125f, data.Eye.Right.Gaze.x);
        Assert.Equal(0.5f, data.Eye.Right.Openness);
        Assert.Equal(0.5f, data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight);
        Assert.Equal(0f, data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight);
        Assert.Equal(0.5f, data.Shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight);
        Assert.Equal(0f, data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight);
        Assert.Equal(4f, data.Eye.Left.PupilDiameter_MM);
        Assert.Equal(3.5f, data.Eye.Right.PupilDiameter_MM);
        Assert.True(data.Eye._maxDilation >= data.Eye._minDilation);

        // Only the eye shapes are touched; everything else stays invalid so VRCFT doesn't copy it.
        Assert.Equal(Invalid, data.Shapes[(int)UnifiedExpressions.JawOpen].Weight);
        Assert.Equal(Invalid, data.Shapes[(int)UnifiedExpressions.BrowInnerUpLeft].Weight);

        // VRCFT's v2/EyeLidLeft/Right formula gives the eyelids back.
        Assert.Equal(0.875f, data.Eye.Left.Openness * .75f + data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight * .25f, 1e-6f);
        Assert.Equal(0.375f, data.Eye.Right.Openness * .75f + data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight * .25f, 1e-6f);

        // VRCFT's v2/PupilDilation (UnifiedEyeData.Combined) is the mean dilation (0.25 + 0.75) / 2.
        Assert.Equal(0.5f, data.Eye.Combined().PupilDiameter_MM, 1e-5f);
    }

    [Theory]
    [InlineData(3f, 3f, 0f, 0f)]
    [InlineData(3f, 3f, 1f, 1f)]
    [InlineData(2.5f, 6.5f, 0.1f, 0.9f)]
    [InlineData(4f, 4.4f, 0.33f, 0.41f)]
    public void VRCFT_Combined_gives_the_mean_dilation(float leftMm, float rightMm, float leftD, float rightD)
    {
        var data = InvalidData();
        var packet = new FeoPacket(1, FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil, 0,
            new FeoEye(0, 0, 0.75f, 0, leftMm, leftD), new FeoEye(0, 0, 0.75f, 0, rightMm, rightD));
        new VrcftEyeWriter().Write(data, EyeMapper.Map(packet));

        Assert.Equal((leftD + rightD) / 2f, data.Eye.Combined().PupilDiameter_MM, 1e-5f);
    }

    [Fact]
    public void Pupils_follow_a_changing_stream_and_max_stays_above_min()
    {
        var data = InvalidData();
        for (var i = 0; i < 200; i++)
        {
            // Pupils swinging both ways, so the range moves up and down.
            var t = i / 10f;
            var d = 0.5f + 0.5f * MathF.Sin(t);
            var mm = 3f + 2f * MathF.Sin(t * 1.3f);
            var packet = new FeoPacket((uint)i, FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil, 0,
                new FeoEye(0, 0, 0.75f, 0, mm, d), new FeoEye(0, 0, 0.75f, 0, mm, d));
            new VrcftEyeWriter().Write(data, EyeMapper.Map(packet));

            Assert.True(data.Eye._maxDilation >= data.Eye._minDilation);
            Assert.Equal(d, data.Eye.Combined().PupilDiameter_MM, 1e-4f);
        }
    }

    [Fact]
    public void Neutral_relaxes_the_eyes_and_leaves_the_pupils()
    {
        var data = InvalidData();
        new VrcftEyeWriter().Write(data, EyeMapper.Map(FeoPacketTests.ExamplePacket));
        var (min, max) = (data.Eye._minDilation, data.Eye._maxDilation);

        new VrcftEyeWriter().Write(data, EyeFrame.Neutral);

        foreach (var eye in new[] { data.Eye.Left, data.Eye.Right })
        {
            Assert.Equal(0f, eye.Gaze.x);
            Assert.Equal(0f, eye.Gaze.y);
            Assert.Equal(1f, eye.Openness);
        }
        Assert.Equal(0f, data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight);
        Assert.Equal(0f, data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight);
        Assert.Equal(4f, data.Eye.Left.PupilDiameter_MM);
        Assert.Equal(3.5f, data.Eye.Right.PupilDiameter_MM);
        Assert.Equal(min, data.Eye._minDilation);
        Assert.Equal(max, data.Eye._maxDilation);
    }

    private static readonly UnifiedExpressions[] LeftBrowShapes =
    [
        UnifiedExpressions.BrowInnerUpLeft, UnifiedExpressions.BrowOuterUpLeft,
        UnifiedExpressions.BrowLowererLeft, UnifiedExpressions.BrowPinchLeft,
    ];

    private static readonly UnifiedExpressions[] RightBrowShapes =
    [
        UnifiedExpressions.BrowInnerUpRight, UnifiedExpressions.BrowOuterUpRight,
        UnifiedExpressions.BrowLowererRight, UnifiedExpressions.BrowPinchRight,
    ];

    private static float[] Weights(UnifiedTrackingData data, UnifiedExpressions[] shapes) =>
        shapes.Select(s => data.Shapes[(int)s].Weight).ToArray();

    private static FeoPacket BrowPacket(FeoFlags browFlags) =>
        FeoPacketTests.BrowPacket with { Flags = FeoFlags.Tracking | browFlags };

    [Fact]
    public void Brow_flags_write_the_4_shapes_per_eye()
    {
        var data = InvalidData();
        new VrcftEyeWriter().Write(data, EyeMapper.Map(BrowPacket(FeoFlags.LeftBrow | FeoFlags.RightBrow)));

        Assert.Equal([0.5f, 0.25f, 0f, 0.125f], Weights(data, LeftBrowShapes));
        Assert.Equal([1f, 0.75f, 0.0625f, 0f], Weights(data, RightBrowShapes));
        // Nothing else of the face.
        Assert.Equal(Invalid, data.Shapes[(int)UnifiedExpressions.JawOpen].Weight);
        Assert.Equal(Invalid, data.Shapes[(int)UnifiedExpressions.CheekSquintLeft].Weight);
    }

    [Fact]
    public void Without_the_flag_an_eye_s_brows_stay_invalid()
    {
        var data = InvalidData();
        var writer = new VrcftEyeWriter();

        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.LeftBrow)));
        Assert.Equal([0.5f, 0.25f, 0f, 0.125f], Weights(data, LeftBrowShapes));
        Assert.All(Weights(data, RightBrowShapes), w => Assert.Equal(Invalid, w));

        // A 76-byte packet (no brows block) and neutral frames don't touch brows never written.
        var other = InvalidData();
        var otherWriter = new VrcftEyeWriter();
        otherWriter.Write(other, EyeFrame.Neutral);
        otherWriter.Write(other, EyeMapper.Map(FeoPacketTests.ExamplePacket));
        otherWriter.Write(other, EyeMapper.Map(FeoPacketTests.ExamplePacket with { Flags = FeoFlags.Tracking | FeoFlags.LeftBrow | FeoFlags.RightBrow }));
        Assert.All(Weights(other, LeftBrowShapes.Concat(RightBrowShapes).ToArray()), w => Assert.Equal(Invalid, w));
    }

    [Fact]
    public void A_brow_that_loses_its_flag_is_zeroed_once()
    {
        var data = InvalidData();
        var writer = new VrcftEyeWriter();

        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.LeftBrow | FeoFlags.RightBrow)));
        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.RightBrow)));
        Assert.Equal([0f, 0f, 0f, 0f], Weights(data, LeftBrowShapes));
        Assert.Equal([1f, 0.75f, 0.0625f, 0f], Weights(data, RightBrowShapes));

        // Once: afterwards the left brows are left alone (marked here to see that nothing writes them).
        foreach (var s in LeftBrowShapes)
            data.Shapes[(int)s].Weight = 0.9f;
        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.RightBrow)));
        writer.Write(data, EyeMapper.Map(FeoPacketTests.ExamplePacket));
        Assert.All(Weights(data, LeftBrowShapes), w => Assert.Equal(0.9f, w));

        // The right brow lost its brows with the 76-byte packet: zeroed then.
        Assert.Equal([0f, 0f, 0f, 0f], Weights(data, RightBrowShapes));

        // When the flag comes back, the brows follow again.
        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.LeftBrow)));
        Assert.Equal([0.5f, 0.25f, 0f, 0.125f], Weights(data, LeftBrowShapes));
    }

    [Fact]
    public void Neutral_zeroes_brows_written_before_once()
    {
        var data = InvalidData();
        var writer = new VrcftEyeWriter();
        writer.Write(data, EyeMapper.Map(BrowPacket(FeoFlags.LeftBrow)));

        // A packet without TRACKING (or the timeout) gives the neutral frame.
        writer.Write(data, EyeMapper.Map(FeoPacketTests.BrowPacket with { Flags = FeoFlags.LeftBrow | FeoFlags.RightBrow }));
        Assert.Equal([0f, 0f, 0f, 0f], Weights(data, LeftBrowShapes));
        Assert.All(Weights(data, RightBrowShapes), w => Assert.Equal(Invalid, w));

        foreach (var s in LeftBrowShapes)
            data.Shapes[(int)s].Weight = 0.9f;
        writer.Write(data, EyeFrame.Neutral);
        Assert.All(Weights(data, LeftBrowShapes), w => Assert.Equal(0.9f, w));
        Assert.All(Weights(data, RightBrowShapes), w => Assert.Equal(Invalid, w));
    }

    [Fact]
    public void Neutral_at_init_leaves_the_pupils_invalid()
    {
        var data = InvalidData();
        new VrcftEyeWriter().Write(data, EyeFrame.Neutral);

        Assert.Equal(1f, data.Eye.Left.Openness);
        Assert.Equal(Invalid, data.Eye.Left.PupilDiameter_MM);
        Assert.Equal(Invalid, data.Eye._minDilation);
        Assert.Equal(Invalid, data.Eye._maxDilation);
    }
}
