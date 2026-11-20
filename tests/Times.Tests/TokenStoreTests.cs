namespace Times.Tests;

/// <summary>Only the selection and the unsupported store: the real Keychain and Credential Manager are never touched.</summary>
public class TokenStoreTests
{
    [Fact]
    public void For_PicksTheStoreForEachOs()
    {
        Assert.IsType<MacKeychainTokenStore>(TokenStore.For(macOS: true, windows: false));
        Assert.IsType<WindowsCredentialTokenStore>(TokenStore.For(macOS: false, windows: true));
        Assert.IsType<UnsupportedTokenStore>(TokenStore.For(macOS: false, windows: false));
    }

    [Fact]
    public void ForCurrentPlatform_MatchesTheOs()
    {
        ITokenStore store = TokenStore.ForCurrentPlatform();

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacKeychainTokenStore>(store);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialTokenStore>(store);
        }
        else
        {
            Assert.IsType<UnsupportedTokenStore>(store);
        }
    }

    [Fact]
    public void RealStores_AreSupported()
    {
        Assert.True(new MacKeychainTokenStore("/Users/me/.dotnet/tools/tm").IsSupported);
        Assert.True(new WindowsCredentialTokenStore().IsSupported);
    }

    // AC-3
    [Fact]
    public void Unsupported_IsNotSupported_AndRefusesEveryCall_WithAFixedMessage()
    {
        UnsupportedTokenStore store = new UnsupportedTokenStore();

        Assert.False(store.IsSupported);
        TokenStoreException read = Assert.Throws<TokenStoreException>(() => store.Read());
        TokenStoreException write = Assert.Throws<TokenStoreException>(() => store.Write("contoso", "PAT-MARKER-1"));
        Assert.Throws<TokenStoreException>(() => store.Delete());
        Assert.Equal(UnsupportedTokenStore.NotAvailable, read.Message);
        Assert.Equal(UnsupportedTokenStore.NotAvailable, write.Message);
    }

    // Rework: the Keychain item trusts only Times' own executable, never a shared .NET host.
    [Fact]
    public void TrustedApplicationPath_IsTimesOwnExecutable()
    {
        using TempTree tree = new TempTree();
        string tm = tree.File("tools/tm");
        string apphost = tree.File("bin/Debug/net10.0/Times");

        Assert.Equal(tm, MacKeychainTokenStore.TrustedApplicationPath(tm));
        Assert.Equal(apphost, MacKeychainTokenStore.TrustedApplicationPath(apphost));
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.0/libexec/DOTNET")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("dotnet")]
    [InlineData("bin/Times")]
    [InlineData("/no/such/folder/tm")]
    [InlineData("")]
    [InlineData(null)]
    public void TrustedApplicationPath_RefusesTheSharedHostAndUnclearPaths(string? path)
    {
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(path));
    }

    // Round 3: a host named dotnet is refused even when it exists, and a renamed one by its host/fxr folder.
    [Fact]
    public void TrustedApplicationPath_RefusesARealOrRenamedDotnetHost()
    {
        using TempTree tree = new TempTree();
        string dotnet = tree.File("root/dotnet");
        string renamed = tree.File("root/dotnet10");
        Directory.CreateDirectory(Path.Combine(tree.Root, "root", "host", "fxr", "10.0.0"));

        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(dotnet));
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(renamed));
        Assert.True(MacKeychainTokenStore.IsSharedHost(renamed));
        Assert.False(MacKeychainTokenStore.IsSharedHost(tree.File("tools/tm")));
    }

    // Round 3: symlinks are resolved, so a link to Times trusts the real file and a link to a host is refused.
    [Fact]
    public void TrustedApplicationPath_ResolvesSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symlinks needs extra rights there; the macOS store is the one that uses this.
        }

        using TempTree tree = new TempTree();
        string real = tree.File("app/Times");
        string toTimes = tree.Link("bin/times-link", real);
        string toLink = tree.Link("bin/times-link-2", toTimes);
        string renamedHost = tree.File("sdk/dotnet10");
        Directory.CreateDirectory(Path.Combine(tree.Root, "sdk", "host", "fxr"));
        string toRenamedHost = tree.Link("bin/tm", renamedHost);
        string toNamedHost = tree.Link("bin/tm2", tree.File("other/dotnet"));
        string dangling = tree.Link("bin/gone", Path.Combine(tree.Root, "missing"));

        Assert.Equal(real, MacKeychainTokenStore.TrustedApplicationPath(toTimes));
        Assert.Equal(real, MacKeychainTokenStore.TrustedApplicationPath(toLink));
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(toRenamedHost));
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(toNamedHost));
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(dangling));
    }

    // Refused before the Keychain is touched, with a fixed message that never holds the token.
    [Fact]
    public void MacWrite_ThroughTheSharedHost_StoresNothing_WithAFixedMessage()
    {
        FakeKeychainWriter writer = new FakeKeychainWriter();
        MacKeychainTokenStore store = new MacKeychainTokenStore("/usr/local/share/dotnet/dotnet", writer);

        TokenStoreException ex = Assert.Throws<TokenStoreException>(() => store.Write("contoso", "PAT-MARKER-1"));

        Assert.Equal(MacKeychainTokenStore.SharedHostRefused, ex.Message);
        Assert.DoesNotContain("PAT-MARKER-1", ex.ToString());
        Assert.Empty(writer.Calls);
    }

    // Round 3: the refusal is known before the token is asked for.
    [Fact]
    public void WriteRefusal_IsSetOnlyWhenWritingWouldBeRefused()
    {
        using TempTree tree = new TempTree();

        Assert.Equal(MacKeychainTokenStore.SharedHostRefused,
            new MacKeychainTokenStore("/usr/local/share/dotnet/dotnet", new FakeKeychainWriter()).WriteRefusal);
        Assert.Null(new MacKeychainTokenStore(tree.File("tools/tm"), new FakeKeychainWriter()).WriteRefusal);
        Assert.Null(new WindowsCredentialTokenStore().WriteRefusal);
        Assert.Equal(UnsupportedTokenStore.NotAvailable, new UnsupportedTokenStore().WriteRefusal);
    }

    // Round 3, job 322: the access object is built first; an existing item gets it, then is updated in place.
    [Fact]
    public void MacWrite_BuildsAccessFirst_ThenReplacesTheItem_AndReleasesIt()
    {
        using TempTree tree = new TempTree();
        string tm = tree.File("tools/tm");
        FakeKeychainWriter writer = new FakeKeychainWriter(101);

        new MacKeychainTokenStore(tm, writer).Write("contoso", "PAT-MARKER-1");

        Assert.Equal(["CreateAccess " + tm, "FindItems", "SetAccess 101", "UpdateItem contoso", "Release 101", "Release 42"], writer.Calls);
        Assert.All(writer.Password!, b => Assert.Equal(0, b));
    }

    // Job 322 AC-1: with no stored item, a new one is added and nothing is deleted.
    [Fact]
    public void MacWrite_NoStoredItem_AddsOne_AndDeletesNothing()
    {
        using TempTree tree = new TempTree();
        string tm = tree.File("tools/tm");
        FakeKeychainWriter writer = new FakeKeychainWriter();

        new MacKeychainTokenStore(tm, writer).Write("contoso", "PAT-MARKER-1");

        Assert.Equal(["CreateAccess " + tm, "FindItems", "AddItem contoso", "Release 42"], writer.Calls);
        Assert.DoesNotContain(writer.Calls, c => c.StartsWith("Delete", StringComparison.Ordinal));
        Assert.All(writer.Password!, b => Assert.Equal(0, b));
    }

    // Job 322 AC-2: with one stored item, it is updated in place: no add and no delete.
    [Fact]
    public void MacWrite_OneStoredItem_UpdatesItInPlace_WithoutAddOrDelete()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101);

        new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("fabrikam", "PAT-MARKER-1");

        Assert.Equal(["FindItems", "SetAccess 101", "UpdateItem fabrikam", "Release 101", "Release 42"], writer.Calls[1..]);
        Assert.Equal(new IntPtr(101), writer.UpdatedItem);
        Assert.DoesNotContain(writer.Calls, c => c.StartsWith("AddItem", StringComparison.Ordinal) || c.StartsWith("Delete", StringComparison.Ordinal));
    }

    // Job 322 AC-3: a failed update keeps the old item, says so with a fixed message and releases everything.
    [Fact]
    public void MacWrite_UpdateFails_DeletesNothing_AndReleasesEverything()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101) { FailUpdate = true };

        TokenStoreException ex = Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal("The token could not be saved in the macOS Keychain.", ex.Message);
        Assert.Equal(["FindItems", "SetAccess 101", "UpdateItem contoso", "Release 101", "Release 42"], writer.Calls[1..]);
        Assert.DoesNotContain(writer.Calls, c => c.StartsWith("Delete", StringComparison.Ordinal));
        writer.AssertEveryReferenceReleasedOnce();
    }

    // Job 322 AC-4: a failed access change keeps the old token under its old list: no update, no delete.
    [Fact]
    public void MacWrite_SetAccessFails_NeverUpdatesOrDeletes_AndReleasesEverything()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101, 102) { FailSetAccess = true };

        TokenStoreException ex = Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal("The token could not be saved in the macOS Keychain.", ex.Message);
        Assert.Equal(["FindItems", "SetAccess 101", "Release 101", "Release 102", "Release 42"], writer.Calls[1..]);
        writer.AssertEveryReferenceReleasedOnce();
    }

    // Job 322 AC-5: duplicates are deleted only after the first item was updated.
    [Fact]
    public void MacWrite_Duplicates_UpdatesTheFirst_ThenDeletesTheOthers()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101, 102, 103);

        new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1");

        Assert.Equal(
            ["FindItems", "SetAccess 101", "UpdateItem contoso", "DeleteItem 102", "DeleteItem 103", "Release 101", "Release 102", "Release 103", "Release 42"],
            writer.Calls[1..]);
        writer.AssertEveryReferenceReleasedOnce();
    }

    // Job 322 AC-5: if the update of the first fails, no duplicate is deleted.
    [Fact]
    public void MacWrite_Duplicates_UpdateFails_DeletesNone()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101, 102) { FailUpdate = true };

        Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal(["FindItems", "SetAccess 101", "UpdateItem contoso", "Release 101", "Release 102", "Release 42"], writer.Calls[1..]);
        writer.AssertEveryReferenceReleasedOnce();
    }

    // Job 322 AC-6: an unreadable keychain changes nothing and says so.
    [Fact]
    public void MacWrite_FindFails_MakesNoFurtherChange()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101) { FailFind = true };

        TokenStoreException ex = Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal("The macOS Keychain could not be read.", ex.Message);
        Assert.Equal(["FindItems", "Release 42"], writer.Calls[1..]);
    }

    // Job 322 AC-7: the token bytes are zeroed and no message holds the PAT, whatever step fails.
    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "add")]
    [InlineData(1, "")]
    [InlineData(1, "update")]
    [InlineData(2, "")]
    [InlineData(2, "update")]
    [InlineData(2, "setaccess")]
    [InlineData(1, "find")]
    public void MacWrite_ZeroesTheTokenBytes_AndNeverShowsThePat(int storedItems, string fail)
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(Enumerable.Range(101, storedItems).Select(i => new IntPtr(i)).ToArray())
        {
            FailAdd = fail == "add",
            FailUpdate = fail == "update",
            FailSetAccess = fail == "setaccess",
            FailFind = fail == "find",
        };
        MacKeychainTokenStore store = new MacKeychainTokenStore(tree.File("tools/tm"), writer);

        Exception? ex = Record.Exception(() => store.Write("contoso", "PAT-MARKER-1"));

        if (fail.Length == 0)
        {
            Assert.Null(ex);
        }
        else
        {
            Assert.IsType<TokenStoreException>(ex);
            Assert.DoesNotContain("PAT-MARKER-1", ex.ToString());
        }

        if (writer.Password is not null)
        {
            Assert.NotEmpty(writer.Password);
            Assert.All(writer.Password, b => Assert.Equal(0, b));
        }
        else
        {
            Assert.True(fail is "find" or "setaccess");
        }

        writer.AssertEveryReferenceReleasedOnce();
    }

    // Round 3: a failed access build keeps the stored token (nothing is deleted or added).
    [Fact]
    public void MacWrite_AccessBuildFails_KeepsTheEarlierItem()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter(101) { FailAccess = true };

        Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal(["CreateAccess " + tree.File("tools/tm")], writer.Calls);
    }

    // A failed add still releases the access object and zeroes the token bytes.
    [Fact]
    public void MacWrite_AddFails_StillReleasesAndZeroes()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter { FailAdd = true };

        Assert.Throws<TokenStoreException>(() => new MacKeychainTokenStore(tree.File("tools/tm"), writer).Write("contoso", "PAT-MARKER-1"));

        Assert.Equal("Release 42", writer.Calls[^1]);
        Assert.All(writer.Password!, b => Assert.Equal(0, b));
    }

    // Records every native step as a string; item handles are preset, the access object is 42.
    private sealed class FakeKeychainWriter : MacKeychainTokenStore.IKeychainWriter
    {
        private const string SaveFailed = "The token could not be saved in the macOS Keychain.";

        private static readonly IntPtr Access = new IntPtr(42);

        private readonly IntPtr[] items;

        private readonly List<IntPtr> handedOut = [];

        public FakeKeychainWriter(params int[] items)
            : this(items.Select(i => new IntPtr(i)).ToArray())
        {
        }

        public FakeKeychainWriter(IntPtr[] items)
        {
            this.items = items;
        }

        public List<string> Calls { get; } = [];

        public bool FailAccess { get; set; }

        public bool FailFind { get; set; }

        public bool FailSetAccess { get; set; }

        public bool FailUpdate { get; set; }

        public bool FailAdd { get; set; }

        public byte[]? Password { get; private set; }

        public IntPtr? UpdatedItem { get; private set; }

        public IntPtr CreateAccess(string path)
        {
            Calls.Add("CreateAccess " + path);
            if (FailAccess)
            {
                throw new TokenStoreException(SaveFailed);
            }

            handedOut.Add(Access);
            return Access;
        }

        public IReadOnlyList<IntPtr> FindItems()
        {
            Calls.Add("FindItems");
            if (FailFind)
            {
                throw new TokenStoreException("The macOS Keychain could not be read.");
            }

            handedOut.AddRange(items);
            return items;
        }

        public void SetAccess(IntPtr item, IntPtr access)
        {
            Assert.Equal(Access, access);
            Calls.Add("SetAccess " + item);
            if (FailSetAccess)
            {
                throw new TokenStoreException(SaveFailed);
            }
        }

        public void UpdateItem(IntPtr item, string organization, byte[] password)
        {
            Calls.Add("UpdateItem " + organization);
            UpdatedItem = item;
            Password = password;
            if (FailUpdate)
            {
                throw new TokenStoreException(SaveFailed);
            }
        }

        public void DeleteItem(IntPtr item)
        {
            Calls.Add("DeleteItem " + item);
        }

        public void AddItem(string organization, byte[] password, IntPtr access)
        {
            Assert.Equal(Access, access);
            Calls.Add("AddItem " + organization);
            Password = password;
            if (FailAdd)
            {
                throw new TokenStoreException(SaveFailed);
            }
        }

        public void Release(IntPtr reference)
        {
            Assert.Contains(reference, handedOut);
            Calls.Add("Release " + reference);
        }

        // Every reference the fake handed out is released exactly once.
        public void AssertEveryReferenceReleasedOnce()
        {
            foreach (IntPtr reference in handedOut)
            {
                Assert.Single(Calls, c => c == "Release " + reference);
            }
        }
    }

    // A throw-away folder; files are created empty, links point where they are told.
    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "times-tokenstore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string File(string relative)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!System.IO.File.Exists(path))
            {
                System.IO.File.WriteAllBytes(path, []);
            }

            return path;
        }

        public string Link(string relative, string target)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.CreateSymbolicLink(path, target);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
