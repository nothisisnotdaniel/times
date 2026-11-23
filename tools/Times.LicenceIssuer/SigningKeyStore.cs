using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    // Complete DER encodings of id-ecPublicKey and prime256v1, respectively.
    private static ReadOnlySpan<byte> EcPublicKeyOid => [0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01];
    private static ReadOnlySpan<byte> P256Oid => [0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07];
    private static ReadOnlySpan<byte> P256Order =>
    [
        0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xbc, 0xe6, 0xfa, 0xad, 0xa7, 0x17, 0x9e, 0x84, 0xf3, 0xb9, 0xca, 0xc2, 0xfc, 0x63, 0x25, 0x51,
    ];

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

    public byte[]? Read(string keyId) => Read(IntPtr.Zero, keyId);

    internal byte[]? Read(IntPtr search, string keyId) =>
        keychain.ReadAndMigrate(search, Service, keyId, MaxBytes, trustedPath is not null, ReadKey);

    public bool TryCreate(string keyId, byte[] pkcs8)
    {
        if (trustedPath is null)
        {
            throw new TokenStoreException(SharedHostRefused);
        }

        return keychain.TryCreate(Service, keyId, pkcs8, trustedPath);
    }

    private static byte[] ReadKey(ReadOnlySpan<byte> stored)
    {
        // The raw SEQUENCE tag is 0x30. Base64 of that tag starts with 'M'.
        if (stored is [0x30, ..])
        {
            byte[] der = stored.ToArray();
            try
            {
                Validate(der);
                return der;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(der);
                throw;
            }
        }

        return DecodeLegacy(stored.ToArray());
    }

    // Owns the mutable copy, including any prefix overwritten by a partial decode.
    internal static byte[] DecodeLegacy(byte[] encoded)
    {
        try
        {
            if (Base64.DecodeFromUtf8InPlace(encoded, out int length) != OperationStatus.Done)
            {
                throw new TokenStoreException(Unreadable);
            }

            ReadOnlyMemory<byte> der = encoded.AsMemory(0, length);
            Validate(der);
            return der.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void Validate(ReadOnlyMemory<byte> der)
    {
        try
        {
            // Some providers accept BER or infer a curve from the key size. Check the encoded
            // PrivateKeyInfo (RFC 5208) and ECPrivateKey (RFC 5915) before asking the provider to use it.
            AsnReader reader = new AsnReader(der, AsnEncodingRules.DER);
            AsnReader fields = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            Require(fields.TryReadInt32(out int version) && version == 0);
            AsnReader algorithm = fields.ReadSequence();
            Require(algorithm.ReadEncodedValue().Span.SequenceEqual(EcPublicKeyOid));
            ReadP256Curve(algorithm);
            Require(fields.TryReadPrimitiveOctetString(out ReadOnlyMemory<byte> ecPrivateKey));
            ValidateEcPrivateKey(ecPrivateKey);
            if (fields.HasData)
            {
                ReadAttributes(fields.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)));
            }

            fields.ThrowIfNotEmpty();
            using ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(der.Span, out int read);
            Require(read == der.Length && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new TokenStoreException(Unreadable);
        }
    }

    private static void ReadP256Curve(AsnReader parameters)
    {
        Require(parameters.ReadEncodedValue().Span.SequenceEqual(P256Oid));
        parameters.ThrowIfNotEmpty();
    }

    private static void ValidateEcPrivateKey(ReadOnlyMemory<byte> encoded)
    {
        AsnReader reader = new AsnReader(encoded, AsnEncodingRules.DER);
        AsnReader fields = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        Require(fields.TryReadInt32(out int version) && version == 1);
        Require(fields.TryReadPrimitiveOctetString(out ReadOnlyMemory<byte> scalar) && scalar.Length == 32);
        Require(scalar.Span.ContainsAnyExcept((byte)0) && scalar.Span.SequenceCompareTo(P256Order) < 0);
        Asn1Tag parameters = new Asn1Tag(TagClass.ContextSpecific, 0);
        if (fields.HasData && fields.PeekTag().HasSameClassAndValue(parameters))
        {
            ReadP256Curve(fields.ReadSequence(parameters));
        }

        if (fields.HasData)
        {
            AsnReader publicKey = fields.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
            Require(publicKey.TryReadPrimitiveBitString(out int unused, out ReadOnlyMemory<byte> point)
                && unused == 0 && !point.IsEmpty);
            publicKey.ThrowIfNotEmpty();
            CheckPublicPoint(scalar.Span, point.Span);
        }

        fields.ThrowIfNotEmpty();
    }

    private static void CheckPublicPoint(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        // Import D alone: providers that accept a supplied Q can export it unchanged,
        // even when it is not D times the generator.
        byte[] d = scalar.ToArray();
        try
        {
            using ECDsa derived = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            ECPoint q = derived.ExportParameters(false).Q;
            Require(point.Length == 65 && point[0] == 4 && point.Slice(1, 32).SequenceEqual(q.X)
                && point[33..].SequenceEqual(q.Y));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
        }
    }

    private static void ReadAttributes(AsnReader attributes)
    {
        while (attributes.HasData)
        {
            AsnReader attribute = attributes.ReadSequence();
            ReadObjectIdentifierBytes(attribute);
            AsnReader values = attribute.ReadSetOf();
            attribute.ThrowIfNotEmpty();
            Require(values.HasData);
            while (values.HasData)
            {
                ValidateAttributeValue(values.ReadEncodedValue());
            }
        }
    }

    private static void ValidateAttributeValue(ReadOnlyMemory<byte> encoded)
    {
        // Attribute values have open types. Walk constructed values within their own bounds
        // without using the call stack, including DER ordering on nested universal sets.
        Stack<AsnReader> levels = new Stack<AsnReader>();
        levels.Push(new AsnReader(encoded, AsnEncodingRules.DER));
        while (levels.TryPeek(out AsnReader? level))
        {
            if (!level.HasData)
            {
                levels.Pop();
                continue;
            }

            Asn1Tag tag = level.PeekTag();
            if (tag == Asn1Tag.SetOf)
            {
                levels.Push(level.ReadSetOf());
            }
            else if (tag.IsConstructed)
            {
                Require(tag.TagClass != TagClass.Universal || (UniversalTagNumber)tag.TagValue is
                    UniversalTagNumber.Sequence or UniversalTagNumber.External or UniversalTagNumber.Embedded
                    or UniversalTagNumber.UnrestrictedCharacterString);
                levels.Push(new AsnReader(level.PeekContentBytes(), AsnEncodingRules.DER));
                level.ReadEncodedValue();
            }
            else
            {
                ValidatePrimitive(level, tag);
            }
        }
    }

    private static void ValidatePrimitive(AsnReader reader, Asn1Tag tag)
    {
        if (tag.TagClass == TagClass.Universal)
        {
            switch ((UniversalTagNumber)tag.TagValue)
            {
                case UniversalTagNumber.EndOfContents:
                case UniversalTagNumber.Sequence:
                case UniversalTagNumber.Set:
                case UniversalTagNumber.External:
                case UniversalTagNumber.Embedded:
                case UniversalTagNumber.UnrestrictedCharacterString:
                    throw new TokenStoreException(Unreadable);
                case UniversalTagNumber.Boolean:
                    reader.ReadBoolean();
                    return;
                case UniversalTagNumber.Integer:
                    reader.ReadIntegerBytes();
                    return;
                case UniversalTagNumber.BitString:
                    Require(reader.TryReadPrimitiveBitString(out _, out _));
                    return;
                case UniversalTagNumber.Null:
                    reader.ReadNull();
                    return;
                case UniversalTagNumber.ObjectIdentifier:
                    ReadObjectIdentifierBytes(reader);
                    return;
                case UniversalTagNumber.Enumerated:
                    reader.ReadEnumeratedBytes();
                    return;
                case UniversalTagNumber.UtcTime:
                    reader.ReadUtcTime();
                    return;
                case UniversalTagNumber.GeneralizedTime:
                    reader.ReadGeneralizedTime();
                    return;
                case UniversalTagNumber.OctetString:
                    Require(reader.TryReadPrimitiveOctetString(out _));
                    return;
                case UniversalTagNumber.RelativeObjectIdentifier:
                    ValidateOidComponents(reader.PeekContentBytes().Span);
                    break;
                case UniversalTagNumber.UniversalString:
                    ReadOnlySpan<byte> utf32 = reader.PeekContentBytes().Span;
                    Require(utf32.Length % 4 == 0);
                    for (int i = 0; i < utf32.Length; i += 4)
                    {
                        Require(Rune.IsValid(BinaryPrimitives.ReadInt32BigEndian(utf32[i..])));
                    }

                    break;
                case UniversalTagNumber.UTF8String:
                case UniversalTagNumber.BMPString:
                case UniversalTagNumber.IA5String:
                case UniversalTagNumber.NumericString:
                case UniversalTagNumber.PrintableString:
                case UniversalTagNumber.VisibleString:
                case UniversalTagNumber.T61String:
                    Span<char> characters = new char[reader.PeekContentBytes().Length];
                    try
                    {
                        Require(reader.TryReadCharacterString(characters, (UniversalTagNumber)tag.TagValue, out _));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters));
                    }

                    return;
                default:
                    // No DER content validator for this universal type.
                    throw new TokenStoreException(Unreadable);
            }
        }

        reader.ReadEncodedValue();
    }

    private static void ReadObjectIdentifierBytes(AsnReader reader)
    {
        Require(reader.PeekTag() == Asn1Tag.ObjectIdentifier);
        ValidateOidComponents(reader.PeekContentBytes().Span);
        reader.ReadEncodedValue();
    }

    private static void ValidateOidComponents(ReadOnlySpan<byte> value)
    {
        Require(!value.IsEmpty);
        bool start = true;
        foreach (byte part in value)
        {
            Require(!start || part != 0x80); // no leading zero group in a base-128 component
            start = (part & 0x80) == 0;
        }

        Require(start);
    }

    private static void Require(bool valid)
    {
        if (!valid)
        {
            throw new TokenStoreException(Unreadable);
        }
    }
}
