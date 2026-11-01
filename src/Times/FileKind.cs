using System.Runtime.InteropServices;

namespace Times;

/// <summary>What a path finally points at, as far as reading it is concerned.</summary>
internal enum FileKindResult
{
    /// <summary>Nothing is there, and the path is not a link either.</summary>
    Missing,

    /// <summary>A regular file, directly or through links.</summary>
    Regular,

    /// <summary>
    /// Anything else: a directory, FIFO, device, socket, dangling link, a path that cannot be
    /// checked, or a platform where the check is not supported.
    /// </summary>
    Other,
}

/// <summary>
/// Finds the type of a path's final link target <b>without opening it</b>, so a FIFO or a device
/// never blocks the caller. macOS arm64 and Linux x64/arm64 use libc <c>stat(2)</c>, which follows
/// links; Windows uses the final target's attributes. Any other OS/arch gives
/// <see cref="FileKindResult.Other"/>, so callers fail closed.
/// </summary>
internal static class FileKind
{
    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const int TypeMask = 0xF000;
    private const int RegularType = 0x8000;

    /// <summary>Big enough for <c>struct stat</c> on every supported platform (at most 144 bytes).</summary>
    private const int StatBufferBytes = 256;

    /// <summary>The kind of what <paramref name="path"/> finally points at.</summary>
    public static FileKindResult Of(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return OfWindows(path);
        }

        int? modeOffset = ModeOffset(RuntimeInformation.OSArchitecture);
        if (modeOffset is null)
        {
            return FileKindResult.Other;
        }

        return OfUnix(path, modeOffset.Value);
    }

    /// <summary>
    /// The byte offset of <c>st_mode</c> in <c>struct stat</c> on this OS and
    /// <paramref name="architecture"/>, or null where the layout is not known. On macOS it is a
    /// uint16, on Linux a uint32; the type bits fit in the low 16 bits either way.
    /// </summary>
    internal static int? ModeOffset(Architecture architecture)
    {
        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            return 4;
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return 24;
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            return 16;
        }

        return null;
    }

    private static FileKindResult OfUnix(string path, int modeOffset)
    {
        byte[] buffer = new byte[StatBufferBytes];
        int result;
        int error;
        try
        {
            result = Stat(path, buffer);
            error = Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return FileKindResult.Other;
        }

        if (result != 0)
        {
            bool notThere = error == ENOENT || error == ENOTDIR;
            return notThere && !IsLink(path) ? FileKindResult.Missing : FileKindResult.Other;
        }

        int mode = BitConverter.ToUInt16(buffer, modeOffset);
        return (mode & TypeMask) == RegularType ? FileKindResult.Regular : FileKindResult.Other;
    }

    private static FileKindResult OfWindows(string path)
    {
        if (Directory.Exists(path))
        {
            return FileKindResult.Other;
        }

        FileInfo entry = new FileInfo(path);
        if (entry.LinkTarget is null)
        {
            if (!entry.Exists)
            {
                return FileKindResult.Missing;
            }

            return IsPlainFile(entry.Attributes) ? FileKindResult.Regular : FileKindResult.Other;
        }

        FileSystemInfo? target;
        try
        {
            target = entry.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException)
        {
            return FileKindResult.Other;
        }
        catch (UnauthorizedAccessException)
        {
            return FileKindResult.Other;
        }

        if (target is null || !target.Exists)
        {
            return FileKindResult.Other;
        }

        return IsPlainFile(target.Attributes) ? FileKindResult.Regular : FileKindResult.Other;
    }

    private static bool IsPlainFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;

    /// <summary>Whether <paramref name="path"/> itself is a link (dangling or not). Never follows or opens it.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
