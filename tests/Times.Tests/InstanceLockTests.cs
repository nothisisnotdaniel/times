using System.Globalization;

namespace Times.Tests;

public sealed class InstanceLockTests : IDisposable
{
    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private string LockFile => Path.Combine(data.Path, InstanceLock.FileName);

    private static string PidLine => "pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    // AC-1
    [Fact]
    public void TryAcquire_NoFile_CreatesItWithThePid()
    {
        using InstanceLock instance = new InstanceLock(data.Path);

        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);

        Assert.Equal("times.lock", InstanceLock.FileName);
        Assert.Equal(LockFile, instance.LockPath);
        Assert.Equal(PidLine, ReadShared(LockFile).TrimEnd('\n'));
    }

    // AC-1: a leftover file no handle holds is taken over, whatever it holds.
    [Theory]
    [InlineData("")]
    [InlineData("garbage \u0000ÿ and more lines\nthan one\n")]
    [InlineData("pid=1")]
    public void TryAcquire_LeftoverFileWithoutLiveLock_TakesItOver(string content)
    {
        File.WriteAllText(LockFile, content);
        using InstanceLock instance = new InstanceLock(data.Path);

        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);

        Assert.Equal(PidLine, ReadShared(LockFile).TrimEnd('\n'));
    }

    // AC-1: the folder is created if it is missing.
    [Fact]
    public void TryAcquire_MissingFolder_CreatesIt()
    {
        string folder = Path.Combine(data.Path, "nested", "Times");
        using InstanceLock instance = new InstanceLock(folder);

        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);

        Assert.True(File.Exists(Path.Combine(folder, InstanceLock.FileName)));
    }

    // AC-2: a live lock is held; the holder's file is left as it is.
    [Fact]
    public void TryAcquire_WhileAnotherHoldsIt_IsHeld_AndLeavesTheFileAlone()
    {
        using InstanceLock first = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, first.TryAcquire().Status);
        string before = ReadShared(LockFile);

        using InstanceLock second = new InstanceLock(data.Path);
        InstanceLockResult result = second.TryAcquire();

        Assert.Equal(InstanceLockStatus.Held, result.Status);
        Assert.Equal(string.Empty, result.Reason);
        Assert.Equal(before, ReadShared(LockFile));

        second.Dispose();
        Assert.True(File.Exists(LockFile));
        Assert.Equal(before, ReadShared(LockFile));
    }

    // AC-6
    [Fact]
    public void Dispose_Twice_DeletesTheFile_AndANewInstanceAcquires()
    {
        InstanceLock instance = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);

        instance.Dispose();
        instance.Dispose();

        Assert.False(File.Exists(LockFile));
        using InstanceLock next = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, next.TryAcquire().Status);
    }

    // AC-6
    [Fact]
    public async Task Dispose_FromOtherThreads_AtOnce_NeverThrows()
    {
        InstanceLock instance = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);

        Task[] tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(instance.Dispose)).ToArray();
        await Task.WhenAll(tasks);
        instance.Dispose();

        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
        Assert.False(File.Exists(LockFile));
        using InstanceLock next = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, next.TryAcquire().Status);
    }

    // AC-6: disposing a lock that was never taken, or was refused, does nothing.
    [Fact]
    public void Dispose_WithoutTheLock_TouchesNothing()
    {
        using InstanceLock holder = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, holder.TryAcquire().Status);
        InstanceLock never = new InstanceLock(data.Path);
        InstanceLock refused = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Held, refused.TryAcquire().Status);

        never.Dispose();
        refused.Dispose();

        Assert.True(File.Exists(LockFile));
        using InstanceLock other = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Held, other.TryAcquire().Status);
    }

    [Fact]
    public void TryAcquire_WhileHoldingIt_IsAcquiredAgain()
    {
        using InstanceLock instance = new InstanceLock(data.Path);

        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);
        Assert.Equal(InstanceLockStatus.Acquired, instance.TryAcquire().Status);
    }

    // AC-4: a link is refused and never followed, deleted or changed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryAcquire_SymbolicLink_FailsAndLeavesLinkAndTarget(bool targetExists)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder elsewhere = new TempFolder();
        string target = Path.Combine(elsewhere.Path, "target.txt");
        if (targetExists)
        {
            File.WriteAllText(target, "keep me");
        }

        File.CreateSymbolicLink(LockFile, target);
        using InstanceLock instance = new InstanceLock(data.Path);

        InstanceLockResult result = instance.TryAcquire();

        Assert.Equal(InstanceLockStatus.Failed, result.Status);
        Assert.Contains("symbolic link", result.Reason);
        Assert.Equal(target, new FileInfo(LockFile).LinkTarget);
        if (targetExists)
        {
            Assert.Equal("keep me", File.ReadAllText(target));
        }
        else
        {
            Assert.False(File.Exists(target));
        }

        instance.Dispose();
        Assert.Equal(target, new FileInfo(LockFile).LinkTarget);
    }

    // AC-4
    [Fact]
    public void TryAcquire_Directory_Fails()
    {
        Directory.CreateDirectory(LockFile);
        using InstanceLock instance = new InstanceLock(data.Path);

        InstanceLockResult result = instance.TryAcquire();

        Assert.Equal(InstanceLockStatus.Failed, result.Status);
        Assert.Contains("directory", result.Reason);
        Assert.True(Directory.Exists(LockFile));
    }

    // AC-4: a FIFO is refused without blocking.
    [Fact]
    public void TryAcquire_Fifo_FailsWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(LockFile);
        using InstanceLock instance = new InstanceLock(data.Path);
        InstanceLockResult? result = null;

        Assert.Null(SpecialFiles.RunWithin(TimeSpan.FromSeconds(5), () => result = instance.TryAcquire()));

        Assert.Equal(InstanceLockStatus.Failed, result!.Status);
        Assert.Contains("not a regular file", result.Reason);
        Assert.True(SpecialFiles.IsFifo(LockFile));
    }

    // AC-4: the folder cannot be created (a file stands in its way).
    [Fact]
    public void TryAcquire_FolderCannotBeCreated_Fails()
    {
        string blocker = Path.Combine(data.Path, "file");
        File.WriteAllText(blocker, "x");
        using InstanceLock instance = new InstanceLock(Path.Combine(blocker, "Times"));

        InstanceLockResult result = instance.TryAcquire();

        Assert.Equal(InstanceLockStatus.Failed, result.Status);
        Assert.StartsWith("the data folder cannot be created", result.Reason);
    }

    // AC-4: the folder cannot be written.
    [Fact]
    public void TryAcquire_ReadOnlyFolder_Fails_AndCreatesNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanWrite(data.Path))
            {
                // Running as root: access can't be denied.
                return;
            }

            using InstanceLock instance = new InstanceLock(data.Path);
            InstanceLockResult result = instance.TryAcquire();

            Assert.Equal(InstanceLockStatus.Failed, result.Status);
            Assert.StartsWith("times.lock cannot be opened", result.Reason);
            Assert.False(File.Exists(LockFile));
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // Security note: a release unlinks the file before it lets go of the lock. A new holder must lock the
    // file the path names now, so a third instance sees it held.
    [Fact]
    public void ReleaseAndReacquire_InTurns_AlwaysLocksTheFileThePathNames()
    {
        for (int i = 0; i < 20; i++)
        {
            using InstanceLock holder = new InstanceLock(data.Path);
            Assert.Equal(InstanceLockStatus.Acquired, holder.TryAcquire().Status);
            using InstanceLock other = new InstanceLock(data.Path);
            Assert.Equal(InstanceLockStatus.Held, other.TryAcquire().Status);
            holder.Dispose();
            Assert.Equal(InstanceLockStatus.Acquired, other.TryAcquire().Status);
            using InstanceLock third = new InstanceLock(data.Path);
            Assert.Equal(InstanceLockStatus.Held, third.TryAcquire().Status);
        }
    }

    /// <summary>
    /// Reads the lock file without taking a lock of our own (a plain FileStream would ask for a shared
    /// lock and be refused while the file is held): CappedFile opens it through libc.
    /// </summary>
    internal static string ReadShared(string path) =>
        System.Text.Encoding.ASCII.GetString(CappedFile.Read(path, "times.lock", 1024) ?? []);

    private static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, "probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
