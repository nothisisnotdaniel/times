using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Times;

/// <summary>
/// Reads a small data file (such as a day file, <c>projects.json</c> or <c>settings.json</c>)
/// defensively: the path's final link target must be a regular file, and at most a given number of
/// bytes are taken in, whatever its reported size. <see cref="FileKind"/> checks the path first (for
/// the missing case and on Windows). On macOS arm64 and Linux x64/arm64 the file is then opened with
/// <c>O_NONBLOCK</c>, so a FIFO swapped in after the check never blocks, and the opened descriptor
/// is checked with <c>fstat</c> before anything is read from it. Other platforms fail closed.
/// </summary>
internal static class CappedFile
{
    private const int TypeMask = 0xF000;
    private const int RegularType = 0x8000;
    private const int EACCES = 13;
    private const int EPERM = 1;
    private const int ENOENT = 2;

    /// <summary>Big enough for <c>struct stat</c> on every supported platform (at most 144 bytes).</summary>
    private const int StatBufferBytes = 256;

    /// <summary>
    /// The bytes of the file at <paramref name="path"/>, or <c>null</c> if nothing is there. Throws
    /// <see cref="InvalidDataException"/> with <c>&lt;fileName&gt; is not a regular file.</c> or
    /// <c>&lt;fileName&gt; is too large.</c> when it is not a regular final link target or holds more
    /// than <paramref name="maxBytes"/> bytes (by its length or by what the capped read got).
    /// </summary>
    public static byte[]? Read(string path, string fileName, long maxBytes) =>
        Read(path, fileName, maxBytes, $"{fileName} is too large.");

    /// <summary>
    /// Like <see cref="Read(string, string, long)"/>, but a file over <paramref name="maxBytes"/>
    /// throws <see cref="InvalidDataException"/> with <paramref name="tooLargeMessage"/>.
    /// </summary>
    public static byte[]? Read(string path, string fileName, long maxBytes, string tooLargeMessage)
    {
        FileKindResult kind = FileKind.Of(path);
        if (kind == FileKindResult.Missing)
        {
            return null;
        }

        if (kind != FileKindResult.Regular)
        {
            throw NotRegular(fileName);
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(path, maxBytes, tooLargeMessage);
        }

        return ReadOpened(path, fileName, maxBytes, tooLargeMessage);
    }

    /// <summary>
    /// The open-and-check step on Unix, without the <see cref="FileKind"/> pre-check: opens
    /// <paramref name="path"/> with <c>O_RDONLY | O_NONBLOCK | O_CLOEXEC</c> (so a FIFO with no writer
    /// never blocks), checks the opened descriptor with <c>fstat</c> and reads only from a regular
    /// file, at most <paramref name="maxBytes"/> bytes. The descriptor is always closed. On a
    /// platform with no known <c>struct stat</c> layout or flag values it throws
    /// <c>&lt;fileName&gt; is not a regular file.</c> without opening anything.
    /// </summary>
    internal static byte[] ReadOpened(string path, string fileName, long maxBytes, string tooLargeMessage)
    {
        int? modeOffset = FileKind.CurrentModeOffset;
        int? flags = OpenFlags();
        if (modeOffset is null || flags is null)
        {
            throw NotRegular(fileName);
        }

        int descriptor;
        int error;
        try
        {
            descriptor = Open(path, flags.Value);
            error = Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw NotRegular(fileName);
        }

        if (descriptor < 0)
        {
            throw OpenFailed(path, fileName, error);
        }

        // Owns the descriptor from here on, so it is closed on every path (refused, fstat failed, read).
        using SafeFileHandle handle = new SafeFileHandle(descriptor, ownsHandle: true);
        if (!IsRegular(descriptor, modeOffset.Value))
        {
            throw NotRegular(fileName);
        }

        if (RandomAccess.GetLength(handle) > maxBytes)
        {
            throw new InvalidDataException(tooLargeMessage);
        }

        byte[] bytes;
        using (FileStream stream = new FileStream(handle, FileAccess.Read, bufferSize: 0))
        {
            bytes = ReadAtMost(stream, maxBytes + 1);
        }

        if (bytes.Length > maxBytes)
        {
            throw new InvalidDataException(tooLargeMessage);
        }

        return bytes;
    }

    /// <summary>
    /// Reads from <paramref name="stream"/> until it ends or <paramref name="limit"/> bytes have
    /// arrived, whichever comes first. Never asks the stream for more than <paramref name="limit"/> bytes.
    /// </summary>
    public static byte[] ReadAtMost(Stream stream, long limit)
    {
        using MemoryStream result = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (result.Length < limit)
        {
            int wanted = (int)Math.Min(buffer.Length, limit - result.Length);
            int read = stream.Read(buffer, 0, wanted);
            if (read == 0)
            {
                break;
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    /// <summary>
    /// <c>O_RDONLY | O_NONBLOCK | O_CLOEXEC</c> for this OS (macOS <c>0x4</c>/<c>0x1000000</c>, Linux
    /// <c>0x800</c>/<c>0x80000</c>; <c>O_RDONLY</c> is 0 on both), or null where the values are not known.
    /// </summary>
    internal static int? OpenFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x4 | 0x1000000;
        }

        if (OperatingSystem.IsLinux())
        {
            return 0x800 | 0x80000;
        }

        return null;
    }

    private static byte[] ReadWindows(string path, long maxBytes, string tooLargeMessage)
    {
        byte[] bytes;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > maxBytes)
            {
                throw new InvalidDataException(tooLargeMessage);
            }

            bytes = ReadAtMost(stream, maxBytes + 1);
        }

        if (bytes.Length > maxBytes)
        {
            throw new InvalidDataException(tooLargeMessage);
        }

        return bytes;
    }

    // Reads st_mode from the opened descriptor at the same offset FileKind uses. A failed fstat refuses the file.
    private static bool IsRegular(int descriptor, int modeOffset)
    {
        byte[] buffer = new byte[StatBufferBytes];
        int result;
        try
        {
            result = FStat(descriptor, buffer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        if (result != 0)
        {
            return false;
        }

        int mode = BitConverter.ToUInt16(buffer, modeOffset);
        return (mode & TypeMask) == RegularType;
    }

    private static Exception OpenFailed(string path, string fileName, int error)
    {
        if (error == EACCES || error == EPERM)
        {
            return new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }

        if (error == ENOENT)
        {
            return new FileNotFoundException($"{fileName} is gone.", path);
        }

        return new IOException($"{fileName} cannot be opened (error {error}).");
    }

    private static InvalidDataException NotRegular(string fileName) => new($"{fileName} is not a regular file.");

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, [Out] byte[] buffer);
}
