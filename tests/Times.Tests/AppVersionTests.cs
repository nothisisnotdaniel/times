namespace Times.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" 1.2.3 +abc", "1.2.3")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("+abc", "unknown")]
    public void Format_StripsBuildMetadataAndFallsBackToUnknown(string? informationalVersion, string expected)
    {
        Assert.Equal(expected, AppVersion.Format(informationalVersion));
    }

    [Fact]
    public void Current_IsTheProjectVersionWithoutBuildMetadata()
    {
        Assert.Equal("0.1.0", AppVersion.Current);
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    // 302 (AC-7, AC-8): the build date licences are checked against.
    [Theory]
    [InlineData("2026-10-27", true)]
    [InlineData("2026-02-30", false)]
    [InlineData("27.10.2026", false)]
    [InlineData("2026-10-27 ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseBuildDate_TakesOnlyARealYyyyMmDdDate(string? value, bool valid)
    {
        DateOnly? date = AppVersion.ParseBuildDate(value);

        Assert.Equal(valid, date is not null);
        if (valid)
        {
            Assert.Equal(new DateOnly(2026, 10, 27), date);
        }
    }

    // 302: every build carries its date, so a covering licence unlocks Pro.
    [Fact]
    public void BuildDate_IsSetOnThisBuild()
    {
        Assert.NotNull(AppVersion.BuildDate);
    }
}
