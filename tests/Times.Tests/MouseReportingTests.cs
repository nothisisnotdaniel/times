using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class MouseReportingTests
{
    private sealed class FakeInputMode : IConsoleInputMode
    {
        private readonly uint? initial;

        public FakeInputMode(uint? initial)
        {
            this.initial = initial;
        }

        public List<uint> Sets { get; } = [];

        public uint? Get() => initial;

        public void Set(uint mode) => Sets.Add(mode);
    }

    private static TestConsole Console() => new TestConsole().EmitAnsiSequences();

    // AC-1, AC-4
    [Fact]
    public void Enable_ThenDisable_WritesBothSequences_InOrder()
    {
        TestConsole console = Console();
        MouseReporting mouse = new MouseReporting(console);

        mouse.Enable();
        mouse.Disable();

        Assert.Equal("\u001b[?1000h\u001b[?1006h\u001b[?2004h\u001b[?2004l\u001b[?1006l\u001b[?1000l", console.Output);
    }

    // AC-10 (066)
    [Fact]
    public void Enable_TurnsBracketedPasteOn_AndDisable_TurnsItOff()
    {
        TestConsole console = Console();
        MouseReporting mouse = new MouseReporting(console);

        mouse.Enable();
        string enabled = console.Output;
        mouse.Disable();
        string disabled = console.Output[enabled.Length..];

        Assert.Contains("\u001b[?2004h", enabled);
        Assert.DoesNotContain("\u001b[?2004l", enabled);
        Assert.Contains("\u001b[?2004l", disabled);
        Assert.DoesNotContain("\u001b[?2004h", disabled);
        Assert.Contains("\u001b[?2004h", MouseReporting.EnableSequence);
        Assert.Contains("\u001b[?2004l", MouseReporting.DisableSequence);
    }

    // AC-4
    [Fact]
    public void Disable_WithoutEnable_WritesNothing()
    {
        TestConsole console = Console();
        FakeInputMode mode = new FakeInputMode(0x1F7);

        new MouseReporting(console, mode).Disable();

        Assert.Equal(string.Empty, console.Output);
        Assert.Empty(mode.Sets);
    }

    // AC-4
    [Fact]
    public void SecondDisable_WritesNothing()
    {
        TestConsole console = Console();
        FakeInputMode mode = new FakeInputMode(0x1F7);
        MouseReporting mouse = new MouseReporting(console, mode);
        mouse.Enable();
        mouse.Disable();
        string afterFirst = console.Output;

        mouse.Disable();

        Assert.Equal(afterFirst, console.Output);
        Assert.Equal(2, mode.Sets.Count);
    }

    // AC-4
    [Theory]
    [InlineData(0x1F7u)] // a typical cmd.exe mode, quick edit on
    [InlineData(0x0u)]
    [InlineData(0x2E7u)] // quick edit on, virtual terminal input on
    [InlineData(0xFFFFFFFFu)]
    public void InputMode_IsSetForMouse_AndRestoredExactly(uint saved)
    {
        FakeInputMode mode = new FakeInputMode(saved);
        MouseReporting mouse = new MouseReporting(Console(), mode);

        mouse.Enable();
        Assert.Equal([(saved | 0x10u | 0x80u) & ~0x40u], mode.Sets);

        mouse.Disable();
        Assert.Equal([(saved | 0x10u | 0x80u) & ~0x40u, saved], mode.Sets);
    }

    [Fact]
    public void InputMode_ThatCannotBeRead_IsNeverSet()
    {
        TestConsole console = Console();
        FakeInputMode mode = new FakeInputMode(null);
        MouseReporting mouse = new MouseReporting(console, mode);

        mouse.Enable();
        mouse.Disable();

        Assert.Empty(mode.Sets);
        Assert.Contains("\u001b[?1006l\u001b[?1000l", console.Output);
    }

    [Fact]
    public void EnableAgain_AfterDisable_WritesTheSequenceAgain()
    {
        TestConsole console = Console();
        MouseReporting mouse = new MouseReporting(console);

        mouse.Enable();
        mouse.Enable();
        mouse.Disable();
        mouse.Enable();
        mouse.Disable();

        Assert.Equal(2, console.Output.Split("\u001b[?1000h").Length - 1);
        Assert.Equal(2, console.Output.Split("\u001b[?1000l").Length - 1);
    }
}
