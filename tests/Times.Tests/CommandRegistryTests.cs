namespace Times.Tests;

public class CommandRegistryTests
{
    [Fact]
    public void Default_RegistersHelpAndQuit()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Equal(["help", "quit"], registry.Commands.Select(c => c.Name));
    }

    // AC-8
    [Theory]
    [InlineData("quit")]
    [InlineData("QUIT")]
    [InlineData("Quit")]
    public void TryGet_IsCaseInsensitive(string name)
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.True(registry.TryGet(name, out Command? command));
        Assert.Equal("quit", command.Name);
    }

    // Job 004
    [Fact]
    public void Error_SetsHint_NotMessage()
    {
        CommandResult result = CommandResult.Error("oops");

        Assert.NotNull(result.Hint);
        Assert.Null(result.Message);
        Assert.False(result.Quit);
    }

    // Job 004
    [Fact]
    public void Help_SetsMessage_NotHint()
    {
        CommandRegistry.CreateDefault().TryGet("help", out Command? help);

        CommandResult result = help.Handler("");

        Assert.NotNull(result.Message);
        Assert.Null(result.Hint);
    }

    // Job 033 (round 2)
    [Fact]
    public void Help_HidesTheQuicklist_OtherResultsDoNot()
    {
        CommandRegistry.CreateDefault().TryGet("help", out Command? help);

        Assert.True(help!.Handler("").HideQuicklist);
        Assert.False(CommandResult.None.HideQuicklist);
        Assert.False(CommandResult.Error("x").HideQuicklist);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("/help")] // names are stored without '/'; App strips it before the lookup
    public void TryGet_UnknownName_ReturnsFalse(string name)
    {
        Assert.False(CommandRegistry.CreateDefault().TryGet(name, out _));
    }

    [Fact]
    public void Quit_RequestsQuit()
    {
        CommandRegistry.CreateDefault().TryGet("quit", out Command? quit);

        Assert.True(quit.Handler("").Quit);
    }

    [Fact]
    public void Handler_ReceivesArgument()
    {
        CommandRegistry registry = new CommandRegistry();
        string? received = null;
        registry.Register("x", "x", a => { received = a; return CommandResult.None; });

        registry.TryGet("x", out Command? x);
        x.Handler("some args");

        Assert.Equal("some args", received);
    }

    // AC-9 (040)
    [Theory]
    [InlineData("/x")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData(" x")]
    public void Register_RejectsBadNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new CommandRegistry().Register(name, "d", _ => CommandResult.None));
    }

    // AC-9 (040)
    [Fact]
    public void Register_BareName_Works()
    {
        CommandRegistry registry = new CommandRegistry();

        registry.Register("x", "d", _ => CommandResult.None);

        Assert.Equal(["x"], registry.Commands.Select(c => c.Name));
    }

    [Fact]
    public void Register_RejectsDuplicates_CaseInsensitively()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("HELP", "d", _ => CommandResult.None));
    }

    // AC-11, AC-8 (040): the result keeps the user's '/' if one was typed.
    [Theory]
    [InlineData("q", "quit")]
    [InlineData("/q", "/quit")]
    [InlineData("/Q", "/quit")]
    [InlineData("Q", "quit")]
    [InlineData("he", "help")]
    [InlineData("/he", "/help")]
    [InlineData("quit", "quit")]
    [InlineData("/quit", "/quit")]
    public void Complete_UniquePrefix_ReturnsFullName(string prefix, string expected)
    {
        Assert.Equal(expected, CommandRegistry.CreateDefault().Complete(prefix));
    }

    [Theory]
    [InlineData("x")]   // no match
    [InlineData("/x")]  // no match
    [InlineData("/")]   // matches both
    [InlineData("//q")] // only one '/' is ignored
    [InlineData("")]    // empty prefix: no completion
    public void Complete_NoOrAmbiguousMatch_ReturnsNull(string prefix)
    {
        Assert.Null(CommandRegistry.CreateDefault().Complete(prefix));
    }

    [Fact]
    public void Complete_PrefixSharedByTwoCommands_ReturnsNull()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("hello", "d", _ => CommandResult.None);

        Assert.Null(registry.Complete("hel"));
        Assert.Null(registry.Complete("/hel"));
    }

    // Job 006 ------------------------------------------------------------------------------

    private static CommandRegistry WithNavigation()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("prev", "Go to the previous day", _ => CommandResult.None);
        registry.Register("next", "Go to the next day", _ => CommandResult.None);
        registry.Register("today", "Go back to today", _ => CommandResult.None);
        return registry;
    }

    // AC-1
    [Theory]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("", "", 0)]
    [InlineData("today", "today", 0)]
    [InlineData("tdoay", "today", 2)]
    [InlineData("kitten", "sitting", 3)]
    public void Levenshtein_GivesEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, CommandRegistry.Levenshtein(a, b));
    }

    // AC-2
    [Fact]
    public void Match_SlashOnly_ReturnsAllInNameOrder()
    {
        Assert.Equal(
            ["help", "next", "prev", "quit", "today"],
            WithNavigation().Match("/").Select(c => c.Name));
    }

    // AC-2, AC-6 (040)
    [Theory]
    [InlineData("t")]
    [InlineData("/t")]
    public void Match_OneLetter_OnlyKeepsPrefixes(string line)
    {
        Assert.Equal(["today"], WithNavigation().Match(line).Select(c => c.Name));
    }

    // AC-2, AC-6 (040)
    [Theory]
    [InlineData("tdoay", "today")]
    [InlineData("/tdoay", "today")]
    [InlineData("TDOAY", "today")]
    [InlineData("/TDOAY", "today")]
    [InlineData("hepl", "help")]
    [InlineData("/hepl", "help")]
    [InlineData("next foo", "next")]
    [InlineData("/next foo", "next")]
    [InlineData("ne", "next")]
    public void Match_RanksBestFirst(string line, string expected)
    {
        Assert.Equal(expected, WithNavigation().Match(line)[0].Name);
    }

    // AC-2
    [Theory]
    [InlineData("zzzzz")]
    [InlineData("z")]
    [InlineData("/z")]
    [InlineData("zz")]
    public void Match_NoCloseCommand_IsEmpty(string line)
    {
        Assert.Empty(WithNavigation().Match(line));
    }

    // AC-2
    [Fact]
    public void Match_TiesAreSortedByName()
    {
        // "xe" is one edit from the prefixes "he" (help) and "ne" (next), two from the rest.
        IReadOnlyList<Command> matches = WithNavigation().Match("xe");

        Assert.Equal(["help", "next"], matches.Select(c => c.Name));
    }

    // Job 011 ------------------------------------------------------------------------------

    private static CommandRegistry WithPrefixes()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("prev", "d", _ => CommandResult.None);
        registry.Register("print", "d", _ => CommandResult.None);
        registry.Register("project", "d", _ => CommandResult.None);
        return registry;
    }

    // AC-1
    [Theory]
    [InlineData("p")]
    [InlineData("P")]
    [InlineData("/p")]
    [InlineData("/P")]
    public void StartingWith_ReturnsPrefixMatchesInNameOrder(string prefix)
    {
        Assert.Equal(["prev", "print", "project"], WithPrefixes().StartingWith(prefix).Select(c => c.Name));
    }

    // AC-1, AC-8 (040)
    [Theory]
    [InlineData("z")]
    [InlineData("/z")]
    [InlineData("//p")]
    [InlineData("")]
    public void StartingWith_EmptyOrNoMatch_IsEmpty(string prefix)
    {
        Assert.Empty(WithPrefixes().StartingWith(prefix));
    }

    // AC-6 (040): a lone '/' still lists everything.
    [Fact]
    public void StartingWith_LoneSlash_ListsEveryCommand()
    {
        Assert.Equal(["help", "prev", "print", "project", "quit"], WithPrefixes().StartingWith("/").Select(c => c.Name));
    }

    // AC-2, AC-8 (040)
    [Theory]
    [InlineData("p", "pr")]
    [InlineData("PR", "pr")]
    [InlineData("/p", "/pr")]
    [InlineData("/PR", "/pr")]
    [InlineData("pri", "pri")]
    [InlineData("/pri", "/pri")]
    [InlineData("/", "/")]
    [InlineData("z", "z")]
    [InlineData("/z", "/z")]
    [InlineData("", "")]
    [InlineData("pro", "pro")]
    public void CommonPrefix_ExtendsToLongestSharedPrefix(string prefix, string expected)
    {
        Assert.Equal(expected, WithPrefixes().CommonPrefix(prefix));
    }

    // AC-1
    [Fact]
    public void Complete_KeepsItsContract_WithAmbiguousPrefix()
    {
        Assert.Null(WithPrefixes().Complete("pr"));
        Assert.Null(WithPrefixes().Complete("/pr"));
        Assert.Equal("print", WithPrefixes().Complete("pri"));
        Assert.Equal("/print", WithPrefixes().Complete("/pri"));
    }
}
