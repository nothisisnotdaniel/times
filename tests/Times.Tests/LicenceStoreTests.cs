namespace Times.Tests;

/// <summary>The store selection and refusal rules; no test touches a real Keychain or Credential Manager.</summary>
public sealed class LicenceStoreTests
{
    [Fact]
    public void For_PicksTheStoreOfTheOs()
    {
        Assert.IsType<MacKeychainLicenceStore>(LicenceStore.For(macOS: true, windows: false));
        Assert.IsType<WindowsCredentialLicenceStore>(LicenceStore.For(macOS: false, windows: true));
        Assert.IsType<UnsupportedLicenceStore>(LicenceStore.For(macOS: false, windows: false));
    }

    [Fact]
    public void SharedNames_AreFixed()
    {
        Assert.Equal("Suite Pro licence", LicenceStore.MacService);
        Assert.Equal("key", LicenceStore.MacAccount);
        Assert.Equal("Suite/ProLicence", LicenceStore.WindowsTarget);
    }

    [Fact]
    public void Unsupported_RefusesEverything()
    {
        UnsupportedLicenceStore store = new UnsupportedLicenceStore();

        Assert.False(store.IsSupported);
        Assert.NotNull(store.WriteRefusal);
        Assert.Throws<TokenStoreException>(() => store.Read());
        Assert.Throws<TokenStoreException>(() => store.Write("CTX1-x.y"));
        Assert.Throws<TokenStoreException>(() => store.Delete());
    }

    // The access list rule of the Azure DevOps token: no apphost to trust (or the shared host) refuses before the Keychain.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/tm")]
    public void Mac_WithoutAnApphost_RefusesBeforeTheKeychain(string? processPath)
    {
        MacKeychainLicenceStore store = new MacKeychainLicenceStore(processPath);

        Assert.Equal(MacKeychainLicenceStore.SharedHostRefused, store.WriteRefusal);
        TokenStoreException refused = Assert.Throws<TokenStoreException>(() => store.Write("CTX1-x.y"));
        Assert.Equal(MacKeychainLicenceStore.SharedHostRefused, refused.Message);
    }

    [Fact]
    public void Mac_ThroughTheDotnetHost_Refuses()
    {
        using TempFolder folder = new TempFolder();
        string dotnet = Path.Combine(folder.Path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnet, string.Empty);

        Assert.Equal(MacKeychainLicenceStore.SharedHostRefused, new MacKeychainLicenceStore(dotnet).WriteRefusal);
    }

    [Fact]
    public void Mac_WithAnApphost_MayWrite()
    {
        using TempFolder folder = new TempFolder();
        string tm = Path.Combine(folder.Path, "tm");
        File.WriteAllText(tm, string.Empty);

        Assert.Null(new MacKeychainLicenceStore(tm).WriteRefusal);
    }
}
