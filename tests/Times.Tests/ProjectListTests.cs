using System.Text.Json;
using System.Text.RegularExpressions;

namespace Times.Tests;

public class ProjectListTests
{
    // AC-6
    [Fact]
    public void ToJson_WritesProjectsInOrder_WithoutNumbers()
    {
        ProjectList list = ProjectList.Empty.Add(new Project(1234, "Website relaunch")).Add(new Project(77, "Support"));

        Assert.Equal(
            "{\"projects\":[{\"workItem\":1234,\"title\":\"Website relaunch\"},{\"workItem\":77,\"title\":\"Support\"}]}",
            list.ToJson());
    }

    // AC-6
    [Fact]
    public void Empty_WritesEmptyArray_AndReadsBack()
    {
        Assert.Equal("{\"projects\":[]}", ProjectList.Empty.ToJson());
        Assert.Equal(ProjectList.Empty, ProjectList.FromJson("{\"projects\":[]}"));
    }

    // AC-2
    [Fact]
    public void Add_AppendsAtEnd_AndReturnsNewList()
    {
        ProjectList first = ProjectList.Empty.Add(new Project(5, "B"));
        ProjectList second = first.Add(new Project(3, "A"));

        Assert.Equal([new Project(5, "B")], first.Projects);
        Assert.Equal([new Project(5, "B"), new Project(3, "A")], second.Projects);
        Assert.Equal(1, second.NumberOf(5));
        Assert.Equal(2, second.NumberOf(3));
        Assert.Null(second.NumberOf(4));
        Assert.True(ProjectList.Empty.IsEmpty);
    }

    // AC-5
    [Fact]
    public void Add_DuplicateWorkItem_Throws()
    {
        ProjectList list = ProjectList.Empty.Add(new Project(5, "B"));

        Assert.Throws<ArgumentException>(() => list.Add(new Project(5, "Other")));
    }

