using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Times.Tests;

public sealed class DataBackupTests : IDisposable
{
    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private string DataFile(string name) => Path.Combine(data.Path, name);

    private static List<(string Name, byte[] Bytes)> Entries(byte[] zipBytes)
    {
        using ZipArchive zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        List<(string, byte[])> entries = [];
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream stream = entry.Open();
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            entries.Add((entry.FullName, copy.ToArray()));
        }

        return entries;
    }

    private (BackupCounts Counts, List<(string Name, byte[] Bytes)> Entries) Backup()
    {
        using MemoryStream target = new MemoryStream();
        BackupCounts counts = DataBackup.Write(new DayStore(data.Path), target);
        return (counts, Entries(target.ToArray()));
    }

    // AC-1
    [Fact]
    public void Write_HoldsEveryDayFileAndProjects_ByteForByte_InOrdinalOrder()
    {
        byte[] first = Encoding.UTF8.GetBytes("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"}]}");
        byte[] second = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}', 0x0D, 0x0A];
        byte[] projects = Encoding.UTF8.GetBytes("{\"projects\":[{\"workItem\":1,\"title\":\"Ä\"}]}");
        File.WriteAllBytes(DataFile("2026-11-02.json"), second);
        File.WriteAllBytes(DataFile("2025-01-31.json"), first);
        File.WriteAllBytes(DataFile("projects.json"), projects);
        File.WriteAllText(DataFile("settings.json"), "{}");
        File.WriteAllText(DataFile("update-pending.json"), "{}");
        File.WriteAllText(DataFile("notes.txt"), "x");
        File.WriteAllText(DataFile("2026-1-1.json"), "{}");
        Directory.CreateDirectory(DataFile("sub"));
        File.WriteAllText(Path.Combine(DataFile("sub"), "2026-11-03.json"), "{}");

        (BackupCounts counts, List<(string Name, byte[] Bytes)> entries) = Backup();

        Assert.Equal(["2025-01-31.json", "2026-11-02.json", "projects.json"], entries.Select(e => e.Name));
        Assert.Equal(first, entries[0].Bytes);
        Assert.Equal(second, entries[1].Bytes);
        Assert.Equal(projects, entries[2].Bytes);
        Assert.Equal(2, counts.DayFiles);
        Assert.True(counts.Projects);
        Assert.Equal(0, counts.Skipped);
        Assert.Equal(3, counts.Written);
    }

    // AC-1
    [Fact]
    public void Write_WithoutProjects_HoldsOnlyTheDayFiles()
    {
        File.WriteAllText(DataFile("2026-11-02.json"), "{}");

        (BackupCounts counts, List<(string Name, byte[] Bytes)> entries) = Backup();

        Assert.Equal(["2026-11-02.json"], entries.Select(e => e.Name));
        Assert.False(counts.Projects);
        Assert.Equal(1, counts.Written);
    }

    // AC-7
    [Fact]
    public void Write_MissingDataFolder_WritesNoEntries()
    {
        using TempFolder missing = new TempFolder(create: false);
        using MemoryStream target = new MemoryStream();

        BackupCounts counts = DataBackup.Write(new DayStore(missing.Path), target);

        Assert.Equal(0, counts.Written);
        Assert.False(Directory.Exists(missing.Path));
    }

    // AC-2
    [Fact]
    public void Write_TooLargeFile_IsSkippedAndCounted_BrokenJsonIsCopied()
    {
        File.WriteAllBytes(DataFile("2026-11-01.json"), new byte[DayStore.MaxFileBytes + 1]);
        File.WriteAllBytes(DataFile("projects.json"), new byte[DayStore.MaxFileBytes + 1]);
        File.WriteAllText(DataFile("2026-11-02.json"), "{not json");
        byte[] exact = new byte[DayStore.MaxFileBytes];
        exact[0] = 42;
        File.WriteAllBytes(DataFile("2026-11-03.json"), exact);

        (BackupCounts counts, List<(string Name, byte[] Bytes)> entries) = Backup();

        Assert.Equal(["2026-11-02.json", "2026-11-03.json"], entries.Select(e => e.Name));
        Assert.Equal(Encoding.UTF8.GetBytes("{not json"), entries[0].Bytes);
        Assert.Equal(exact, entries[1].Bytes);
        Assert.Equal(2, counts.Skipped);
        Assert.False(counts.Projects);
    }

    // AC-2
    [Fact]
    public void Write_FifoAndFolderWithADayName_AreSkippedAndCounted_WithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(DataFile("2026-11-01.json"));
        Directory.CreateDirectory(DataFile("2026-11-02.json"));
        File.WriteAllText(DataFile("2026-11-03.json"), "{}");
        BackupCounts? counts = null;
        byte[] zipBytes = [];

        Exception? thrown = SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () =>
        {
            using MemoryStream target = new MemoryStream();
            counts = DataBackup.Write(new DayStore(data.Path), target);
            zipBytes = target.ToArray();
        });

        Assert.Null(thrown);
        Assert.Equal(2, counts!.Skipped);
        Assert.Equal(["2026-11-03.json"], Entries(zipBytes).Select(e => e.Name));
        Assert.True(SpecialFiles.IsFifo(DataFile("2026-11-01.json")));
    }

    // AC-2
    [Fact]
    public void Write_SymlinkToARegularFile_IsCopied_SymlinkToAFifo_IsSkipped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder elsewhere = new TempFolder();
        string real = Path.Combine(elsewhere.Path, "real.json");
        File.WriteAllText(real, "{\"x\":1}");
        string fifo = Path.Combine(elsewhere.Path, "fifo");
        SpecialFiles.MakeFifo(fifo);
        File.CreateSymbolicLink(DataFile("2026-11-01.json"), real);
        File.CreateSymbolicLink(DataFile("2026-11-02.json"), fifo);

        BackupCounts? counts = null;
        byte[] zipBytes = [];
        Exception? thrown = SpecialFiles.RunWithin(TimeSpan.FromSeconds(10), () =>
        {
            using MemoryStream target = new MemoryStream();
            counts = DataBackup.Write(new DayStore(data.Path), target);
            zipBytes = target.ToArray();
        });

        Assert.Null(thrown);
        List<(string Name, byte[] Bytes)> entries = Entries(zipBytes);
        Assert.Equal(["2026-11-01.json"], entries.Select(e => e.Name));
        Assert.Equal(Encoding.UTF8.GetBytes("{\"x\":1}"), entries[0].Bytes);
        Assert.Equal(1, counts!.Skipped);
    }

    // AC-8
    [Fact]
    public void Write_ChangesNothingInTheDataFolder()
    {
        File.WriteAllText(DataFile("2026-11-01.json"), "{}");
        File.WriteAllText(DataFile("projects.json"), "{\"projects\":[]}");
        DateTime old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(DataFile("2026-11-01.json"), old);

        Backup();

        Assert.Equal(["2026-11-01.json", "projects.json"],
            Directory.EnumerateFileSystemEntries(data.Path).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal));
        Assert.Equal(old, File.GetLastWriteTimeUtc(DataFile("2026-11-01.json")));
    }

    // AC-5
    [Theory]
    [InlineData(2026, 10, 27, 18, 30, "times-backup-2026-10-27-1830.zip")]
    [InlineData(2026, 1, 2, 3, 4, "times-backup-2026-01-02-0304.zip")]
    [InlineData(2026, 12, 31, 23, 59, "times-backup-2026-12-31-2359.zip")]
    public void FileName_IsInvariantAndZeroPadded(int year, int month, int day, int hour, int minute, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // A culture with its own calendar and digits must not change the name.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(expected, DataBackup.FileName(new DateTime(year, month, day, hour, minute, 45)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // AC-4
    [Fact]
    public void IsInside_SameChildGrandchildTrailingSeparator_AreInside()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        Directory.CreateDirectory(Path.Combine(data.Path, "a", "b"));

        Assert.True(DataBackup.IsInside(data.Path, data.Path));
        Assert.True(DataBackup.IsInside(data.Path + sep, data.Path));
        Assert.True(DataBackup.IsInside(data.Path, data.Path + sep));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "a"), data.Path));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "a", "b") + sep, data.Path));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "missing", "deeper"), data.Path));
    }

    // AC-4
    [Fact]
    public void IsInside_IgnoresCase()
    {
        Directory.CreateDirectory(Path.Combine(data.Path, "Sub"));

        Assert.True(DataBackup.IsInside(data.Path.ToUpperInvariant(), data.Path));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "SUB"), data.Path.ToLowerInvariant()));
    }

    // AC-4
    [Fact]
    public void IsInside_SiblingWithSharedPrefix_AndParent_AreOutside()
    {
        string sibling = data.Path + "-old";
        string sibling2 = data.Path + "2";
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(sibling2);
        try
        {
            Assert.False(DataBackup.IsInside(sibling, data.Path));
            Assert.False(DataBackup.IsInside(sibling2, data.Path));
            Assert.False(DataBackup.IsInside(Path.GetDirectoryName(data.Path)!, data.Path));
        }
        finally
        {
            Directory.Delete(sibling);
            Directory.Delete(sibling2);
        }
    }

    // AC-4
    [Fact]
    public void IsInside_DotDotSegments_AreWorkedOut()
    {
        Directory.CreateDirectory(Path.Combine(data.Path, "a"));
        string name = Path.GetFileName(data.Path);

        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "a", ".."), data.Path));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "..", name, "a"), data.Path));
        Assert.False(DataBackup.IsInside(Path.Combine(data.Path, ".."), data.Path));
    }

    // AC-4
    [Fact]
    public void IsInside_SymlinkOutsidePointingIn_IsInside_AndDataFolderBehindALink_IsCompared()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder outside = new TempFolder();
        Directory.CreateDirectory(Path.Combine(data.Path, "sub"));
        string toData = Path.Combine(outside.Path, "to-data");
        string toSub = Path.Combine(outside.Path, "to-sub");
        string relative = Path.Combine(outside.Path, "relative");
        Directory.CreateSymbolicLink(toData, data.Path);
        Directory.CreateSymbolicLink(toSub, Path.Combine(data.Path, "sub"));
        Directory.CreateSymbolicLink(relative, Path.Combine("..", Path.GetFileName(data.Path)));

        Assert.True(DataBackup.IsInside(toData, data.Path));
        Assert.True(DataBackup.IsInside(toSub, data.Path));
        Assert.True(DataBackup.IsInside(relative, data.Path));
        Assert.True(DataBackup.IsInside(Path.Combine(toSub, "..", "sub"), data.Path));
        // The data folder named through a link is still the data folder.
        Assert.True(DataBackup.IsInside(data.Path, toData));
        Assert.True(DataBackup.IsInside(Path.Combine(data.Path, "sub"), toData + "/"));
        Assert.False(DataBackup.IsInside(outside.Path, toData));
    }

    // AC-4
    [Fact]
    public void IsInside_DotDotAfterALink_GoesUpFromTheLinkTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder outside = new TempFolder();
        Directory.CreateDirectory(Path.Combine(data.Path, "sub", "deeper"));
        string link = Path.Combine(outside.Path, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(data.Path, "sub", "deeper"));

        // <outside>/link/.. is <data>/sub for the OS, not <outside>.
        Assert.True(DataBackup.IsInside(Path.Combine(link, ".."), data.Path));
    }

    // AC-4
    [Fact]
    public void IsInside_LinkLoop_DoesNotHang()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder outside = new TempFolder();
        string a = Path.Combine(outside.Path, "a");
        string b = Path.Combine(outside.Path, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        Assert.False(DataBackup.IsInside(a, data.Path));
    }
}
