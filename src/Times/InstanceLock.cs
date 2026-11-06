using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Times;

/// <summary>What <see cref="InstanceLock.TryAcquire"/> found.</summary>
internal enum InstanceLockStatus
{
    /// <summary>This instance holds the lock now.</summary>
    Acquired,

    /// <summary>Another open handle holds the lock: another Times runs with this data folder.</summary>
    Held,

    /// <summary>The lock could not be taken for another reason (see <see cref="InstanceLockResult.Reason"/>).</summary>
    Failed,
}

/// <summary>The outcome of <see cref="InstanceLock.TryAcquire"/>: a status and, for <see cref="InstanceLockStatus.Failed"/>, a plain-text reason.</summary>
internal sealed class InstanceLockResult
{
    private InstanceLockResult(InstanceLockStatus status, string reason)
    {
        Status = status;
        Reason = reason;
    }

    public static InstanceLockResult Acquired { get; } = new InstanceLockResult(InstanceLockStatus.Acquired, string.Empty);

    public static InstanceLockResult Held { get; } = new InstanceLockResult(InstanceLockStatus.Held, string.Empty);

    public InstanceLockStatus Status { get; }

    /// <summary>Why the lock could not be taken (plain text, not markup); empty unless <see cref="InstanceLockStatus.Failed"/>.</summary>
    public string Reason { get; }

    public static InstanceLockResult Failed(string reason) => new InstanceLockResult(InstanceLockStatus.Failed, reason);
}

