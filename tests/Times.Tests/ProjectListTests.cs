using System.Text.Json;

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
}
