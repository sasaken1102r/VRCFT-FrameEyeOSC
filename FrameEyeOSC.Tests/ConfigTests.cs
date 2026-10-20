namespace FrameEyeOSC.Tests;

public class ConfigTests
{
    [Theory]
    [InlineData("{\"port\": 12000}", 12000, true)]
    [InlineData("{ \"Port\": 9100, \"other\": true }", 9100, true)]
    [InlineData("{}", 11112, true)]
    [InlineData("{ // comment\n \"port\": 11113, }", 11113, true)]
    [InlineData("{\"claimWithoutPackets\": false}", 11112, false)]
    [InlineData("{\"claimWithoutPackets\": true}", 11112, true)]
    [InlineData("{\"ClaimWithoutPackets\": false, \"port\": 12001}", 12001, false)]
    public void Valid_files(string json, int port, bool claimWithoutPackets)
    {
        var config = FrameEyeOscConfig.Parse(json, out var warning);
        Assert.Null(warning);
        Assert.Equal(port, config.Port);
        Assert.Equal(claimWithoutPackets, config.ClaimWithoutPackets);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"port\": \"11112\"}")]
    [InlineData("{\"port\": 0}")]
    [InlineData("{\"port\": 70000}")]
    [InlineData("{\"port\": 11112.5}")]
    [InlineData("{\"port\": 12000, \"claimWithoutPackets\": \"yes\"}")]
    [InlineData("{\"claimWithoutPackets\": true, \"port\": 0}")]
    public void Bad_files_warn_and_use_the_defaults(string json)
    {
        var config = FrameEyeOscConfig.Parse(json, out var warning);
        Assert.NotNull(warning);
        Assert.Equal(FrameEyeOscConfig.Default, config);
    }

    [Fact]
    public void Missing_file_is_the_defaults_without_a_warning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"feo-missing-{Guid.NewGuid():N}", FrameEyeOscConfig.FileName);
        var config = FrameEyeOscConfig.Load(path, out var warning);
        Assert.Null(warning);
        Assert.Equal(11112, config.Port);
        Assert.False(File.Exists(path)); // never written
    }
}
