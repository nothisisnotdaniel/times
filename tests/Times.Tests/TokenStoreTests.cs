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
        Assert.True(new MacKeychainTokenStore().IsSupported);
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
}
