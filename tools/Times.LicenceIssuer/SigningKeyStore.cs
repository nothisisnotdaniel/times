using System.Buffers;
using System.Buffers.Text;
using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Times.LicenceIssuer;

/// <summary>
/// Holds the issuer's P-256 signing keys (PKCS#8) by key id. Failures throw <see cref="TokenStoreException"/>
/// with a fixed message that never holds a key. Tests inject a fake, so no test touches the Keychain.
/// </summary>
internal interface ISigningKeyStore
{
    /// <summary>False where there is no supported store; then nothing else may be called.</summary>
    public bool IsSupported { get; }

    /// <summary>A fixed reason why creation and migration refuse in this run, or <c>null</c>.</summary>
    public string? WriteRefusal { get; }

    public bool Exists(string keyId);

    /// <summary>The PKCS#8 private key, or <c>null</c> if there is none. The caller zeroes it.</summary>
    public byte[]? Read(string keyId);

    /// <summary>Adds a new key. False if the ID is already in use. The caller owns the PKCS#8 buffer.</summary>
    public bool TryCreate(string keyId, byte[] pkcs8);
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

    public bool TryCreate(string keyId, byte[] pkcs8) => throw new TokenStoreException(NotAvailable);
}

/// <summary>
/// A Keychain generic password per key: service <see cref="Service"/>, account = key id, value = PKCS#8 DER.
/// New items trust only the issuer's apphost. A writable read migrates legacy base64 on the same item.
/// </summary>
internal sealed class MacKeychainSigningKeyStore : ISigningKeyStore
{
    public const string Service = "Suite Pro signing key";

    public const string SharedHostRefused =
        "A signing key is stored only when the issuer runs as its own program, not through dotnet.";

    private const int MaxBytes = 16 * 1024;
    private const string Unreadable = "The stored signing key is not readable.";

    private readonly string? trustedPath;
    private readonly MacKeychainItem keychain;

    public MacKeychainSigningKeyStore(string? processPath, MacKeychainItem? keychain = null)
    {
        trustedPath = MacKeychainTokenStore.TrustedApplicationPath(processPath);
        this.keychain = keychain ?? MacKeychainItem.Default;
    }

    public bool IsSupported => true;

    public string? WriteRefusal => trustedPath is null ? SharedHostRefused : null;

    public bool Exists(string keyId) => keychain.Exists(Service, keyId);

    public byte[]? Read(string keyId) => keychain.Read(Service, keyId, MaxBytes, ReadKey);

    public bool TryCreate(string keyId, byte[] pkcs8)
    {
        if (trustedPath is null)
        {
            throw new TokenStoreException(SharedHostRefused);
        }

        return keychain.TryCreate(Service, keyId, pkcs8, trustedPath);
    }

    private byte[] ReadKey(IntPtr item, ReadOnlySpan<byte> stored)
    {
        // The raw SEQUENCE tag is 0x30. Base64 of that tag starts with 'M'.
        if (stored is [0x30, ..])
        {
            Validate(stored);
            return stored.ToArray();
        }

        return DecodeLegacy(item, stored.ToArray());
    }

    // Owns the mutable copy, including any prefix overwritten by a partial decode.
    internal byte[] DecodeLegacy(IntPtr item, byte[] encoded)
    {
        try
        {
            if (Base64.DecodeFromUtf8InPlace(encoded, out int length) != OperationStatus.Done)
            {
                throw new TokenStoreException(Unreadable);
            }

            ReadOnlyMemory<byte> der = encoded.AsMemory(0, length);
            Validate(der.Span);
            if (trustedPath is not null)
            {
                keychain.UpdateData(item, der);
            }

            return der.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void Validate(ReadOnlySpan<byte> der)
    {
        try
        {
            // ImportPkcs8PrivateKey also accepts BER. Check the DER of both PrivateKeyInfo and
            // the ECPrivateKey carried in its privateKey OCTET STRING before letting the provider import it.
            ReadOnlySpan<byte> fields = CheckDerSequence(der);
            AsnDecoder.ReadIntegerBytes(fields, AsnEncodingRules.DER, out int versionLength);
            fields = fields[versionLength..];
            AsnDecoder.ReadSequence(fields, AsnEncodingRules.DER, out _, out _, out int algorithmLength);
            if (!AsnDecoder.TryReadPrimitiveOctetString(fields[algorithmLength..], AsnEncodingRules.DER,
                    out ReadOnlySpan<byte> ecPrivateKey, out _))
            {
                throw new TokenStoreException(Unreadable);
            }

            CheckDerSequence(ecPrivateKey);
            using ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(der, out int read);
            if (read != der.Length
                || key.ExportParameters(false).Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new TokenStoreException(Unreadable);
            }
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new TokenStoreException(Unreadable);
        }
    }

    private static ReadOnlySpan<byte> CheckDerSequence(ReadOnlySpan<byte> encoded)
    {
        AsnDecoder.ReadSequence(encoded, AsnEncodingRules.DER, out int start, out _, out int consumed);
        if (consumed != encoded.Length)
        {
            throw new TokenStoreException(Unreadable);
        }

        // Visit nested headers without recursion. The EC importer checks the field types and values.
        ReadOnlySpan<byte> fields = encoded[start..];
        ReadOnlySpan<byte> remaining = fields;
        while (!remaining.IsEmpty)
        {
            Asn1Tag tag = AsnDecoder.ReadEncodedValue(remaining, AsnEncodingRules.DER, out int header, out _, out int total);
            if (tag == Asn1Tag.ConstructedOctetString || tag == Asn1Tag.ConstructedBitString)
            {
                throw new TokenStoreException(Unreadable);
            }

            remaining = remaining[(tag.IsConstructed ? header : total)..];
        }

        return fields;
    }
}
