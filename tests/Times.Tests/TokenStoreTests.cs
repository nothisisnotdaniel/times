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

    // Rework: the Keychain item trusts only Times' own executable, never the shared dotnet host.
    [Theory]
    [InlineData("/Users/me/.dotnet/tools/tm")]
    [InlineData("/Users/me/src/Times/src/Times/bin/Debug/net10.0/Times")]
    public void TrustedApplicationPath_IsTimesOwnExecutable(string path)
    {
        Assert.Equal(path, MacKeychainTokenStore.TrustedApplicationPath(path));
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("/opt/homebrew/Cellar/dotnet/10.0.0/libexec/DOTNET")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("dotnet")]
    [InlineData("bin/Times")]
    [InlineData("")]
    [InlineData(null)]
    public void TrustedApplicationPath_RefusesTheSharedHostAndUnclearPaths(string? path)
    {
        Assert.Null(MacKeychainTokenStore.TrustedApplicationPath(path));
    }

    // Refused before the Keychain is touched, with a fixed message that never holds the token.
    [Fact]
    public void MacWrite_ThroughTheSharedHost_StoresNothing_WithAFixedMessage()
    {
        MacKeychainTokenStore store = new MacKeychainTokenStore("/usr/local/share/dotnet/dotnet");

        TokenStoreException ex = Assert.Throws<TokenStoreException>(() => store.Write("contoso", "PAT-MARKER-1"));

        Assert.Equal(MacKeychainTokenStore.SharedHostRefused, ex.Message);
        Assert.DoesNotContain("PAT-MARKER-1", ex.ToString());
    }
}
