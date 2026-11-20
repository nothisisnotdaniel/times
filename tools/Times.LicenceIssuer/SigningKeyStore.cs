using System.Text;

namespace Times.LicenceIssuer;

/// <summary>
/// Holds the issuer's P-256 signing keys (PKCS#8) by key id. Failures throw <see cref="TokenStoreException"/>
/// with a fixed message that never holds a key. Tests inject a fake, so no test touches the Keychain.
/// </summary>
internal interface ISigningKeyStore
{
    /// <summary>False where there is no supported store; then nothing else may be called.</summary>
    public bool IsSupported { get; }

    /// <summary>A fixed reason why <see cref="Write"/> refuses in this run, or <c>null</c>.</summary>
    public string? WriteRefusal { get; }

    public bool Exists(string keyId);

    /// <summary>The PKCS#8 private key, or <c>null</c> if there is none. The caller zeroes it.</summary>
    public byte[]? Read(string keyId);

    /// <summary>Stores the PKCS#8 private key under <paramref name="keyId"/>.</summary>
    public void Write(string keyId, byte[] pkcs8);
}

/// <summary>Picks the signing-key store for the running OS: the macOS Keychain, or none.</summary>
internal static class SigningKeyStore
{
    public static ISigningKeyStore ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacKeychainSigningKeyStore(Environment.ProcessPath) : new UnsupportedSigningKeyStore();
}

/// <summary>Anything but macOS: no store.</summary>
internal sealed class UnsupportedSigningKeyStore : ISigningKeyStore
{
    public const string NotAvailable = "Signing keys are kept only in the macOS Keychain.";

    public bool IsSupported => false;

    public string? WriteRefusal => NotAvailable;

    public bool Exists(string keyId) => throw new TokenStoreException(NotAvailable);

    public byte[]? Read(string keyId) => throw new TokenStoreException(NotAvailable);

    public void Write(string keyId, byte[] pkcs8) => throw new TokenStoreException(NotAvailable);
}

/// <summary>
/// A Keychain generic password per key: service <see cref="Service"/>, account = key id, value = base64
/// PKCS#8. The item's access list trusts only the issuer's apphost (never the shared <c>dotnet</c> host).
/// </summary>
internal sealed class MacKeychainSigningKeyStore : ISigningKeyStore
{
    public const string Service = "Suite Pro signing key";

    public const string SharedHostRefused =
        "A signing key is stored only when the issuer runs as its own program, not through dotnet.";

    private const int MaxBytes = 16 * 1024;

    private readonly string? trustedPath;

    public MacKeychainSigningKeyStore(string? processPath)
    {
        trustedPath = MacKeychainTokenStore.TrustedApplicationPath(processPath);
    }

    public bool IsSupported => true;

    public string? WriteRefusal => trustedPath is null ? SharedHostRefused : null;

    public bool Exists(string keyId) => MacKeychainItem.Exists(Service, keyId);

    public byte[]? Read(string keyId)
    {
        byte[]? text = MacKeychainItem.Read(Service, keyId, MaxBytes);
        if (text is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(Encoding.ASCII.GetString(text));
        }
        catch (FormatException)
        {
            throw new TokenStoreException("The stored signing key is not readable.");
        }
        finally
        {
            Array.Clear(text);
        }
    }

    public void Write(string keyId, byte[] pkcs8)
    {
        if (trustedPath is null)
        {
            throw new TokenStoreException(SharedHostRefused);
        }

        MacKeychainItem.Replace(Service, keyId, Encoding.ASCII.GetBytes(Convert.ToBase64String(pkcs8)), trustedPath);
    }
}
