using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>
/// The Windows console pieces for mouse-wheel scrolling: the input-mode seam, an input that turns
/// wheel records into the same SGR keys a Unix terminal sends, and a console that swaps in that
/// input. Only five <c>kernel32</c> calls are used. <see cref="WheelKeys"/> is pure and runs anywhere.
/// </summary>
internal static class WindowsConsole
{
    public const ushort KeyEvent = 0x0001;
    public const ushort MouseEvent = 0x0002;
    public const ushort WindowBufferSizeEvent = 0x0004;
    public const ushort MenuEvent = 0x0008;
    public const ushort FocusEvent = 0x0010;
    public const uint MouseWheeled = 0x0004;

    /// <summary>The synthetic SGR keys (after the Esc) for one wheel notch up.</summary>
    public const string WheelUpKeys = "[<64;1;1M";

    /// <summary>The synthetic SGR keys (after the Esc) for one wheel notch down.</summary>
    public const string WheelDownKeys = "[<65;1;1M";

    /// <summary>
    /// The keys one input record stands for: Esc plus <see cref="WheelUpKeys"/> or
    /// <see cref="WheelDownKeys"/> for a vertical wheel record (the high word of
    /// <paramref name="buttonState"/> is the signed wheel delta: positive is up), and none for any
    /// other mouse, focus, menu or resize record. Key records are not translated here.
    /// </summary>
    public static IReadOnlyList<ConsoleKeyInfo> WheelKeys(ushort eventType, uint buttonState, uint eventFlags)
    {
        if (eventType != MouseEvent || eventFlags != MouseWheeled)
        {
            return [];
        }

        short delta = unchecked((short)(buttonState >> 16));
        if (delta == 0)
        {
            return [];
        }

        string keys = delta > 0 ? WheelUpKeys : WheelDownKeys;
        List<ConsoleKeyInfo> result = [new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)];
        foreach (char c in keys)
        {
            result.Add(new ConsoleKeyInfo(c, default, false, false, false));
        }

        return result;
    }
}

/// <summary>The console input mode of the standard input handle, through <c>Get/SetConsoleMode</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConsoleMode : IConsoleInputMode
{
    public uint? Get() =>
        NativeMethods.GetConsoleMode(NativeMethods.InputHandle(), out uint mode) ? mode : null;

    public void Set(uint mode) => NativeMethods.SetConsoleMode(NativeMethods.InputHandle(), mode);
}

/// <summary>
/// Console input that leaves key presses to <see cref="Console.ReadKey(bool)"/> but first takes wheel
/// records off the console input queue and hands them out as the keys from
/// <see cref="WindowsConsole.WheelKeys"/>. Other mouse, focus, menu and resize records and key releases
/// are dropped, so <see cref="Console.ReadKey(bool)"/> never swallows a wheel record while it waits.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConsoleInput : IAnsiConsoleInput
{
    private const ushort AltKey = 0x12;

    // Shift, Ctrl, Alt, Caps Lock, Num Lock, Scroll Lock: Console.ReadKey skips these and would then
    // block, swallowing any wheel record that comes in while it waits.
    private static readonly ushort[] ModifierKeys = [0x10, 0x11, AltKey, 0x14, 0x90, 0x91];
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);

    private readonly Queue<ConsoleKeyInfo> queued = new();

    public bool IsKeyAvailable() => queued.Count > 0 || Fill();

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (true)
        {
            if (queued.Count > 0)
            {
                return queued.Dequeue();
            }

            if (Fill())
            {
                if (queued.Count > 0)
                {
                    return queued.Dequeue();
                }

                return Console.ReadKey(intercept);
            }

            Thread.Sleep(Poll);
        }
    }

    public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsKeyAvailable())
            {
                return ReadKey(intercept);
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    // Looks at the records waiting in the console input queue. Wheel records are turned into queued
    // keys, records Console.ReadKey has no use for are removed. Returns true once there are queued
    // keys or a key press is first in line (for Console.ReadKey), false if the queue ran empty.
    private bool Fill()
    {
        IntPtr handle = NativeMethods.InputHandle();
        while (true)
        {
            if (!NativeMethods.PeekConsoleInput(handle, out NativeMethods.InputRecord record, 1, out uint count) || count == 0)
            {
                return queued.Count > 0;
            }

            if (record.EventType == WindowsConsole.KeyEvent && IsForReadKey(record))
            {
                return true;
            }

            NativeMethods.ReadConsoleInput(handle, out record, 1, out _);
            foreach (ConsoleKeyInfo key in WindowsConsole.WheelKeys(record.EventType, record.ButtonState, record.EventFlags))
            {
                queued.Enqueue(key);
            }

            if (queued.Count > 0)
            {
                return true;
            }
        }
    }

    // A key press other than a lone modifier, or the Alt release that carries an Alt+numpad character.
    private static bool IsForReadKey(NativeMethods.InputRecord record)
    {
        if (record.KeyDown != 0)
        {
            return !ModifierKeys.Contains(record.VirtualKeyCode);
        }

        return record.VirtualKeyCode == AltKey && record.UnicodeChar != 0;
    }
}

/// <summary>An <see cref="IAnsiConsole"/> that is another console with only its <see cref="Input"/> replaced.</summary>
internal sealed class InputOverrideConsole : IAnsiConsole
{
    private readonly IAnsiConsole inner;

    public InputOverrideConsole(IAnsiConsole inner, IAnsiConsoleInput input)
    {
        this.inner = inner;
        Input = input;
    }

    public Profile Profile => inner.Profile;

    public IAnsiConsoleCursor Cursor => inner.Cursor;

    public IAnsiConsoleInput Input { get; }

    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;

    public RenderPipeline Pipeline => inner.Pipeline;

    public void Clear(bool home) => inner.Clear(home);

    public void Write(IRenderable renderable) => inner.Write(renderable);

    public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
}

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    private const int StdInputHandle = -10;

    public static IntPtr InputHandle() => GetStdHandle(StdInputHandle);

    // INPUT_RECORD: a WORD event type, then (at offset 4) a 16-byte union of KEY_EVENT_RECORD and
    // MOUSE_EVENT_RECORD. Only the fields Times reads are mapped.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        // KEY_EVENT_RECORD.bKeyDown
        [FieldOffset(4)]
        public int KeyDown;

        // KEY_EVENT_RECORD.wVirtualKeyCode
        [FieldOffset(10)]
        public ushort VirtualKeyCode;

        // KEY_EVENT_RECORD.uChar
        [FieldOffset(14)]
        public ushort UnicodeChar;

        // MOUSE_EVENT_RECORD.dwButtonState
        [FieldOffset(8)]
        public uint ButtonState;

        // MOUSE_EVENT_RECORD.dwEventFlags
        [FieldOffset(16)]
        public uint EventFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", EntryPoint = "PeekConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekConsoleInput(IntPtr hConsoleInput, out InputRecord lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadConsoleInput(IntPtr hConsoleInput, out InputRecord lpBuffer, uint nLength, out uint lpNumberOfEventsRead);
}
