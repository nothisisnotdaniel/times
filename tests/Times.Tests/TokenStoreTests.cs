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

    // Round 3: the access object is built before the old item is removed, then added and released.
    [Fact]
    public void MacWrite_BuildsAccessFirst_ThenReplacesTheItem_AndReleasesIt()
    {
        using TempTree tree = new TempTree();
        string tm = tree.File("tools/tm");
        FakeKeychainWriter writer = new FakeKeychainWriter();

        new MacKeychainTokenStore(tm, writer).Write("contoso", "PAT-MARKER-1");

        Assert.Equal(["CreateAccess " + tm, "DeleteAll", "AddItem contoso", "Release"], writer.Calls);
        Assert.All(writer.Password!, b => Assert.Equal(0, b));
    }

    // Round 3: a failed access build keeps the stored token (nothing is deleted or added).
    [Fact]
    public void MacWrite_AccessBuildFails_KeepsTheEarlierItem()
    {
        using TempTree tree = new TempTree();
        FakeKeychainWriter writer = new FakeKeychainWriter { FailAccess = true };

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

        Assert.Equal("Release", writer.Calls[^1]);
        Assert.All(writer.Password!, b => Assert.Equal(0, b));
    }

    private sealed class FakeKeychainWriter : MacKeychainTokenStore.IKeychainWriter
    {
        public List<string> Calls { get; } = [];

        public bool FailAccess { get; set; }

        public bool FailAdd { get; set; }

        public byte[]? Password { get; private set; }

        public IntPtr CreateAccess(string path)
        {
            Calls.Add("CreateAccess " + path);
            if (FailAccess)
            {
                throw new TokenStoreException("The token could not be saved in the macOS Keychain.");
            }

            return new IntPtr(42);
        }

        public bool DeleteAll()
        {
            Calls.Add("DeleteAll");
            return true;
        }

        public void AddItem(string organization, byte[] password, IntPtr access)
        {
            Calls.Add("AddItem " + organization);
            Password = password;
            if (FailAdd)
            {
                throw new TokenStoreException("The token could not be saved in the macOS Keychain.");
            }
        }

        public void Release(IntPtr reference)
        {
            Assert.Equal(new IntPtr(42), reference);
            Calls.Add("Release");
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
