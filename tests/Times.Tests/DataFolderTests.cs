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

    // 050 AC-1
    [Theory]
    [InlineData("/a\tb\n", "/ab")]
    [InlineData("\u001b[31mx", "[31mx")]
    [InlineData("\t/a", "/a")]
    [InlineData("/a\nb", "/ab")]
    [InlineData("/a\r", "/a")]
    [InlineData("\0/a\0b\0", "/ab")]
    [InlineData("\u001b/a\u001b", "/a")]
    [InlineData("\u0007/a\u0007b", "/ab")]
    [InlineData("\u007f/a\u007fb\u007f", "/ab")]
    [InlineData("\u0085/a\u0085b\u0085", "/ab")]
    [InlineData("\u009b/a\u009b2Jb\u009b", "/a2Jb")]
    [InlineData("\u2028/a\u2028b\u2028", "/ab")]
    [InlineData("\u2029/a\u2029b\u2029", "/ab")]
    [InlineData("\t\n\r\0\u001b\u0007\u007f\u0085\u009b\u2028\u2029", "")]
    public void ForDisplay_RemovesControlCharactersAndSeparators(string value, string expected)
    {
        Assert.Equal(expected, DataFolder.ForDisplay(value));
    }

    // 050 AC-2
    [Theory]
    [InlineData("   ")]
    [InlineData(" a b ")]
    [InlineData("[red]x[/]")]
    [InlineData("~/times")]
    [InlineData("C:dir")]
    [InlineData("dätä")]
    [InlineData("")]
    public void ForDisplay_KeepsEverythingElse(string value)
    {
        Assert.Equal(value, DataFolder.ForDisplay(value));
    }
}
