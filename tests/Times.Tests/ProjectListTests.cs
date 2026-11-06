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
    public void HasInvisibleOrBrokenCharacters_True(string escaped)
    {
        Assert.True(ProjectList.HasInvisibleOrBrokenCharacters(Unescape(escaped)));
    }

    // AC-1 (056)
    [Theory]
    [InlineData("Website relaunch")]
    [InlineData("Caf\\u00E9 \\u00DCn\\u00EFcode")]
    [InlineData("\\u65E5\\u672C\\u8A9E")]
    [InlineData("Launch \\uD83D\\uDE80")]
    [InlineData("")]
    public void HasInvisibleOrBrokenCharacters_False(string escaped)
    {
        Assert.False(ProjectList.HasInvisibleOrBrokenCharacters(Unescape(escaped)));
    }

    // AC-1 (056): the U+E0041 tag character is checked as one code point, not as two surrogates.
    [Fact]
    public void HasInvisibleOrBrokenCharacters_TagCharacter_IsCheckedPerCodePoint()
    {
        string tag = char.ConvertFromUtf32(0xE0041);

        Assert.Equal("\uDB40\uDC41", tag);
        Assert.True(ProjectList.HasInvisibleOrBrokenCharacters("a" + tag + "b"));
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

    /// <summary>
    /// Project files with an escaped lone surrogate (<c>\ud800</c> or <c>\udc00</c>) in a root or a
    /// project property name.
    /// </summary>
    public static TheoryData<string> LoneSurrogateProjectFiles()
    {
        string[] templates =
        [
            "{{\"{0}\":1,\"projects\":[]}}",
            "{{\"projects\":[],\"a{0}b\":1}}",
            "{{\"projects\":[{{\"{0}\":1,\"workItem\":1,\"title\":\"A\"}}]}}",
            "{{\"projects\":[{{\"workItem\":1,\"title\":\"A\",\"x{0}\":1}}]}}",
        ];
        TheoryData<string> data = new TheoryData<string>();
        foreach (string template in templates)
        {
            foreach (string surrogate in new[] { "\\ud800", "\\udc00" })
            {
                data.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, surrogate));
            }
        }

        return data;
    }

    // AC-2 (086)
    [Theory]
    [MemberData(nameof(LoneSurrogateProjectFiles))]
    public void FromJson_LoneSurrogateInPropertyName_ThrowsJsonException(string json)
    {
        Assert.Contains("\\u", json);
        Assert.Throws<JsonException>(() => ProjectList.FromJson(json));
    }

    // Wider invisible-character check (job 095) -----------------------------------------------

    // AC-1 (095)
    [Theory]
    [InlineData("abc\\u115Fdef")]
    [InlineData("abc\\u1160def")]
    [InlineData("abc\\u3164def")]
    [InlineData("abc\\uFFA0def")]
    [InlineData("abc\\u2800def")]
    [InlineData("abc\\u034Fdef")]
    [InlineData("abc\\u17B4def")]
    [InlineData("abc\\u17B5def")]
    [InlineData("abc\\u180Bdef")]
    [InlineData("abc\\u180Cdef")]
    [InlineData("abc\\u180Ddef")]
    [InlineData("abc\\u180Fdef")]
    [InlineData("abc\\uFE00def")]
    [InlineData("abc\\uFE0Ddef")]
    [InlineData("abc\\uDB40\\uDD00def")]
    [InlineData("abc\\uDB40\\uDDEFdef")]
    [InlineData("abc\\u2065def")]
    [InlineData("abc\\uFFF0def")]
    [InlineData("abc\\uFFF8def")]
    [InlineData("abc\\uDB40\\uDC80def")]
    [InlineData("abc\\uDB43\\uDFFFdef")]
    [InlineData("\\u3164")]
    [InlineData("\\u2800")]
    [InlineData("\\u3164\\u3164\\u3164")]
    [InlineData("\\u2800\\u2800")]
    public void IsValidTitle_InvisibleCodePoint_IsFalse_AndAddThrows(string escaped)
    {
        string title = Unescape(escaped);

        Assert.True(ProjectList.HasInvisibleOrBrokenCharacters(title));
        Assert.False(ProjectList.IsValidTitle(title));
        Assert.Throws<ArgumentException>(() => ProjectList.Empty.Add(new Project(1, title)));
    }

    // AC-1 (095): U+E0100 is checked as one code point, not as two surrogates.
    [Fact]
    public void IsValidTitle_SupplementarySelector_IsCheckedPerCodePoint()
    {
        string selector = char.ConvertFromUtf32(0xE0100);

        Assert.Equal("\uDB40\uDD00", selector);
        Assert.False(ProjectList.IsValidTitle("abc" + selector + "def"));
    }

    // AC-2 (095)
    [Theory]
    [InlineData("\\uFE0F")]
    [InlineData("\\uFE0E")]
    [InlineData("a\\uFE0F\\uFE0F")]
    [InlineData("a\\uFE0E\\uFE0F")]
    [InlineData("a\\uFE0F\\uFE0E")]
    [InlineData("\\u2764\\uFE0F\\uFE0F")]
    [InlineData("a\\u200B\\uFE0F")]
    [InlineData("a\\u3164\\uFE0F")]
    [InlineData("\\u2764\\uFE00")]
    [InlineData("\\u2764\\uDB40\\uDD00")]
    public void IsValidTitle_SelectorOutsideEmojiException_IsFalse(string escaped)
    {
        string title = Unescape(escaped);

        Assert.False(ProjectList.IsValidTitle(title));
        Assert.Throws<ArgumentException>(() => ProjectList.Empty.Add(new Project(1, title)));
    }

    // AC-2 (095): "variation-selector smuggling" with supplementary selectors is refused.
    [Fact]
    public void IsValidTitle_TwentySupplementarySelectors_IsFalse()
    {
        string title = "a";
        for (int i = 0; i < 20; i++)
        {
            title += char.ConvertFromUtf32(0xE0100 + (i * 11));
        }

        Assert.Equal(41, title.Length);
        Assert.False(ProjectList.IsValidTitle(title));
    }

    // AC-3 (095)
    [Theory]
    [InlineData("Love \\u2764\\uFE0F")]
    [InlineData("Text \\u2764\\uFE0E")]
    [InlineData("Launch \\uD83D\\uDE80")]
    [InlineData("Caf\\u00E9 \\u00DCn\\u00EFcode")]
    [InlineData("\\u65E5\\u672C\\u8A9E")]
    [InlineData("\\uD55C\\uAD6D\\uC5B4")]
    [InlineData("Website relaunch")]
    [InlineData("\\u2764\\uFE0F \\u2764\\uFE0E")]
    public void IsValidTitle_VisibleTextAndEmoji_IsTrue_AndAddKeepsIt(string escaped)
    {
        string title = Unescape(escaped);

        Assert.False(ProjectList.HasInvisibleOrBrokenCharacters(title));
        Assert.True(ProjectList.IsValidTitle(title));
        Assert.Equal(title, ProjectList.Empty.Add(new Project(1, title)).Projects[0].Title);
    }

    // AC-6 (095)
    [Theory]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u3164def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u3164def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u2800def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\ufe00def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\udb40\\udd00def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"\\ufe0f\"}]}")]
    public void FromJson_InvisibleTitle_ThrowsJsonException(string json)
    {
        JsonException ex = Assert.Throws<JsonException>(() => ProjectList.FromJson(json));
        Assert.Contains("control, formatting or broken characters", ex.Message);
    }

    // AC-6 (095): an emoji with its presentation selector still loads.
    [Fact]
    public void FromJson_EmojiWithSelector_Loads()
    {
        ProjectList list = ProjectList.FromJson("{\"projects\":[{\"workItem\":1,\"title\":\"Love \\u2764\\ufe0f\"}]}");

        Assert.Equal("Love \u2764\uFE0F", list.Projects[0].Title);
    }

    // Manage the quicklist (job 102) ------------------------------------------------------------

    private static ProjectList Three() =>
        new ProjectList([new Project(11, "A"), new Project(22, "B"), new Project(33, "C")]);

    // AC-11 (102)
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rename_ReplacesTitle_KeepsWorkItemAndPosition_AndLeavesOriginal(int number)
    {
        ProjectList original = Three();

        ProjectList renamed = original.Rename(number, "New title");

        Assert.Equal(Three(), original);
        Assert.Equal(3, renamed.Projects.Count);
        Assert.Equal(new Project(original.Projects[number - 1].WorkItem, "New title"), renamed.Projects[number - 1]);
        for (int i = 0; i < 3; i++)
        {
            if (i != number - 1)
            {
                Assert.Equal(original.Projects[i], renamed.Projects[i]);
            }
        }
    }

    // AC-11 (102)
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u200bb")]
    [InlineData("a\u0007b")]
    public void Rename_InvalidTitle_Throws(string title)
    {
        Assert.Throws<ArgumentException>(() => Three().Rename(1, title));
    }

    // AC-11 (102)
    [Theory]
    [InlineData(1, new[] { 22, 33 })]
    [InlineData(2, new[] { 11, 33 })]
    [InlineData(3, new[] { 11, 22 })]
    public void Remove_DropsProject_LaterOnesMoveUp_AndLeavesOriginal(int number, int[] expected)
    {
        ProjectList original = Three();

        ProjectList removed = original.Remove(number);

        Assert.Equal(Three(), original);
        Assert.Equal(expected, removed.Projects.Select(p => p.WorkItem));
        Assert.True(new ProjectList([new Project(1, "X")]).Remove(1).IsEmpty);
    }

    // AC-11 (102)
    [Theory]
    [InlineData(3, 1, new[] { 33, 11, 22 })]
    [InlineData(1, 3, new[] { 22, 33, 11 })]
    [InlineData(1, 2, new[] { 22, 11, 33 })]
    [InlineData(2, 1, new[] { 22, 11, 33 })]
    [InlineData(2, 3, new[] { 11, 33, 22 })]
    [InlineData(2, 2, new[] { 11, 22, 33 })]
    public void Move_PutsProjectAtNewNumber_ShiftsOthers_AndLeavesOriginal(int from, int to, int[] expected)
    {
        ProjectList original = Three();

        ProjectList moved = original.Move(from, to);

        Assert.Equal(Three(), original);
        Assert.Equal(expected, moved.Projects.Select(p => p.WorkItem));
        Assert.Equal(["A", "B", "C"], moved.Projects.OrderBy(p => p.WorkItem).Select(p => p.Title));
    }

    // AC-11 (102)
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void OutOfRangeNumbers_Throw(int number)
    {
        ProjectList list = Three();

        Assert.Throws<ArgumentOutOfRangeException>(() => list.Rename(number, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Remove(number));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Move(number, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Move(1, number));
        Assert.Equal(Three(), list);
    }

    // AC-11 (102)
    [Fact]
    public void EmptyList_AnyNumber_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectList.Empty.Rename(1, "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectList.Empty.Remove(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectList.Empty.Move(1, 1));
        Assert.True(ProjectList.Empty.IsEmpty);
    }
}
