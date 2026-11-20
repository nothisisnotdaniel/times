using Spectre.Console;

namespace Times.Tests;

public sealed class EchoTextTests
{
    // AC-1
    [Fact]
    public void Markup_EscapesMarkupCharacters()
    {
        Assert.Equal("a[[b]]c", EchoText.Markup("a[b]c"));
    }

    // AC-1
    [Fact]
    public void Markup_DropsBidiZeroWidthAndControlCharacters()
    {
        Assert.Equal("8:15", EchoText.Markup("8\u202E:1\u200B5\u001B"));
    }

    // AC-1
    [Fact]
    public void Markup_KeepsEmojiPair()
    {
        Assert.Equal("ok \U0001F600", EchoText.Markup("ok \U0001F600"));
    }

    // AC-1: U+E0041 (TAG LATIN CAPITAL LETTER A) is Cf; both surrogate halves go.
    [Fact]
    public void Markup_DropsSupplementaryFormatCodePointWithBothHalves()
    {
        string result = EchoText.Markup("a\U000E0041b");

        Assert.Equal("ab", result);
        Assert.DoesNotContain(result, char.IsSurrogate);
    }

    // AC-1
    [Theory]
    [InlineData("a[b]c")]
    [InlineData("8\u202E:1\u200B5\u001B")]
    [InlineData("\U0001F600")]
    [InlineData("a\U000E0041b")]
    [InlineData("")]
    [InlineData("\u200B")]
    [InlineData("[red]x[/]\u2028y")]
    public void Markup_EqualsEscapeOfForDisplay(string input)
    {
        Assert.Equal(Markup.Escape(DataFolder.ForDisplay(input)), EchoText.Markup(input));
    }

    // AC-8
    [Theory]
    [InlineData("a[b]c")]
    [InlineData("8\u202E:1\u200B5\u001B")]
    [InlineData("a\U000E0041b")]
    public void AppShown_EqualsEchoText(string input)
    {
        Assert.Equal(EchoText.Markup(input), App.Shown(input));
    }

    // AC-8
    [Fact]
    public void AppSource_HasNoBoldEchoThroughMarkupEscape()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Times", "App.cs"));
        Assert.Contains("EchoText.Markup(", source);
        Assert.DoesNotContain("[bold]{Markup.Escape(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[bold]\\\"{Markup.Escape(", source, StringComparison.Ordinal);
    }
}
