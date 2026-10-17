using System.Reflection;
using VRCFaceTracking.Core.OSC.DataTypes;
using VRCFaceTracking.Core.Params;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.DataTypes;
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
        Assert.Equal(0.5f, data.Eye.Combined().PupilDiameter_MM);
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

        Assert.Equal((leftD + rightD) / 2f, data.Eye.Combined().PupilDiameter_MM);
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
            Assert.Equal(d, data.Eye.Combined().PupilDiameter_MM);
        }
    }

    private static FeoPacket PupilPacket(float leftMm, float rightMm, float leftD, float rightD) =>
        new(1, FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil, 0,
            new FeoEye(0, 0, 0.75f, 0, leftMm, leftD), new FeoEye(0, 0, 0.75f, 0, rightMm, rightD));

    [Fact]
    public void Actual_SDK_pupil_normalization_preserves_quantizer_boundaries_and_their_neighbors()
    {
        var dilations = QuantizerBoundaryDilations();
        (float Left, float Right)[] diameters =
        [
            (0f, 0f), (0f, 4f), (4f, 0f), (2f, 8f), (8f, 2f),
            (3f, 5f), (3.25f, 4.75f), (4f, 4f), (2f, 7.5f), (7.5f, 2f),
            (8f, 8f), (10f, 12f), (12f, 10f), (16f, 20f), (20f, 16f),
        ];
        foreach (var (left, right) in diameters)
        {
            var data = InvalidData();
            var writer = new VrcftEyeWriter();
            foreach (var dilation in dilations)
            {
                writer.Write(data, EyeMapper.Map(PupilPacket(left, right, dilation, dilation)));
                Assert.Equal(left, data.Eye.Left.PupilDiameter_MM);
                Assert.Equal(right, data.Eye.Right.PupilDiameter_MM);
                Assert.Equal(dilation, data.Eye.Combined().PupilDiameter_MM);
            }
        }
    }

    [Fact]
    public void Actual_SDK_pupils_keep_asymmetric_diameters_and_clamped_mean_dilation()
    {
        var random = new Random(1701);
        var data = InvalidData();
        var writer = new VrcftEyeWriter();
        for (var i = 0; i < 10_000; i++)
        {
            var leftMm = 0.5f + random.NextSingle() * 9.5f;
            var rightMm = 0.5f + random.NextSingle() * 9.5f;
            var leftD = random.NextSingle() * 3f - 1f;
            var rightD = random.NextSingle() * 3f - 1f;
            var expected = (Math.Clamp(leftD, 0f, 1f) + Math.Clamp(rightD, 0f, 1f)) * 0.5f;

            writer.Write(data, EyeMapper.Map(PupilPacket(leftMm, rightMm, leftD, rightD)));
            Assert.Equal(leftMm, data.Eye.Left.PupilDiameter_MM);
            Assert.Equal(rightMm, data.Eye.Right.PupilDiameter_MM);
            Assert.Equal(expected, data.Eye.Combined().PupilDiameter_MM);
        }
    }

    // Invoke the SDK's own getters without registering global OSC update handlers.
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static Func<UnifiedTrackingData, bool> SdkBooleanGetter(Func<UnifiedTrackingData, float> value)
    {
        var scalar = new EParam("v2/PupilDilation", value);
        var children = (Parameter[])typeof(EParam).GetField("_parameter", PrivateInstance)!.GetValue(scalar)!;
        return (Func<UnifiedTrackingData, bool>)typeof(BaseParam<bool>)
            .GetField("_getValueFunc", PrivateInstance)!.GetValue(children[0])!;
    }

    [Fact]
    public void Half_dilation_does_not_flip_the_actual_SDK_boolean_parameter()
    {
        var data = InvalidData();
        new VrcftEyeWriter().Write(data, EyeMapper.Map(PupilPacket(4f, 4f, 0.5f, 0.5f)));

        var actual = SdkBooleanGetter(d => d.Eye.Combined().PupilDiameter_MM);
        var expected = SdkBooleanGetter(_ => 0.5f);
        Assert.False(expected(data));
        Assert.Equal(expected(data), actual(data));
    }

    [Fact]
    public void Actual_SDK_binary_pupil_parameters_match_the_source_dilation_for_one_to_eight_bits()
    {
        float[] dilations =
        [
            0f, float.Epsilon, 0.062499996f, 0.0625f, 0.06250001f,
            0.12499999f, 0.125f, 0.49999997f, 0.5f, 0.50000006f, 0.99999f, 1f,
        ];
        var maximum = typeof(BinaryBaseParameter).GetField("_maxPossibleBinaryInt", PrivateInstance)!;
        var process = typeof(BinaryBaseParameter).GetMethod("ProcessBinary", PrivateInstance)!;
        foreach (var dilation in dilations)
        {
            var data = InvalidData();
            new VrcftEyeWriter().Write(data, EyeMapper.Map(PupilPacket(3.25f, 4.75f, dilation, dilation)));
            var actual = new BinaryBaseParameter("v2/PupilDilation", d => d.Eye.Combined().PupilDiameter_MM);
            var expected = new BinaryBaseParameter("v2/PupilDilation", _ => dilation);
            for (var bits = 1; bits <= 8; bits++)
            {
                maximum.SetValue(actual, 1 << bits);
                maximum.SetValue(expected, 1 << bits);
                for (var bit = 0; bit < bits; bit++)
                    Assert.Equal(process.Invoke(expected, [data, bit]), process.Invoke(actual, [data, bit]));
            }
        }
    }

    private static float[] QuantizerBoundaryDilations()
    {
        var values = new HashSet<float> { 0f, 1f };
        void AddNeighbors(float value)
        {
            if (value >= 0f && value <= 1f) values.Add(value);
            for (var i = 0; i < 2; i++)
            {
                var lower = MathF.BitDecrement(value);
                if (lower >= 0f) values.Add(lower);
                value = lower;
            }
        }
        void AddBothSides(float value)
        {
            AddNeighbors(value);
            AddNeighbors(MathF.BitIncrement(MathF.BitIncrement(value)));
        }

        // Steam's1..8-bit boundaries and saturation point.
        for (var k = 1; k < 256; k++) AddBothSides(k / 256f);
        AddBothSides(0.99999f);
        AddNeighbors(1f);

        // The pinned Avalonia SDK's binary quantizer has different float
        // transition points. Find its actual transitions, then test adjacent
        // representable inputs; this only generates cases, not expected output.
        for (var bits = 1; bits <= 8; bits++)
        {
            var n = 1 << bits;
            for (var level = 1; level < n; level++)
            {
                uint lower = 0, upper = 0x3f800000;
                while (lower < upper)
                {
                    var middle = lower + (upper - lower) / 2;
                    var value = BitConverter.UInt32BitsToSingle(middle);
                    var adjusted = value + 1f / n;
                    if ((int)(adjusted * (n - 1)) >= level) upper = middle;
                    else lower = middle + 1;
                }
                AddBothSides(BitConverter.UInt32BitsToSingle(lower));
            }
        }
        return values.Order().ToArray();
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
