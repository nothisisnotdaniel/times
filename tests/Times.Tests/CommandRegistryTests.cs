namespace Times.Tests;

public class CommandRegistryTests
{
    [Fact]
    public void Default_RegistersHelpAndQuit()
    {
        var registry = CommandRegistry.CreateDefault();

        Assert.Equal(["/help", "/quit"], registry.Commands.Select(c => c.Name));
    }

    // AC-8
    [Theory]
    [InlineData("/quit")]
    [InlineData("/QUIT")]
    [InlineData("/Quit")]
    public void TryGet_IsCaseInsensitive(string name)
    {
        var registry = CommandRegistry.CreateDefault();

        Assert.True(registry.TryGet(name, out var command));
        Assert.Equal("/quit", command.Name);
    }

    // Job 004
    [Fact]
    public void Error_SetsHint_NotMessage()
    {
        var result = CommandResult.Error("oops");

        Assert.NotNull(result.Hint);
        Assert.Null(result.Message);
        Assert.False(result.Quit);
    }

    // Job 004
    [Fact]
    public void Help_SetsMessage_NotHint()
    {
        CommandRegistry.CreateDefault().TryGet("/help", out var help);

        var result = help.Handler("");

        Assert.NotNull(result.Message);
        Assert.Null(result.Hint);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsFalse()
    {
        Assert.False(CommandRegistry.CreateDefault().TryGet("/nope", out _));
    }

    [Fact]
    public void Quit_RequestsQuit()
    {
        CommandRegistry.CreateDefault().TryGet("/quit", out var quit);

        Assert.True(quit.Handler("").Quit);
    }

    [Fact]
    public void Handler_ReceivesArgument()
    {
        var registry = new CommandRegistry();
        string? received = null;
        registry.Register("/x", "x", a => { received = a; return CommandResult.None; });

        registry.TryGet("/x", out var x);
        x.Handler("some args");

        Assert.Equal("some args", received);
    }

    [Theory]
    [InlineData("noslash")]
    [InlineData("/")]
    [InlineData("/two words")]
    public void Register_RejectsBadNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new CommandRegistry().Register(name, "d", _ => CommandResult.None));
    }

    [Fact]
    public void Register_RejectsDuplicates_CaseInsensitively()
    {
        var registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("/HELP", "d", _ => CommandResult.None));
    }

    // AC-11
    [Theory]
    [InlineData("/q", "/quit")]
    [InlineData("/Q", "/quit")]
    [InlineData("/he", "/help")]
    [InlineData("/quit", "/quit")]
    public void Complete_UniquePrefix_ReturnsFullName(string prefix, string expected)
    {
        Assert.Equal(expected, CommandRegistry.CreateDefault().Complete(prefix));
    }

    [Theory]
    [InlineData("/x")]   // no match
    [InlineData("/")]    // matches both
    [InlineData("q")]    // not a command
    [InlineData("")]
    public void Complete_NoOrAmbiguousMatch_ReturnsNull(string prefix)
    {
        Assert.Null(CommandRegistry.CreateDefault().Complete(prefix));
    }

    [Fact]
    public void Complete_PrefixSharedByTwoCommands_ReturnsNull()
    {
        var registry = CommandRegistry.CreateDefault();
        registry.Register("/hello", "d", _ => CommandResult.None);

        Assert.Null(registry.Complete("/hel"));
    }
}
