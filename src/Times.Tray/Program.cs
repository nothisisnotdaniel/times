namespace Times.Tray;

/// <summary>Starts the tray app: no window, no Dock icon on macOS, running until Quit.</summary>
internal static class Program
{
    [STAThread]
    public static int Main()
    {
#if WINDOWS
        return WindowsTray.Run();
#else
        if (OperatingSystem.IsMacOS())
        {
            return MacTray.Run();
        }

        Console.Error.WriteLine("Times.Tray runs on macOS and Windows only.");
        return 1;
#endif
    }

    /// <summary>The PNG bytes of the <c>in</c> or <c>out</c> icon.</summary>
    public static byte[] IconBytes(TrayIconState state)
    {
        string name = state == TrayIconState.In ? "in" : "out";
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream($"Times.Tray.{name}.png")
            ?? throw new InvalidOperationException($"The {name} icon is not embedded.");
        using MemoryStream copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
