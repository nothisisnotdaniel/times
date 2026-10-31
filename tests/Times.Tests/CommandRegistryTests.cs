using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

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
        registry.Register("x", "xa", "x", a => { received = a; return CommandResult.None; });

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
        Assert.Throws<ArgumentException>(() => new CommandRegistry().Register(name, "zz", "d", _ => CommandResult.None));
    }

    // AC-9 (040)
    [Fact]
    public void Register_BareName_Works()
    {
        CommandRegistry registry = new CommandRegistry();

        registry.Register("x", "xa", "d", _ => CommandResult.None);

        Assert.Equal(["x"], registry.Commands.Select(c => c.Name));
    }

    [Fact]
    public void Register_RejectsDuplicates_CaseInsensitively()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("HELP", "zz", "d", _ => CommandResult.None));
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
        registry.Register("hello", "hl", "d", _ => CommandResult.None);

        Assert.Null(registry.Complete("hel"));
        Assert.Null(registry.Complete("/hel"));
    }

    // Job 006 ------------------------------------------------------------------------------

    private static CommandRegistry WithNavigation()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("prev", "p", "Go to the previous day", _ => CommandResult.None);
        registry.Register("next", "n", "Go to the next day", _ => CommandResult.None);
        registry.Register("today", "t", "Go back to today", _ => CommandResult.None);
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
        registry.Register("prev", "p", "d", _ => CommandResult.None);
        registry.Register("print", "pi", "d", _ => CommandResult.None);
        registry.Register("project", "pj", "d", _ => CommandResult.None);
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

    // Job 041 ------------------------------------------------------------------------------

    private static CommandResult Nothing(string argument) => CommandResult.None;

    // AC-1
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("/")]
    [InlineData("/a")]
    [InlineData(" ")]
    [InlineData("a ")]
    [InlineData("\t")]
    [InlineData("1")]
    [InlineData("a1")]
    [InlineData("é")]
    [InlineData("-")]
    public void Register_RejectsBadAliases(string alias)
    {
        CommandRegistry registry = new CommandRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register("demo", alias, "d", Nothing));
        Assert.Empty(registry.Commands);
        Assert.False(registry.TryGet("demo", out _));
    }

    // AC-1
    [Theory]
    [InlineData("d")]
    [InlineData("dm")]
    [InlineData("DM")]
    public void Register_AcceptsOneOrTwoAsciiLetters(string alias)
    {
        CommandRegistry registry = new CommandRegistry();

        registry.Register("demo", alias, "d", Nothing);

        Assert.Equal(alias, Assert.Single(registry.Commands).Alias);
    }

    // AC-2: the collision kinds, each on its own.
    [Fact]
    public void Register_AliasEqualToRegisteredName_Throws()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("ab", "x", "d", Nothing);

        Assert.Throws<ArgumentException>(() => registry.Register("other", "AB", "d", Nothing));
        Assert.False(registry.TryGet("other", out _));
    }

    // AC-2
    [Fact]
    public void Register_AliasEqualToRegisteredAlias_Throws()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("other", "H", "d", Nothing));
        Assert.False(registry.TryGet("other", out _));
        Assert.Equal("help", registry.TryGet("h", out Command? help) ? help.Name : null);
    }

    // AC-2
    [Fact]
    public void Register_NameEqualToRegisteredAlias_Throws()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("Q", "zz", "d", Nothing));
        Assert.False(registry.TryGet("zz", out _));
        Assert.Equal(["help", "quit"], registry.Commands.Select(c => c.Name));
    }

    // AC-2
    [Theory]
    [InlineData("go", "go")]
    [InlineData("go", "GO")]
    [InlineData("g", "g")]
    public void Register_AliasEqualToOwnName_Throws(string name, string alias)
    {
        CommandRegistry registry = new CommandRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(name, alias, "d", Nothing));
        Assert.Empty(registry.Commands);
    }

    // AC-2: a duplicate name keeps the first command and does not take the new alias.
    [Fact]
    public void Register_DuplicateName_DoesNotAddTheAlias()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register("help", "zz", "d", Nothing));
        Assert.False(registry.TryGet("zz", out _));
        registry.Register("other", "zz", "d", Nothing);
        Assert.True(registry.TryGet("zz", out Command? other));
        Assert.Equal("other", other.Name);
    }

    // AC-3
    [Theory]
    [InlineData("cp")]
    [InlineData("CP")]
    [InlineData("Cp")]
    [InlineData("checkpoint")]
    [InlineData("CHECKPOINT")]
    public void TryGet_FindsByAliasOrName_IgnoringCase(string typed)
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("checkpoint", "cp", "d", Nothing);

        Assert.True(registry.TryGet(typed, out Command? command));
        Assert.Equal("checkpoint", command.Name);
    }

    // AC-3
    [Theory]
    [InlineData("q")]
    [InlineData("h")]
    public void TryGet_BuiltInAliases(string alias)
    {
        Assert.True(CommandRegistry.CreateDefault().TryGet(alias, out Command? command));
        Assert.Equal(alias, command.Alias);
    }

    private static CommandRegistry WithAppAliases()
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        foreach ((string name, string alias) in new[]
        {
            ("arrive", "a"), ("leave", "l"), ("break", "b"), ("breakend", "be"), ("checkpoint", "cp"),
            ("remove", "r"), ("change", "c"), ("prev", "p"), ("next", "n"), ("today", "t"),
            ("addproject", "ap"), ("projects", "pr"),
        })
        {
            registry.Register(name, alias, "d", Nothing);
        }

        return registry;
    }

    // AC-9
    [Theory]
    [InlineData("cp", "checkpoint")]
    [InlineData("CP", "checkpoint")]
    [InlineData("/cp", "checkpoint")]
    [InlineData("cp 9:00 Standup", "checkpoint")]
    [InlineData("b", "break")]
    [InlineData("be", "breakend")]
    [InlineData("c", "change")]
    [InlineData("p", "prev")]
    [InlineData("pr", "projects")]
    [InlineData("ap", "addproject")]
    [InlineData("a", "arrive")]
    [InlineData("q", "quit")]
    public void Match_ExactAlias_IsListedFirst(string line, string expected)
    {
        Assert.Equal(expected, WithAppAliases().Match(line)[0].Name);
    }

    // AC-9
    [Fact]
    public void Match_B_ListsBreakFirst_ThenBreakend()
    {
        IReadOnlyList<Command> matches = WithAppAliases().Match("b");

        Assert.Equal("break", matches[0].Name);
        Assert.Contains(matches, c => c.Name == "breakend");
    }

    // AC-9: an alias that only starts with the token keeps the command, even when its name does not.
    [Fact]
    public void Match_AliasPrefix_KeepsCommand()
    {
        CommandRegistry registry = new CommandRegistry();
        registry.Register("zeta", "xy", "d", Nothing);

        Assert.Equal(["zeta"], registry.Match("x").Select(c => c.Name));
        Assert.Equal(["zeta"], registry.Match("/X").Select(c => c.Name));
        Assert.Equal(["zeta"], registry.Match("xy").Select(c => c.Name));
    }

    // AC-9: aliases get no typo matching.
    [Theory]
    [InlineData("yx")]
    [InlineData("xz")]
    [InlineData("y")]
    [InlineData("//x")]
    public void Match_AliasTypo_DoesNotMatch(string line)
    {
        CommandRegistry registry = new CommandRegistry();
        registry.Register("zeta", "xy", "d", Nothing);

        Assert.Empty(registry.Match(line));
    }

    // AC-10: Tab completion and prefix lookups stay on names only.
    [Fact]
    public void CompletionHelpers_IgnoreAliases()
    {
        CommandRegistry registry = new CommandRegistry();
        registry.Register("zeta", "xy", "d", Nothing);

        Assert.Null(registry.Complete("x"));
        Assert.Empty(registry.StartingWith("x"));
        Assert.Equal("x", registry.CommonPrefix("x"));
    }

    // AC-7
    [Fact]
    public void RenderHelp_HasAliasColumn_InSteelBlue()
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 80;
        console.Write(CommandRegistry.CreateDefault().RenderHelp());

        string plain = Regex.Replace(console.Output, @"\x1b\[[0-9;]*m", string.Empty);
        Assert.Matches(@"│ Command +│ Alias +│ Description +│", plain);
        Assert.Matches(@"│ help +│ h +│ List all commands +│", plain);
        Assert.Matches(@"│ quit +│ q +│ Exit Times +│", plain);
        Assert.Contains("\u001b[38;5;8mAlias", console.Output);
        Assert.Contains("\u001b[38;5;75mq\u001b[0m", console.Output);
    }
}
