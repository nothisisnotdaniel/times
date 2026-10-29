namespace Times.Tests;

public sealed class DataFolderTests
{
    // AC-1
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnsetOrEmptyVariable_UsesTimesUnderAppData(string? environmentValue)
    {
        DataFolder folder = DataFolder.Resolve(environmentValue, "/home/u/.config");

        Assert.Equal(Path.Combine("/home/u/.config", "Times"), folder.Path);
        Assert.False(folder.FromEnvironment);
    }

    // AC-2
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnsetOrEmptyVariable_WithEmptyAppData_GivesEmptyPath(string? environmentValue)
    {
        DataFolder folder = DataFolder.Resolve(environmentValue, "");

        Assert.Equal("", folder.Path);
        Assert.False(folder.FromEnvironment);
    }

    // AC-3
    [Theory]
    [InlineData("   ", "/home/u/.config")]
    [InlineData("relative/dir", "/home/u/.config")]
    [InlineData("~/times", "/home/u/.config")]
    [InlineData(" /abs/with spaces ", "/home/u/.config")]
    [InlineData("   ", "")]
    [InlineData("relative/dir", "")]
    [InlineData("~/times", "")]
    public void SetVariable_IsUsedExactly(string environmentValue, string appData)
    {
        DataFolder folder = DataFolder.Resolve(environmentValue, appData);

        Assert.Equal(environmentValue, folder.Path);
        Assert.True(folder.FromEnvironment);
    }

    // AC-3
    [Theory]
    [InlineData("/home/u/.config")]
    [InlineData("")]
    public void SetVariable_AbsoluteTempPath_IsUsedExactly(string appData)
    {
        using TempFolder temp = new TempFolder(create: false);

        DataFolder folder = DataFolder.Resolve(temp.Path, appData);

        Assert.Equal(temp.Path, folder.Path);
        Assert.True(folder.FromEnvironment);
    }

    [Fact]
    public void VariableName_IsTimesDataDir()
    {
        Assert.Equal("TIMES_DATA_DIR", DataFolder.VariableName);
    }
}
