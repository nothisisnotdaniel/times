using Spectre.Console;

namespace Times;

/// <summary>Reads and sets the console's input mode (Windows only; see <see cref="WindowsConsoleMode"/>).</summary>
internal interface IConsoleInputMode
{
    /// <summary>The current input mode, or <c>null</c> if it cannot be read (e.g. input is redirected).</summary>
    public uint? Get();

    public void Set(uint mode);
}

/// <summary>
/// Turns terminal mouse reporting (X10 button events, 1000, in SGR encoding, 1006) on and off, so the
/// wheel reaches the line editor instead of the terminal's scrollback, and bracketed paste (2004) with
/// it, so a paste arrives between <c>ESC[200~</c> and <c>ESC[201~</c> and its line breaks never act as
/// Enter. Bracketed paste is turned off first and on last. The escape sequences go
/// through the injected <see cref="IAnsiConsole"/> as a <see cref="ControlCode"/>. On Windows an
/// optional <see cref="IConsoleInputMode"/> also turns on mouse input (and quick edit off) and puts
/// the saved mode back on <see cref="Disable"/>.
/// <see cref="Disable"/> can be called from any thread (Ctrl+C, process exit), more than once; it
/// does nothing unless <see cref="Enable"/> ran since the last disable.
/// </summary>
internal sealed class MouseReporting
{
    public const string EnableSequence = "\u001b[?1000h\u001b[?1006h\u001b[?2004h";
    public const string DisableSequence = "\u001b[?2004l\u001b[?1006l\u001b[?1000l";

    public const uint EnableMouseInput = 0x10;
    public const uint EnableQuickEditMode = 0x40;
    public const uint EnableExtendedFlags = 0x80;

    private readonly IAnsiConsole console;
    private readonly IConsoleInputMode? inputMode;
    private readonly Lock gate = new();
    private bool enabled;
    private uint? savedMode;

    public MouseReporting(IAnsiConsole console, IConsoleInputMode? inputMode = null)
    {
        this.console = console;
        this.inputMode = inputMode;
    }

    public void Enable()
    {
        lock (gate)
        {
            if (enabled)
            {
                return;
            }

            enabled = true;
            savedMode = inputMode?.Get();
            if (savedMode is { } saved)
            {
                inputMode!.Set((saved | EnableMouseInput | EnableExtendedFlags) & ~EnableQuickEditMode);
            }

            console.Write(new ControlCode(EnableSequence));
        }
    }

    public void Disable()
    {
        lock (gate)
        {
            if (!enabled)
            {
                return;
            }

            enabled = false;
            console.Write(new ControlCode(DisableSequence));
            if (savedMode is { } saved)
            {
                inputMode!.Set(saved);
                savedMode = null;
            }
        }
    }
}
