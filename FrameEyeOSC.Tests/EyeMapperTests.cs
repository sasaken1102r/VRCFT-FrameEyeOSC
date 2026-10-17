namespace FrameEyeOSC.Tests;

public class EyeMapperTests
{
    private const float Tol = 1e-6f;

    private static FeoPacket Packet(FeoFlags flags, FeoEye left, FeoEye right) => new(1, flags, 0, left, right);

    private static FeoEye Eye(float x = 0, float y = 0, float lid = 0.75f, float squint = 0, float mm = 0, float dil = 0) =>
        new(x, y, lid, squint, mm, dil);

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.375f, 0.5f, 0f)]
    [InlineData(0.75f, 1f, 0f)]
    [InlineData(0.875f, 1f, 0.5f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(-0.5f, 0f, 0f)]
    [InlineData(1.5f, 1f, 1f)]
    public void Eyelid_splits_into_openness_and_wide(float eyelid, float openness, float wide)
    {
        var m = EyeMapper.MapEye(Eye(lid: eyelid), cameraLid: false);
        Assert.Equal(openness, m.Openness, Tol);
        Assert.Equal(wide, m.Wide, Tol);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(0.375f)]
    [InlineData(0.75f)]
    [InlineData(0.8f)]
    [InlineData(0.875f)]
    [InlineData(1f)]
    public void VRCFT_v2_EyeLid_gives_the_eyelid_back(float eyelid)
    {
        var m = EyeMapper.MapEye(Eye(lid: eyelid), cameraLid: false);
        // VRCFT's UnifiedExpressionsParameters: v2/EyeLidLeft = Openness * .75 + EyeWide * .25
        Assert.Equal(eyelid, m.Openness * .75f + m.Wide * .25f, 1e-6f);
    }

    [Fact]
    public void Squint_only_with_camera_lid()
    {
        var packet = Packet(FeoFlags.Tracking | FeoFlags.RightCameraLid, Eye(squint: 0.6f), Eye(squint: 0.4f));
        var frame = EyeMapper.Map(packet);
        Assert.Equal(0f, frame.Left.Squint);
        Assert.Equal(0.4f, frame.Right.Squint);

        Assert.Equal(1f, EyeMapper.MapEye(Eye(squint: 1.7f), cameraLid: true).Squint);
        Assert.Equal(0f, EyeMapper.MapEye(Eye(squint: -0.3f), cameraLid: true).Squint);
    }

    [Theory]
    [InlineData(0.3f, -0.4f, 0.3f, -0.4f)]
    [InlineData(1.8f, -2.5f, 1f, -1f)]
    [InlineData(-1.01f, 1.01f, -1f, 1f)]
    public void Gaze_is_passed_through_and_clamped(float x, float y, float ex, float ey)
    {
        var m = EyeMapper.MapEye(Eye(x, y), cameraLid: false);
        Assert.Equal(ex, m.GazeX);
        Assert.Equal(ey, m.GazeY);
    }

    [Fact]
    public void Example_packet_maps_per_spec()
    {
        var frame = EyeMapper.Map(FeoPacketTests.ExamplePacket);

        Assert.Equal(new MappedEye(0.25f, -0.5f, 1f, 0.5f, 0.5f), frame.Left);
        Assert.Equal(new MappedEye(0.125f, -0.5f, 0.5f, 0f, 0f), frame.Right);

        var pupils = Assert.NotNull(frame.Pupils);
        Assert.Equal(4.0f, pupils.LeftMm);
        Assert.Equal(3.5f, pupils.RightMm);
        // A = 3.75, D = 0.5 -> min = 3.75 - 0.5 * 2.655
        Assert.Equal(3.75f - 0.5f * EyeMapper.DilationRangeMm, pupils.MinDilation, Tol);
        Assert.Equal(pupils.MinDilation + EyeMapper.DilationRangeMm, pupils.MaxDilation, Tol);
    }

    [Fact]
    public void Pupils_from_both_eyes()
    {
        var p = EyeMapper.MapPupils(Packet(FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil,
            Eye(mm: 3f, dil: 0.2f), Eye(mm: 5f, dil: 0.6f)));
        var pupils = Assert.NotNull(p);
        Assert.Equal(3f, pupils.LeftMm);
        Assert.Equal(5f, pupils.RightMm);
        Assert.Equal(0.4f, Normalized(pupils), 1e-5f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_eye_with_pupil_gives_both_eyes_its_values(bool leftHasPupil)
    {
        var withPupil = Eye(mm: 4.2f, dil: 0.7f);
        var without = Eye(mm: 0f, dil: 0f);
        var packet = leftHasPupil
            ? Packet(FeoFlags.Tracking | FeoFlags.LeftPupil, withPupil, without)
            : Packet(FeoFlags.Tracking | FeoFlags.RightPupil, without, withPupil);

        var pupils = Assert.NotNull(EyeMapper.MapPupils(packet));
        Assert.Equal(4.2f, pupils.LeftMm);
        Assert.Equal(4.2f, pupils.RightMm);
        Assert.Equal(0.7f, Normalized(pupils), 1e-5f);
    }

    [Fact]
    public void No_pupil_flag_leaves_the_pupils()
    {
        var frame = EyeMapper.Map(Packet(FeoFlags.Tracking | FeoFlags.LeftCameraLid, Eye(mm: 4f, dil: 0.5f), Eye(mm: 4f, dil: 0.5f)));
        Assert.Null(frame.Pupils);
    }

    [Theory]
    [InlineData(2f, 2f, 0f, 0f)]
    [InlineData(8f, 8f, 1f, 1f)]
    [InlineData(3f, 4f, 0f, 1f)]
    [InlineData(4f, 6f, 0.25f, 0.75f)]
    [InlineData(0f, 0f, 0.5f, 0.5f)]
    [InlineData(5f, 5f, -1f, 3f)] // out-of-range dilations clamp to 0 and 1
    public void VRCFT_normalization_gives_the_mean_dilation(float leftMm, float rightMm, float leftD, float rightD)
    {
        var pupils = Assert.NotNull(EyeMapper.MapPupils(Packet(FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil,
            Eye(mm: leftMm, dil: leftD), Eye(mm: rightMm, dil: rightD))));

        var expected = (Math.Clamp(leftD, 0f, 1f) + Math.Clamp(rightD, 0f, 1f)) / 2f;
        Assert.Equal(expected, Normalized(pupils), 1e-5f);
        Assert.True(pupils.MaxDilation >= pupils.MinDilation);
        Assert.Equal(EyeMapper.DilationRangeMm, pupils.MaxDilation - pupils.MinDilation, 1e-5f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-7f)]
    [InlineData(1e6f)]
    [InlineData(3e38f)]
    [InlineData(-5f)]
    public void Max_is_never_below_min(float mm)
    {
        foreach (var d in new[] { 0f, 0.5f, 1f })
        {
            var pupils = Assert.NotNull(EyeMapper.MapPupils(Packet(FeoFlags.Tracking | FeoFlags.LeftPupil | FeoFlags.RightPupil,
                Eye(mm: mm, dil: d), Eye(mm: mm, dil: d))));
            Assert.True(float.IsFinite(pupils.MinDilation) && float.IsFinite(pupils.MaxDilation));
            Assert.True(pupils.MaxDilation >= pupils.MinDilation);
        }
    }

    [Fact]
    public void Not_tracking_is_neutral_and_leaves_the_pupils()
    {
        var packet = Packet(FeoFlags.LeftCameraLid | FeoFlags.LeftPupil | FeoFlags.RightPupil,
            Eye(0.5f, 0.5f, 0.2f, 0.9f, 4f, 0.5f), Eye(-0.5f, -0.5f, 1f, 0.9f, 4f, 0.5f));
        var frame = EyeMapper.Map(packet);
        Assert.Equal(EyeFrame.Neutral, frame);
        Assert.Equal(new MappedEye(0f, 0f, 1f, 0f, 0f), frame.Left);
        Assert.Null(frame.Pupils);
    }

    [Fact]
    public void Brows_map_per_eye_with_the_flag()
    {
        var frame = EyeMapper.Map(FeoPacketTests.BrowPacket);
        Assert.Equal(new MappedBrow(0.5f, 0.25f, 0f, 0.125f), frame.LeftBrow);
        Assert.Equal(new MappedBrow(1f, 0.75f, 0.0625f, 0f), frame.RightBrow);

        var leftOnly = FeoPacketTests.BrowPacket with { Flags = FeoFlags.Tracking | FeoFlags.LeftBrow };
        frame = EyeMapper.Map(leftOnly);
        Assert.NotNull(frame.LeftBrow);
        Assert.Null(frame.RightBrow);

        var rightOnly = FeoPacketTests.BrowPacket with { Flags = FeoFlags.Tracking | FeoFlags.RightBrow };
        frame = EyeMapper.Map(rightOnly);
        Assert.Null(frame.LeftBrow);
        Assert.NotNull(frame.RightBrow);
    }

    [Fact]
    public void No_brows_block_or_no_flag_maps_to_no_brows()
    {
        var frame = EyeMapper.Map(FeoPacketTests.ExamplePacket);
        Assert.Null(frame.LeftBrow);
        Assert.Null(frame.RightBrow);

        // Flags without the block (a short packet) don't count.
        frame = EyeMapper.Map(FeoPacketTests.ExamplePacket with { Flags = FeoFlags.Tracking | FeoFlags.LeftBrow | FeoFlags.RightBrow });
        Assert.Null(frame.LeftBrow);
        Assert.Null(frame.RightBrow);
    }

    [Fact]
    public void Brows_are_clamped_to_0_1()
    {
        var packet = FeoPacketTests.BrowPacket with
        {
            Brows = new FeoBrows(new FeoBrow(1.5f, -0.5f, 2f, -3f), new FeoBrow(-1e30f, 1e30f, 0.3f, 1f)),
        };
        var frame = EyeMapper.Map(packet);
        Assert.Equal(new MappedBrow(1f, 0f, 1f, 0f), frame.LeftBrow);
        Assert.Equal(new MappedBrow(0f, 1f, 0.3f, 1f), frame.RightBrow);
    }

    [Fact]
    public void Not_tracking_has_no_brows()
    {
        var frame = EyeMapper.Map(FeoPacketTests.BrowPacket with { Flags = FeoFlags.LeftBrow | FeoFlags.RightBrow });
        Assert.Equal(EyeFrame.Neutral, frame);
        Assert.Null(frame.LeftBrow);
        Assert.Null(frame.RightBrow);
    }

    // VRCFT's UnifiedEyeData.Combined(): (A - min) / (max - min) with A the mean diameter.
    private static float Normalized(MappedPupils p) =>
        ((p.LeftMm + p.RightMm) / 2f - p.MinDilation) / (p.MaxDilation - p.MinDilation);
}
