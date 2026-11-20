using System.Text;

namespace Times.Tests;

public class CappedFileTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static bool Supported => !OperatingSystem.IsWindows() && FileKind.CurrentModeOffset is not null;

    // AC-8 (086): the open-and-fstat step alone (no FileKind pre-check) never blocks on a FIFO.
    [Fact]
    public void ReadOpened_Fifo_IsRefused_WithoutBlocking_AndStaysAFifo()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "projects.json");
        SpecialFiles.MakeFifo(path);

        Exception? thrown = SpecialFiles.RunWithin(Timeout, () => CappedFile.ReadOpened(path, "projects.json", 64, "too large"));

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal("projects.json is not a regular file.", ex.Message);
        Assert.True(SpecialFiles.IsFifo(path));
    }

    // AC-8 (086)
    [Fact]
    public void ReadOpened_LinkToFifo_IsRefused_WithoutBlocking()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string fifo = Path.Combine(temp.Path, "pipe");
        string path = Path.Combine(temp.Path, "settings.json");
        SpecialFiles.MakeFifo(fifo);
        File.CreateSymbolicLink(path, fifo);

        Exception? thrown = SpecialFiles.RunWithin(Timeout, () => CappedFile.ReadOpened(path, "settings.json", 64, "too large"));

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal("settings.json is not a regular file.", ex.Message);
        Assert.True(SpecialFiles.IsFifo(fifo));
    }

    // AC-8 (086)
    [Fact]
    public void ReadOpened_Directory_IsRefused()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "dir");
        Directory.CreateDirectory(path);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CappedFile.ReadOpened(path, "dir", 64, "too large"));
        Assert.Equal("dir is not a regular file.", ex.Message);
    }

    // AC-8 (086)
    [Fact]
    public void ReadOpened_RegularFile_ReturnsItsBytes()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "projects.json");
        byte[] contents = Encoding.UTF8.GetBytes("{\"projects\":[]}");
        File.WriteAllBytes(path, contents);

        byte[]? bytes = null;
        Assert.Null(SpecialFiles.RunWithin(Timeout, () => bytes = CappedFile.ReadOpened(path, "projects.json", 64, "too large")));

        Assert.Equal(contents, bytes);
    }

    // AC-8 (086)
    [Fact]
    public void ReadOpened_FileOverTheCap_ThrowsTheTooLargeMessage()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "big.json");
        File.WriteAllBytes(path, new byte[65]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CappedFile.ReadOpened(path, "big.json", 64, "big.json is too large."));
        Assert.Equal("big.json is too large.", ex.Message);
    }

    // AC-8 (086)
    [Fact]
    public void ReadOpened_MissingFile_IsAnIOException()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();

        Assert.IsAssignableFrom<IOException>(Record.Exception(() => CappedFile.ReadOpened(Path.Combine(temp.Path, "gone.json"), "gone.json", 64, "too large")));
    }

    // AC-8 (086): the descriptor is closed after every outcome, so many reads never run out of descriptors.
    [Fact]
    public void ReadOpened_ClosesTheDescriptor_AfterRefusalsAndReads()
    {
        if (!Supported)
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string fifo = Path.Combine(temp.Path, "pipe");
        string file = Path.Combine(temp.Path, "file.json");
        SpecialFiles.MakeFifo(fifo);
        File.WriteAllBytes(file, new byte[10]);
        int before = OpenDescriptorCount();

        for (int i = 0; i < 200; i++)
        {
            Assert.Throws<InvalidDataException>(() => CappedFile.ReadOpened(fifo, "pipe", 64, "too large"));
            Assert.Throws<InvalidDataException>(() => CappedFile.ReadOpened(file, "file.json", 5, "too large"));
            CappedFile.ReadOpened(file, "file.json", 64, "too large");
        }

        Assert.True(OpenDescriptorCount() < before + 50, "Descriptors leaked.");
    }

    // AC-8 (086)
    [Fact]
    public void OpenFlags_AreNonBlockingAndCloseOnExec_PerOs()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(0x1000004, CappedFile.OpenFlags());
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(0x80800, CappedFile.OpenFlags());
        }
        else
        {
            Assert.Null(CappedFile.OpenFlags());
        }
    }

    // AC-9 (086): Read still reports a missing file as null.
    [Fact]
    public void Read_MissingFile_IsNull()
    {
        using TempFolder temp = new TempFolder();

        Assert.Null(CappedFile.Read(Path.Combine(temp.Path, "gone.json"), "gone.json", 64));
    }

    private static int OpenDescriptorCount()
    {
        string folder = OperatingSystem.IsMacOS() ? "/dev/fd" : "/proc/self/fd";
        return Directory.EnumerateFileSystemEntries(folder).Count();
    }

    // AC-1 (314): only a disk handle with none of Directory, Device or ReparsePoint counts. Runs on every OS.
    [Theory]
    [InlineData(1u, FileAttributes.Normal, true)]
    [InlineData(1u, FileAttributes.Archive, true)]
    [InlineData(1u, FileAttributes.ReadOnly | FileAttributes.Hidden, true)]
    [InlineData(1u, FileAttributes.Directory, false)]
    [InlineData(1u, FileAttributes.Device, false)]
    [InlineData(1u, FileAttributes.ReparsePoint, false)]
    [InlineData(1u, FileAttributes.Archive | FileAttributes.ReparsePoint, false)]
    [InlineData(2u, FileAttributes.Normal, false)]
    [InlineData(3u, FileAttributes.Normal, false)]
    [InlineData(0u, FileAttributes.Normal, false)]
    [InlineData(0x8000u, FileAttributes.Normal, false)]
    public void IsRegularWindowsHandle_OnlyDiskWithoutRefusedAttributes(uint fileType, FileAttributes attributes, bool expected)
    {
        Assert.Equal(expected, CappedFile.IsRegularWindowsHandle(fileType, attributes));
    }

    // AC-2 (314): on Windows a regular file is read through the checked handle, and the handle is closed after.
    [Fact]
    public void ReadWindows_RegularFile_ReturnsBytes_AndClosesTheHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "2026-11-01.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(Encoding.UTF8.GetBytes("{}"), CappedFile.ReadWindows(path, "the file", 64, "too large"));
        Assert.Equal(Encoding.UTF8.GetBytes("{}"), CappedFile.Read(path, "the file", 64));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // AC-2 (314): a too-large file is refused and its handle is closed too.
    [Fact]
    public void ReadWindows_TooLarge_IsRefused_AndClosesTheHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "projects.json");
        File.WriteAllText(path, new string('x', 65));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CappedFile.ReadWindows(path, "projects.json", 64, "too large"));
        Assert.Equal("too large", ex.Message);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // AC-2 (314): the handle check alone (no FileKind pre-check) refuses the NUL character device without blocking.
    [Theory]
    [InlineData(@"\\.\NUL")]
    [InlineData("NUL")]
    public void ReadWindows_NulDevice_IsRefused_WithoutBlocking(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Exception? thrown = SpecialFiles.RunWithin(Timeout, () => CappedFile.ReadWindows(path, "settings.json", 64, "too large"));

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal("settings.json is not a regular file.", ex.Message);
    }
}