    // AC-5
    [Theory]
    [InlineData(0, "Title")]
    [InlineData(-1, "Title")]
    [InlineData(1, "")]
    [InlineData(1, "   ")]
    [InlineData(1, "a\nb")]
    [InlineData(1, "a\u0007b")]
    [InlineData(1, "a\u2028b")]
    public void Add_InvalidProject_Throws(int workItem, string title)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProjectList.Empty.Add(new Project(workItem, title)));
    }

    // AC-5
    [Fact]
    public void IsValidTitle_ChecksLength()
    {
        Assert.True(ProjectList.IsValidTitle(new string('x', 200)));
        Assert.False(ProjectList.IsValidTitle(new string('x', 201)));
    }

    // AC-4, AC-6
    [Fact]
    public void RoundTrip_KeepsOrderAndTitlesUnchanged()
    {
        ProjectList list = ProjectList.Empty
            .Add(new Project(int.MaxValue, "[red]Fix[/] \"quoted\" \\ back"))
            .Add(new Project(1, "Ünïcödé  two  spaces <tag> &amp;"))
            .Add(new Project(42, new string('x', 200)));

        Assert.Equal(list, ProjectList.FromJson(list.ToJson()));
        Assert.Equal(ProjectList.Empty, ProjectList.FromJson(ProjectList.Empty.ToJson()));
    }

    // AC-6
    [Fact]
    public void FromJson_IgnoresUnknownProperties()
    {
        ProjectList list = ProjectList.FromJson("{\"version\":1,\"projects\":[{\"workItem\":7,\"title\":\"A\",\"extra\":true}]}");

        Assert.Equal([new Project(7, "A")], list.Projects);
    }

    // AC-6
    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"projects\"")]
    [InlineData("{}")]
    [InlineData("{\"projects\":null}")]
    [InlineData("{\"projects\":{}}")]
    [InlineData("{\"projects\":\"x\"}")]
    [InlineData("{\"projects\":[],\"projects\":[]}")]
    [InlineData("{\"projects\":[1]}")]
    [InlineData("{\"projects\":[null]}")]
    [InlineData("{\"projects\":[{\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1}]}")]
    [InlineData("{\"projects\":[{\"workItem\":0,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":-5,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":12.5,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1e3,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":2147483648,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":\"1\",\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":null,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"  \"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":5}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":null}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"a\\nb\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"a\\u001bb\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"workItem\":2,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"A\",\"title\":\"B\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"A\"},{\"workItem\":1,\"title\":\"B\"}]}")]
    [InlineData("{\"projects\":[]} []")]
    public void FromJson_Invalid_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ProjectList.FromJson(json));
    }

    // AC-6
    [Fact]
    public void FromJson_TitleLongerThanMax_Throws()
    {
        string json = "{\"projects\":[{\"workItem\":1,\"title\":\"" + new string('x', 201) + "\"}]}";

        Assert.ThrowsAny<JsonException>(() => ProjectList.FromJson(json));
    }

    // Safer titles and names (job 056) ---------------------------------------------------------

    // Cases are written with \u escapes and unescaped in the test, so lone surrogates never go
    // through xUnit's test case serialization.
    private static string Unescape(string escaped) => Regex.Unescape(escaped);

    // AC-1 (056)
    [Theory]
    [InlineData("\\u202E")]
    [InlineData("\\u200B")]
    [InlineData("\\u200C")]
    [InlineData("\\u200D")]
    [InlineData("\\u2060")]
    [InlineData("\\uFEFF")]
    [InlineData("\\u00AD")]
    [InlineData("\\u2066")]
    [InlineData("\\uDB40\\uDC41")]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uDC00\\uD800")]
    [InlineData("ok\\uD800")]
    [InlineData("\\uD800ok")]
    public void HasFormatOrBrokenCharacters_True(string escaped)
    {
        Assert.True(ProjectList.HasFormatOrBrokenCharacters(Unescape(escaped)));
    }

    // AC-1 (056)
    [Theory]
    [InlineData("Website relaunch")]
    [InlineData("Caf\\u00E9 \\u00DCn\\u00EFcode")]
    [InlineData("\\u65E5\\u672C\\u8A9E")]
    [InlineData("Launch \\uD83D\\uDE80")]
    [InlineData("")]
    public void HasFormatOrBrokenCharacters_False(string escaped)
    {
        Assert.False(ProjectList.HasFormatOrBrokenCharacters(Unescape(escaped)));
    }

    // AC-1 (056): the U+E0041 tag character is checked as one code point, not as two surrogates.
    [Fact]
    public void HasFormatOrBrokenCharacters_TagCharacter_IsCheckedPerCodePoint()
    {
        string tag = char.ConvertFromUtf32(0xE0041);

        Assert.Equal("\uDB40\uDC41", tag);
        Assert.True(ProjectList.HasFormatOrBrokenCharacters("a" + tag + "b"));
    }

    // AC-2 (056)
    [Theory]
    [InlineData("abc\\u202Edef")]
    [InlineData("abc\\u200Bdef")]
    [InlineData("abc\\u200Cdef")]
    [InlineData("abc\\u200Ddef")]
    [InlineData("abc\\u2060def")]
    [InlineData("abc\\uFEFFdef")]
    [InlineData("abc\\u00ADdef")]
    [InlineData("abc\\u2066def")]
    [InlineData("abc\\uDB40\\uDC41def")]
    [InlineData("abc\\uD800def")]
    [InlineData("abc\\uDC00def")]
    [InlineData("abc\\uDC00\\uD800def")]
    [InlineData("\\u200B")]
    public void IsValidTitle_FormatOrBroken_IsFalse_AndAddThrows(string escaped)
    {
        string title = Unescape(escaped);

        Assert.False(ProjectList.IsValidTitle(title));
        Assert.Throws<ArgumentException>(() => ProjectList.Empty.Add(new Project(1, title)));
    }

    // AC-2 (056)
    [Theory]
    [InlineData("Website relaunch")]
    [InlineData("Caf\\u00E9 \\u00DCn\\u00EFcode")]
    [InlineData("\\u65E5\\u672C\\u8A9E")]
    [InlineData("Launch \\uD83D\\uDE80")]
    public void IsValidTitle_OrdinaryUnicode_IsTrue_AndAddKeepsIt(string escaped)
    {
        string title = Unescape(escaped);

        Assert.True(ProjectList.IsValidTitle(title));
        Assert.Equal(title, ProjectList.Empty.Add(new Project(1, title)).Projects[0].Title);
    }

    // AC-6 (056)
    [Theory]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u202Edef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u200Bdef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u202edef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\ud800def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\udc00def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"\\ud800\"}]}")]
    public void FromJson_FormatOrBrokenTitle_ThrowsJsonException(string json)
    {
        JsonException ex = Assert.Throws<JsonException>(() => ProjectList.FromJson(json));
        Assert.Contains("control, formatting or broken characters", ex.Message);
    }

    // AC-6 (056): a valid escaped pair still loads.
    [Fact]
    public void FromJson_EscapedEmojiPair_Loads()
    {
        ProjectList list = ProjectList.FromJson("{\"projects\":[{\"workItem\":1,\"title\":\"Launch \\ud83d\\ude80\"}]}");

        Assert.Equal("Launch \uD83D\uDE80", list.Projects[0].Title);
    }
}
