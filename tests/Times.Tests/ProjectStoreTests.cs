namespace Times.Tests;

public class ProjectStoreTests
{
    // AC-6
    [Fact]
    public void FilePath_IsProjectsJson_InTheFolder()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.Equal(Path.Combine(temp.Path, "projects.json"), new ProjectStore(temp.Path).FilePath);
    }

    // AC-3
    [Fact]
    public void Load_WhenFolderIsMissing_GivesEmptyList_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.Equal(ProjectList.Empty, new ProjectStore(temp.Path).Load());
        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-3
    [Fact]
    public void Load_WhenFileIsMissing_GivesEmptyList_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder();

        Assert.Equal(ProjectList.Empty, new ProjectStore(temp.Path).Load());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    // AC-1, AC-2, AC-6
    [Fact]
    public void SaveThenLoad_RoundTrips_UsingUtf8WithoutBom_AndLeavesNoTempFile()
    {
        using TempFolder temp = new TempFolder(create: false);
        ProjectStore store = new ProjectStore(temp.Path);
        ProjectList list = ProjectList.Empty.Add(new Project(1234, "Wébsite relaunch")).Add(new Project(5, "[red]Fix[/]"));

        store.Save(ProjectList.Empty.Add(new Project(1, "first")));
        store.Save(list);

        Assert.Equal(list, new ProjectStore(temp.Path).Load());
        Assert.Equal([store.FilePath], Directory.EnumerateFileSystemEntries(temp.Path));
        byte[] bytes = File.ReadAllBytes(store.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(list.ToJson(), System.Text.Encoding.UTF8.GetString(bytes));
    }

    // AC-7
    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"projects\":[{\"workItem\":0,\"title\":\"A\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u202Edef\"}]}")] // AC-7 (056)
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\u200Bdef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\u202edef\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\ud800def\"}]}")]
    [InlineData("{\"projects\":[{\"workItem\":1,\"title\":\"abc\\udc00def\"}]}")]
    public void Load_InvalidFile_ThrowsInvalidData_AndLeavesFileUnchanged(string contents)
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        File.WriteAllText(store.FilePath, contents);
        byte[] before = File.ReadAllBytes(store.FilePath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.Load());

        Assert.Equal("projects.json is not valid.", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    // AC-7
    [Fact]
    public void Load_TooLargeFile_ThrowsInvalidData()
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        File.WriteAllText(store.FilePath, new string(' ', (int)ProjectStore.MaxFileBytes + 1) + "{\"projects\":[]}");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => store.Load());

        Assert.Equal("projects.json is too large.", ex.Message);
    }

    // AC-7
    [Fact]
    public void Load_FileExactlyAtLimit_IsRead()
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        string json = "{\"projects\":[]}";
        File.WriteAllText(store.FilePath, new string(' ', (int)ProjectStore.MaxFileBytes - json.Length) + json);

        Assert.Equal(ProjectList.Empty, store.Load());
    }

    // AC-6
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Times")]
    [InlineData("./data")]
    public void InvalidFolder_LoadAndSaveThrow_AndWriteNothing(string folder)
    {
        string[] before = Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray();
        ProjectStore store = new ProjectStore(folder);

        Assert.Throws<InvalidOperationException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save(ProjectList.Empty.Add(new Project(1, "A"))));

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray());
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string NotRegular = "projects.json is not a regular file.";

    private static void AssertRefused(ProjectStore store, string message)
    {
        Exception? thrown = SpecialFiles.RunWithin(Timeout, () => store.Load());

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal(message, ex.Message);
    }

    // AC-1 (058)
    [Fact]
    public void Load_Fifo_IsRefused_WithoutBlocking_AndStaysAFifo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        SpecialFiles.MakeFifo(store.FilePath);

        AssertRefused(store, NotRegular);

        Assert.True(SpecialFiles.IsFifo(store.FilePath));
    }

    // AC-2, AC-8 (058)
    [Theory]
    [InlineData("fifo")]
    [InlineData("/dev/null")]
    [InlineData("/dev/zero")]
    [InlineData("dir")]
    [InlineData("gone")]
    public void Load_LinkToNonRegularTarget_IsRefused_AndLinkIsUnchanged(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        string targetPath = target.StartsWith('/') ? target : Path.Combine(temp.Path, target);
        if (target == "fifo")
        {
            SpecialFiles.MakeFifo(targetPath);
        }
        else if (target == "dir")
        {
            Directory.CreateDirectory(targetPath);
        }

        File.CreateSymbolicLink(store.FilePath, targetPath);

        AssertRefused(store, NotRegular);

        Assert.Equal(targetPath, new FileInfo(store.FilePath).LinkTarget);
    }

    // AC-3 (058)
    [Fact]
    public void Load_Directory_IsRefused()
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        Directory.CreateDirectory(store.FilePath);

        AssertRefused(store, NotRegular);

        Assert.True(Directory.Exists(store.FilePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.FilePath));
    }

    // AC-4 (058)
    [Fact]
    public void Load_LinkToValidFile_LoadsThatList()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        string target = Path.Combine(temp.Path, "real.json");
        ProjectList list = ProjectList.Empty.Add(new Project(1234, "Website"));
        File.WriteAllText(target, list.ToJson());
        File.CreateSymbolicLink(store.FilePath, target);

        Assert.Equal(list, store.Load());
    }

    // AC-4, AC-5 (058)
    [Fact]
    public void Load_LinkToOversizedFile_IsTooLarge_AndLeavesBothUnchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        string target = Path.Combine(temp.Path, "big.json");
        File.WriteAllText(target, new string(' ', (int)ProjectStore.MaxFileBytes - 14) + "{\"projects\":[]}");
        Assert.Equal(ProjectStore.MaxFileBytes + 1, new FileInfo(target).Length);
        byte[] before = File.ReadAllBytes(target);
        File.CreateSymbolicLink(store.FilePath, target);

        AssertRefused(store, "projects.json is too large.");

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Equal(target, new FileInfo(store.FilePath).LinkTarget);
    }

    // AC-5 (058)
    [Fact]
    public void Load_EmptyFile_IsNotValid()
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        File.WriteAllBytes(store.FilePath, []);

        AssertRefused(store, "projects.json is not valid.");

        Assert.Empty(File.ReadAllBytes(store.FilePath));
    }

    // AC-5 (058)
    [Fact]
    public void Load_FileWithUtf8Bom_Loads()
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        ProjectList list = ProjectList.Empty.Add(new Project(7, "Wébsite"));
        File.WriteAllText(store.FilePath, list.ToJson(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(store.FilePath)[0]);

        Assert.Equal(list, store.Load());
    }

    // AC-8 (058)
    [Fact]
    public void ReadAtMost_CutsALongerStreamAtTheLimit()
    {
        using MemoryStream stream = new MemoryStream(new byte[100_000]);

        byte[] bytes = ProjectStore.ReadAtMost(stream, ProjectStore.MaxFileBytes + 1);

        Assert.Equal(ProjectStore.MaxFileBytes + 1, bytes.Length);
        Assert.Equal(ProjectStore.MaxFileBytes + 1, stream.Position);
    }

    // AC-8 (058)
    [Fact]
    public void ReadAtMost_ReadsAShorterStreamToItsEnd()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("{\"projects\":[]}");
        using MemoryStream stream = new MemoryStream(data);

        Assert.Equal(data, ProjectStore.ReadAtMost(stream, ProjectStore.MaxFileBytes + 1));
    }

    // AC-2 (086)
    [Theory]
    [MemberData(nameof(ProjectListTests.LoneSurrogateProjectFiles), MemberType = typeof(ProjectListTests))]
    public void Load_LoneSurrogateInPropertyName_ThrowsInvalidData(string contents)
    {
        using TempFolder temp = new TempFolder();
        ProjectStore store = new ProjectStore(temp.Path);
        File.WriteAllText(store.FilePath, contents);

        Exception? thrown = Record.Exception(() => store.Load());

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal("projects.json is not valid.", ex.Message);
    }
}
