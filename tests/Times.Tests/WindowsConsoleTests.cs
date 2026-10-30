namespace Times.Tests;

public sealed class WindowsConsoleTests
{
    private static string Keys(IReadOnlyList<ConsoleKeyInfo> keys) => new(keys.Select(k => k.KeyChar).ToArray());

    // AC-11
    [Theory]
    [InlineData(0x00780000u)] // +120: one notch up
    [InlineData(0x00010000u)]
    [InlineData(0x7FFF0000u)]
    public void WheelRecord_WithPositiveDelta_IsWheelUp(uint buttonState)
    {
        IReadOnlyList<ConsoleKeyInfo> keys = WindowsConsole.WheelKeys(WindowsConsole.MouseEvent, buttonState, WindowsConsole.MouseWheeled);

        Assert.Equal("\u001b[<64;1;1M", Keys(keys));
        Assert.Equal(ConsoleKey.Escape, keys[0].Key);
        Assert.Equal(MouseInputKind.WheelUp, MouseInput.Decode(Keys(keys)[1..]));
    }

    // AC-11
    [Theory]
    [InlineData(0xFF880000u)] // -120: one notch down
    [InlineData(0xFFFF0000u)]
    [InlineData(0x80000000u)]
    public void WheelRecord_WithNegativeDelta_IsWheelDown(uint buttonState)
    {
        IReadOnlyList<ConsoleKeyInfo> keys = WindowsConsole.WheelKeys(WindowsConsole.MouseEvent, buttonState, WindowsConsole.MouseWheeled);

        Assert.Equal("\u001b[<65;1;1M", Keys(keys));
        Assert.Equal(MouseInputKind.WheelDown, MouseInput.Decode(Keys(keys)[1..]));
    }

    // AC-11
    [Theory]
    [InlineData(WindowsConsole.MouseEvent, 0x1u, 0x0u)]                      // left click
    [InlineData(WindowsConsole.MouseEvent, 0x0u, 0x1u)]                      // move
    [InlineData(WindowsConsole.MouseEvent, 0x1u, 0x2u)]                      // double click
    [InlineData(WindowsConsole.MouseEvent, 0x00780000u, 0x8u)]               // horizontal wheel
    [InlineData(WindowsConsole.MouseEvent, 0x0u, 0x4u)]                      // wheel without a delta
    [InlineData(WindowsConsole.FocusEvent, 0x00780000u, 0x4u)]
    [InlineData(WindowsConsole.WindowBufferSizeEvent, 0x00780000u, 0x4u)]
    [InlineData(WindowsConsole.MenuEvent, 0x0u, 0x0u)]
    [InlineData(WindowsConsole.KeyEvent, 0x0u, 0x0u)]
    public void OtherRecords_ProduceNoKeys(ushort eventType, uint buttonState, uint eventFlags)
    {
        Assert.Empty(WindowsConsole.WheelKeys(eventType, buttonState, eventFlags));
    }
}
