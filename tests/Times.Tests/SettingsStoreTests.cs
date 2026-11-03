using System.Text;

namespace Times.Tests;

public sealed class SettingsStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>One setting of each kind.</summary>
    internal static SettingsRegistry Registry()
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterOnOff("round", "Round times", false);
        registry.RegisterNumber("target", "Daily target", 480, -10, 720);
        registry.RegisterChoice("theme", "Theme", "dark", ["dark", "light", "[red]x[/]"]);
        return registry;
    }

    private static void AssertDefaults(SettingValues values)
    {
        Assert.False(values.IsOn("round"));
        Assert.Equal(480, values.Number("target"));
        Assert.Equal("dark", values.Choice("theme"));
    }

    private static void AssertRefused(SettingsStore store, string message)
    {
        Exception? thrown = SpecialFiles.RunWithin(Timeout, () => store.Load(Registry()));

        InvalidDataException ex = Assert.IsType<InvalidDataException>(thrown);
        Assert.Equal(message, ex.Message);
    }

    // Scope
    [Fact]
    public void FilePath_IsSettingsJson_InTheFolder()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.Equal(Path.Combine(temp.Path, "settings.json"), new SettingsStore(temp.Path).FilePath);
    }

    // AC-9
    [Fact]
    public void Load_WhenFolderIsMissing_GivesDefaults_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder(create: false);

        AssertDefaults(new SettingsStore(temp.Path).Load(Registry()));

        Assert.False(Directory.Exists(temp.Path));
    }

    // AC-9
    [Fact]
    public void Load_WhenFileIsMissing_GivesDefaults_AndCreatesNothing()
    {
        using TempFolder temp = new TempFolder();

        AssertDefaults(new SettingsStore(temp.Path).Load(Registry()));

        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    // AC-6, AC-7, AC-9
    [Fact]
    public void SaveThenLoad_RoundTrips_UsingUtf8WithoutBom_AndLeavesNoTempFile()
    {
        using TempFolder temp = new TempFolder(create: false);
        SettingsStore store = new SettingsStore(temp.Path);
        SettingValues values = SettingValues.Defaults(Registry()).With("round", true).With("target", -10).With("theme", "[red]x[/]");

        store.Save(SettingValues.Defaults(Registry()).With("round", false));
        store.Save(values);

        SettingValues loaded = store.Load(Registry());
        Assert.True(loaded.IsOn("round"));
        Assert.Equal(-10, loaded.Number("target"));
        Assert.Equal("[red]x[/]", loaded.Choice("theme"));
        Assert.Equal([store.FilePath], Directory.EnumerateFileSystemEntries(temp.Path));
        byte[] bytes = File.ReadAllBytes(store.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("{\"settings\":{\"round\":true,\"target\":-10,\"theme\":\"[red]x[/]\"}}", Encoding.UTF8.GetString(bytes));
    }

    // AC-9
    [Fact]
    public void Save_WritesOnlyNamesThatWereReadOrSet()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);

        store.Save(SettingValues.Defaults(Registry()).With("theme", "light"));

        Assert.Equal("{\"settings\":{\"theme\":\"light\"}}", File.ReadAllText(store.FilePath));
    }

    // AC-9
    [Fact]
    public void Load_NamesMissingFromTheFile_HaveTheirDefaults()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, "{\"settings\":{\"target\":0}}");

        SettingValues values = store.Load(Registry());

        Assert.Equal(0, values.Number("target"));
        Assert.False(values.IsOn("round"));
        Assert.Equal("dark", values.Choice("theme"));
    }

    // AC-11
    [Fact]
    public void UnknownNames_AreIgnoredOnLoad_AndKeptUnchangedOnSave_InFileOrder()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath,
            "{\"settings\":{\"later\":[1, {\"a\":\"b\"},null],\"round\":false,\"Odd Name\":\"\\u00e9\",\"theme\":\"light\"}}");

        SettingValues values = store.Load(Registry());
        Assert.False(values.IsOn("round"));
        store.Save(values.With("round", true).With("target", 5));

        Assert.Equal(
            "{\"settings\":{\"later\":[1, {\"a\":\"b\"},null],\"round\":true,\"Odd Name\":\"\\u00e9\",\"theme\":\"light\",\"target\":5}}",
            File.ReadAllText(store.FilePath));
        Assert.Equal("light", store.Load(Registry()).Choice("theme"));
    }

    // AC-10
    [Theory]
    [InlineData("{not json", "settings.json is not valid JSON.")]
    [InlineData("", "settings.json is not valid JSON.")]
    [InlineData("{\"settings\":{}} x", "settings.json is not valid JSON.")]
    [InlineData("[]", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("null", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{}", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{\"settings\":[]}", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{\"settings\":null}", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{\"settings\":{},\"other\":1}", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{\"Settings\":{}}", "settings.json does not hold {\"settings\":{…}}.")]
    [InlineData("{\"settings\":{},\"settings\":{}}", "settings.json has a property more than once.")]
    [InlineData("{\"settings\":{\"round\":true,\"round\":false}}", "settings.json has a property more than once.")]
    [InlineData("{\"settings\":{\"x\":{\"a\":1,\"a\":2}}}", "settings.json has a property more than once.")]
    [InlineData("{\"settings\":{\"x\":[{\"a\":1,\"a\":2}]}}", "settings.json has a property more than once.")]
    [InlineData("{\"settings\":{\"round\":1}}", "settings.json has a wrong value for \"round\".")]
    [InlineData("{\"settings\":{\"round\":\"true\"}}", "settings.json has a wrong value for \"round\".")]
    [InlineData("{\"settings\":{\"round\":null}}", "settings.json has a wrong value for \"round\".")]
    [InlineData("{\"settings\":{\"target\":\"5\"}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":1.5}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":5.0}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":721}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":-11}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":99999999999}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"target\":true}}", "settings.json has a wrong value for \"target\".")]
    [InlineData("{\"settings\":{\"theme\":\"Dark\"}}", "settings.json has a wrong value for \"theme\".")]
    [InlineData("{\"settings\":{\"theme\":\"blue\"}}", "settings.json has a wrong value for \"theme\".")]
    [InlineData("{\"settings\":{\"theme\":1}}", "settings.json has a wrong value for \"theme\".")]
    [InlineData("{\"settings\":{\"theme\":\"\\ud800\"}}", "settings.json has a wrong value for \"theme\".")]
    [InlineData("{\"settings\":{\"\\ud800\":1}}", "settings.json has a property name that cannot be read.")]
    [InlineData("{\"settings\":{\"x\":[{\"\\udc00a\":1}]}}", "settings.json has a property name that cannot be read.")]
    public void Load_InvalidFile_ThrowsInvalidData_AndLeavesFileUnchanged(string contents, string message)
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, contents);
        byte[] before = File.ReadAllBytes(store.FilePath);

        AssertRefused(store, message);

        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    // AC-10
    [Fact]
    public void Load_TooLargeFile_ThrowsInvalidData()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, new string(' ', (int)SettingsStore.MaxFileBytes + 1) + "{\"settings\":{}}");

        AssertRefused(store, "settings.json is too large.");
    }

    // AC-10
    [Fact]
    public void Load_FileExactlyAtLimit_IsRead()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        string json = "{\"settings\":{\"round\":true}}";
        File.WriteAllText(store.FilePath, new string(' ', (int)SettingsStore.MaxFileBytes - json.Length) + json);

        Assert.True(store.Load(Registry()).IsOn("round"));
    }

    // AC-10
    [Fact]
    public void Load_FileWithUtf8Bom_Loads()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, "{\"settings\":{\"theme\":\"light\"}}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("light", store.Load(Registry()).Choice("theme"));
    }

    // AC-10
    [Fact]
    public void Load_Fifo_IsRefused_WithoutBlocking_AndStaysAFifo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        SpecialFiles.MakeFifo(store.FilePath);

        AssertRefused(store, "settings.json is not a regular file.");

        Assert.True(SpecialFiles.IsFifo(store.FilePath));
    }

    // AC-10
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
        SettingsStore store = new SettingsStore(temp.Path);
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

        AssertRefused(store, "settings.json is not a regular file.");

        Assert.Equal(targetPath, new FileInfo(store.FilePath).LinkTarget);
    }

    // AC-10
    [Fact]
    public void Load_Directory_IsRefused()
    {
        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        Directory.CreateDirectory(store.FilePath);

        AssertRefused(store, "settings.json is not a regular file.");
    }

    // AC-10
    [Fact]
    public void Load_LinkToValidFile_LoadsIt_AndLinkToOversizedFile_IsTooLarge()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        string target = Path.Combine(temp.Path, "real.json");
        File.WriteAllText(target, "{\"settings\":{\"round\":true}}");
        File.CreateSymbolicLink(store.FilePath, target);

        Assert.True(store.Load(Registry()).IsOn("round"));

        File.WriteAllText(target, new string(' ', (int)SettingsStore.MaxFileBytes) + "{}");
        byte[] before = File.ReadAllBytes(target);

        AssertRefused(store, "settings.json is too large.");

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    // Scope
    [Theory]
    [InlineData("")]
    [InlineData("Times")]
    [InlineData("./data")]
    public void InvalidFolder_LoadAndSaveThrow_AndWriteNothing(string folder)
    {
        string[] before = Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray();
        SettingsStore store = new SettingsStore(folder);

        Assert.Throws<InvalidOperationException>(() => store.Load(Registry()));
        Assert.Throws<InvalidOperationException>(() => store.Save(SettingValues.Defaults(Registry()).With("round", true)));

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(Directory.GetCurrentDirectory()).Order(StringComparer.Ordinal).ToArray());
    }

    // AC-11
    [Fact]
    public void Save_IntoReadOnlyFolder_Throws_AndLeavesTheFileAsItWas()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        SettingsStore store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, "{\"settings\":{\"round\":false}}");
        byte[] before = File.ReadAllBytes(store.FilePath);
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => store.Save(store.Load(Registry()).With("round", true)));
        }
        finally
        {
            File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Equal([store.FilePath], Directory.EnumerateFileSystemEntries(temp.Path));
    }

    // Scope
    [Fact]
    public void Getters_RejectUnknownNamesAndWrongKinds_AndWithRejectsValuesTheSettingCannotHold()
    {
        SettingValues values = SettingValues.Defaults(Registry());

        Assert.Throws<ArgumentException>(() => values.IsOn("missing"));
        Assert.Throws<ArgumentException>(() => values.IsOn("target"));
        Assert.Throws<ArgumentException>(() => values.Number("theme"));
        Assert.Throws<ArgumentException>(() => values.Choice("round"));
        Assert.Throws<ArgumentException>(() => values.With("missing", true));
        Assert.Throws<ArgumentException>(() => values.With("round", 1));
        Assert.Throws<ArgumentException>(() => values.With("target", 721));
        Assert.Throws<ArgumentException>(() => values.With("target", "5"));
        Assert.Throws<ArgumentException>(() => values.With("theme", "blue"));
        Assert.Equal("{\"settings\":{}}", values.ToJson());
    }
}