/// <summary>
/// Keeps a second Times off the same data folder. The lock is an OS-level exclusive lock on
/// <c>&lt;folder&gt;/times.lock</c>, held through a <see cref="FileStream"/> opened with
/// <see cref="FileShare.None"/> (<c>flock(LOCK_EX|LOCK_NB)</c> on Unix, the share mode on Windows). It is
/// never inferred from the file existing: a file left behind by a crashed run has no live lock, so it is
/// opened, truncated and taken over. A link, directory or other non-regular <c>times.lock</c> is refused
/// before anything is opened, and never followed, deleted or changed. The lock is advisory
/// (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1</c> turns it off on Unix).
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    /// <summary>The lock file's name inside the data folder.</summary>
    public const string FileName = "times.lock";

    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int EWOULDBLOCK_MACOS = 35;
    private const int EWOULDBLOCK_LINUX = 11;
    private const int StatBufferBytes = 256;

    // st_ino is a 64-bit value at offset 8 in struct stat on macOS arm64, Linux x64 and Linux arm64.
    private const int InodeOffset = 8;

    private readonly object gate = new object();
    private FileStream? stream;

    public InstanceLock(string folder)
    {
        Folder = folder;
        LockPath = Path.Combine(folder, FileName);
    }

    public string Folder { get; }

    /// <summary>The full path of <see cref="FileName"/> in <see cref="Folder"/>.</summary>
    public string LockPath { get; }

    /// <summary>
    /// Takes the lock: creates the folder if it is missing, refuses a <c>times.lock</c> that is a link or not
    /// a regular file, then opens (or creates) it exclusively and writes <c>pid=&lt;process id&gt;</c>. Returns
    /// <see cref="InstanceLockStatus.Held"/>, leaving the file untouched, when another handle holds it.
    /// Already holding it gives <see cref="InstanceLockStatus.Acquired"/> again.
    /// </summary>
    public InstanceLockResult TryAcquire()
    {
        lock (gate)
        {
            if (stream is not null)
            {
                return InstanceLockResult.Acquired;
            }

            try
            {
                Directory.CreateDirectory(Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return InstanceLockResult.Failed($"the data folder cannot be created: {ex.Message}");
            }

            // Once more after a release unlinked the file this one locked (see SamePath).
            for (int attempt = 0; attempt < 2; attempt++)
            {
                InstanceLockResult? refused = CheckKind();
                if (refused is not null)
                {
                    return refused;
                }

                FileStream opened;
                try
                {
                    FileOptions options = OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None;
                    opened = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, options);
                }
                catch (IOException ex) when (IsLockConflict(ex))
                {
                    return InstanceLockResult.Held;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return InstanceLockResult.Failed($"{FileName} cannot be opened: {ex.Message}");
                }

                if (!SamePath(opened))
                {
                    opened.Dispose();
                    continue;
                }

                try
                {
                    byte[] content = Encoding.ASCII.GetBytes(
                        "pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
                    opened.SetLength(0);
                    opened.Write(content, 0, content.Length);
                    opened.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Release(opened);
                    return InstanceLockResult.Failed($"{FileName} cannot be written: {ex.Message}");
                }

                stream = opened;
                return InstanceLockResult.Acquired;
            }

            return InstanceLockResult.Failed($"{FileName} kept changing while it was opened");
        }
    }

    /// <summary>Deletes the lock file, then releases the lock. Safe to call more than once and from any thread.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            FileStream? held = stream;
            stream = null;
            if (held is not null)
            {
                Release(held);
            }
        }
    }

    // Unix: unlink first, then close, so no one sees an unlocked times.lock. Windows: the handle was opened
    // with DeleteOnClose, so closing it deletes the file.
    private void Release(FileStream held)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (SamePath(held))
                {
                    File.Delete(LockPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The lock is released below anyway; a file left behind is taken over by the next start.
            }
        }

        held.Dispose();
    }

    // A link (dangling or not) or anything but a regular file is refused before it is opened.
    private InstanceLockResult? CheckKind()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(LockPath);
            if (info.LinkTarget is not null)
            {
                return InstanceLockResult.Failed($"{FileName} is a symbolic link");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InstanceLockResult.Failed($"{FileName} cannot be checked: {ex.Message}");
        }

        if (Directory.Exists(LockPath))
        {
            return InstanceLockResult.Failed($"{FileName} is a directory");
        }

        FileKindResult kind = FileKind.Of(LockPath);
        if (kind == FileKindResult.Missing || kind == FileKindResult.Regular)
        {
            return null;
        }

        return InstanceLockResult.Failed($"{FileName} is not a regular file");
    }

    // Windows reports a sharing or lock violation (HRESULT 0x80070020/21). On Unix .NET reports the flock errno
    // itself as the HResult: EWOULDBLOCK, 35 on macOS and 11 on Linux.
    private static bool IsLockConflict(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            int code = ex.HResult & 0xFFFF;
            return code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION;
        }

        return ex.HResult == (OperatingSystem.IsMacOS() ? EWOULDBLOCK_MACOS : EWOULDBLOCK_LINUX);
    }

    /// <summary>
    /// Whether <see cref="LockPath"/> still names the file <paramref name="opened"/> is open on (same device and
    /// inode). A releasing holder unlinks the file before it lets go of the lock, so a new opener can lock the
    /// old, unlinked file; this tells it to open again. Windows (no unlink while open) and platforms without a
    /// known <c>struct stat</c> layout answer true.
    /// </summary>
    private bool SamePath(FileStream opened)
    {
        if (OperatingSystem.IsWindows() || FileKind.CurrentModeOffset is null)
        {
            return true;
        }

        byte[] byHandle = new byte[StatBufferBytes];
        byte[] byPath = new byte[StatBufferBytes];
        try
        {
            int descriptor = (int)opened.SafeFileHandle.DangerousGetHandle();
            if (FStat(descriptor, byHandle) != 0)
            {
                return false;
            }

            if (Stat(LockPath, byPath) != 0)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }

        // st_dev: 4 bytes on macOS, 8 on Linux; the first 4 are compared on both, then the inode.
        return BitConverter.ToInt32(byHandle, 0) == BitConverter.ToInt32(byPath, 0)
            && BitConverter.ToUInt64(byHandle, InodeOffset) == BitConverter.ToUInt64(byPath, InodeOffset);
    }

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
