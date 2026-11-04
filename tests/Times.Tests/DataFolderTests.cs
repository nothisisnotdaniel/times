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

    // 085 AC-1
    [Theory]
    [InlineData("/a\u202Eb", "/ab")]
    [InlineData("\u202E/ab", "/ab")]
    [InlineData("/ab\u202E", "/ab")]
    [InlineData("\u200B/a\u200Bb\u200B", "/ab")]
    [InlineData("/a\u200Cb", "/ab")]
    [InlineData("/a\u200Db", "/ab")]
    [InlineData("/a\u200Eb", "/ab")]
    [InlineData("/a\u200Fb", "/ab")]
    [InlineData("/a\u2066\u2067\u2068\u2069b", "/ab")]
    [InlineData("/a\u202A\u202B\u202C\u202D\u202Eb", "/ab")]
    [InlineData("/a\u00ADb", "/ab")]
    [InlineData("/a\u061Cb", "/ab")]
    [InlineData("\uFEFF/ab", "/ab")]
    [InlineData("\u200B\u200C\u200D\u200E\u200F\u2066\u2067\u2068\u2069\u202A\u202B\u202C\u202D\u202E\u00AD\u061C\uFEFF", "")]
    public void ForDisplay_RemovesFormatCharacters(string value, string expected)
    {
        Assert.Equal(expected, DataFolder.ForDisplay(value));
    }

    // 085 AC-2
    [Theory]
    [InlineData("/a\U000E0041b\U000E007F", "/ab")]
    [InlineData("\U000E0001/a", "/a")]
    [InlineData("\U000E0001\U000E0020\U000E007F", "")]
    public void ForDisplay_RemovesSupplementaryFormatCharacters_AsWholePairs(string value, string expected)
    {
        string shown = DataFolder.ForDisplay(value);

        Assert.Equal(expected, shown);
        Assert.DoesNotContain(shown, char.IsSurrogate);
    }

    // 085 AC-3
    [Theory]
    [InlineData("\u001b\u202E/a\u200B\n", "/a")]
    [InlineData("\t\u200D/a\u2028\uFEFFb\U000E0041\u0085", "/ab")]
    public void ForDisplay_RemovesMixedControlAndFormatCharacters(string value, string expected)
    {
        Assert.Equal(expected, DataFolder.ForDisplay(value));
    }

    // 085 AC-4
    [Theory]
    [InlineData("/a/\U0001F600")]
    [InlineData("e\u0301")]
    [InlineData("/a\uFE0Fb")]
    public void ForDisplay_KeepsNonFormatCharacters(string value)
    {
        Assert.Equal(value, DataFolder.ForDisplay(value));
    }

    // 085 AC-4: unpaired surrogates in one fact, because xUnit gives such InlineData cases the same ID.
    [Fact]
    public void ForDisplay_KeepsUnpairedSurrogates()
    {
        foreach (string value in new[] { "/a\uD800b", "/a\uDC00b", "/a\uD800", "\uDC00/a", "/a\uDC00\uD800b" })
        {
            Assert.Equal(value, DataFolder.ForDisplay(value));
        }
    }
}
