using System.Runtime.InteropServices;

namespace Times.Tray;

/// <summary>The time since the last keyboard or mouse input on Windows, from user32's <c>GetLastInputInfo</c>.</summary>
internal sealed partial class WindowsIdleSource : IIdleSource
{
    public TimeSpan? SinceLastInput()
    {
        LastInputInfo info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both are milliseconds since boot in 32 bits; unsigned subtraction stays right across the wrap.
        uint idle = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(idle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
