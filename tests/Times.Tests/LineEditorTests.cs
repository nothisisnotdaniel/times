using Spectre.Console.Testing;

namespace Times.Tests;

public class LineEditorTests
{
    private static (TestConsole Console, LineEditor Editor) Create()
    {
        var console = new TestConsole().Interactive();
        return (console, new LineEditor(console, CommandRegistry.CreateDefault()));
    }

    private static string? Read(LineEditor editor) => editor.ReadLine(5, 23, 70);

    [Fact]
    public void PrintableCharacters_AndEnter_ReturnTheLine()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/help me");

        Assert.Equal("/help me", Read(editor));
    }

    [Fact]
    public void TypedText_IsEchoed()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/hel");

        Read(editor);

        Assert.Contains("/hel", console.Output);
    }

    // AC-10
    [Fact]
    public void Backspace_RemovesLastCharacter()
    {
        var (console, editor) = Create();
        console.Input.PushText("/quiz");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("t");

        Assert.Equal("/quit", Read(editor));
    }

    [Fact]
    public void Backspace_OnEmptyLine_DoesNothing()
    {
        var (console, editor) = Create();
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushTextWithEnter("/q");

        Assert.Equal("/q", Read(editor));
    }

    [Fact]
    public void Escape_ClearsTheLine()
    {
        var (console, editor) = Create();
        console.Input.PushText("abc");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushTextWithEnter("/quit");

        Assert.Equal("/quit", Read(editor));
    }

    // AC-11
    [Fact]
    public void Tab_CompletesUniquePrefix()
    {
        var (console, editor) = Create();
        console.Input.PushText("/q");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/quit", Read(editor));
    }

    [Theory]
    [InlineData("/x")]  // no match
    [InlineData("/")]   // matches /help and /quit
    public void Tab_LeavesNoOrAmbiguousMatchUnchanged(string prefix)
    {
        var (console, editor) = Create();
        console.Input.PushText(prefix);
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(prefix, Read(editor));
    }

    [Fact]
    public void Tab_AfterArguments_DoesNothing()
    {
        var (console, editor) = Create();
        console.Input.PushText("/q x");
        console.Input.PushKey(ConsoleKey.Tab);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/q x", Read(editor));
    }

    // AC-12
    [Fact]
    public void Up_RecallsPreviousLine()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/help");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("/help", Read(editor));
        Assert.Equal("/help", Read(editor));
    }

    [Fact]
    public void Up_PastOldest_StaysOnOldest()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/one", Read(editor));
    }

    [Fact]
    public void Down_PastNewest_GivesEmptyLine()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("", Read(editor));
    }

    [Fact]
    public void Down_MovesToNewerEntry()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/one");
        console.Input.PushTextWithEnter("/two");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/two", Read(editor));
    }

    [Fact]
    public void Up_WithNoHistory_LeavesLineEmpty()
    {
        var (console, editor) = Create();
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal("", Read(editor));
    }

    [Fact]
    public void EmptyLines_AreNotAddedToHistory()
    {
        var (console, editor) = Create();
        console.Input.PushTextWithEnter("/help");
        console.Input.PushTextWithEnter("");
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Read(editor);
        Read(editor);
        Assert.Equal("/help", Read(editor));
    }
}
