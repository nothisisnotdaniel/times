namespace Times.Tests;

public sealed class ContextCliTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly TempFolder home = new();

    public void Dispose()
    {
        folder.Dispose();
        home.Dispose();
    }

    // AC-4
    [Fact]
    public void Parse_ValidList_KeepsContextsOrder()
    {
        IReadOnlyList<ContextTodo>? todos = ContextTodos.Parse(
            "{\"todos\":[{\"due\":\"2026-11-21\",\"time\":null,\"text\":\"Write tests\",\"note\":\"before noon\"}," +
            "{\"due\":\"2026-11-19\",\"time\":null,\"text\":\"Older\",\"note\":null}]}");

        Assert.NotNull(todos);
        Assert.Equal(2, todos.Count);
        Assert.Equal(new DateOnly(2026, 11, 21), todos[0].Due);
        Assert.Null(todos[0].Time);
        Assert.Equal("Write tests", todos[0].Text);
        Assert.Equal("before noon", todos[0].Note);
        Assert.Equal("Older", todos[1].Text);
        Assert.Null(todos[1].Note);
    }

    // AC-4
    [Fact]
    public void Parse_NullOrMissingDue_IsNoDate()
    {
        IReadOnlyList<ContextTodo>? todos = ContextTodos.Parse(
            "{\"todos\":[{\"due\":null,\"time\":null,\"text\":\"Someday\",\"note\":null},{\"text\":\"Bare\"}]}");

        Assert.NotNull(todos);
        Assert.All(todos, t => Assert.Null(t.Due));
        Assert.Equal("Bare", todos[1].Text);
    }

    // AC-4
    [Fact]
    public void Parse_Timed_KeepsTheTime()
    {
        IReadOnlyList<ContextTodo>? todos = ContextTodos.Parse(
            "{\"todos\":[{\"due\":\"2026-11-21\",\"time\":\"09:30\",\"text\":\"Call\",\"note\":null}]}");

        Assert.NotNull(todos);
        Assert.Equal(new TimeOnly(9, 30), todos[0].Time);
    }

    // AC-4
    [Fact]
    public void Parse_EmptyList_IsEmpty()
    {
        IReadOnlyList<ContextTodo>? todos = ContextTodos.Parse("{\"todos\":[]}");

        Assert.NotNull(todos);
        Assert.Empty(todos);
    }

    // AC-4
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"todos\":{}}")]
    [InlineData("{\"todos\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("{\"todos\":[1]}")]
    [InlineData("{\"todos\":[{\"due\":null}]}")]
    [InlineData("{\"todos\":[{\"text\":5}]}")]
    [InlineData("{\"todos\":[{\"text\":\"a\",\"due\":5}]}")]
    [InlineData("{\"todos\":[{\"text\":\"a\",\"due\":\"21.11.2026\"}]}")]
    [InlineData("{\"todos\":[{\"text\":\"a\",\"time\":\"9 Uhr\"}]}")]
    [InlineData("{\"todos\":[{\"text\":\"a\",\"note\":[]}]}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"todos\":[]")]
    public void Parse_Malformed_IsNull(string json)
    {
        Assert.Null(ContextTodos.Parse(json));
    }

    // Scope: the lookup finds the file in an absolute PATH folder, skipping relative ones, without running it.
    [Fact]
    public void Find_UsesTheFirstAbsolutePathFolderHoldingContext()
    {
        string name = OperatingSystem.IsWindows() ? "context.exe" : "context";
        string dummy = Path.Combine(folder.Path, name);
        File.WriteAllText(dummy, "not a program");
        string relative = Path.Combine("relative", "bin");
        string pathVariable = string.Join(Path.PathSeparator, relative, string.Empty, folder.Path);

        Assert.Equal(dummy, ProcessContextCli.Find(pathVariable, home.Path, OperatingSystem.IsWindows()));
    }

    // Scope: without it on PATH, ~/.dotnet/tools is tried; with neither, Context is not installed.
    [Fact]
    public void Find_FallsBackToDotnetTools_ThenNull()
    {
        Assert.Null(ProcessContextCli.Find(folder.Path, home.Path, windows: false));
        Assert.Null(ProcessContextCli.Find(null, home.Path, windows: false));

        string tools = Path.Combine(home.Path, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "context.exe"), "dummy");
        Assert.Null(ProcessContextCli.Find(folder.Path, home.Path, windows: false));
        Assert.Equal(Path.Combine(tools, "context.exe"), ProcessContextCli.Find(folder.Path, home.Path, windows: true));
    }

    // Scope: a folder named context is not the program.
    [Fact]
    public void Find_SkipsADirectoryNamedContext()
    {
        Directory.CreateDirectory(Path.Combine(folder.Path, "context"));

        Assert.Null(ProcessContextCli.Find(folder.Path, home.Path, windows: false));
    }
}
