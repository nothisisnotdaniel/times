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
/// with a fixed message that never holds a key.
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
        using ECDsa key = ECDsa.Create();
        ImportKey(key, der);
    }

    internal static void ImportKey(ECDsa key, ReadOnlyMemory<byte> der)
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
            ReadOnlyMemory<byte> point = ValidateEcPrivateKey(ecPrivateKey, out ReadOnlyMemory<byte> scalar);
            if (fields.HasData)
            {
                ReadAttributes(fields.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)));
            }

            fields.ThrowIfNotEmpty();
            if (point.IsEmpty && OperatingSystem.IsMacOS())
            {
                // The provider's scalar-only import re-exports an uncleared managed
                // private scalar. Obtain the optional public point from an ephemeral key.
                byte[] derived = new byte[65];
                byte[] privateScalar = scalar.ToArray();
                try
                {
                    Require(CopyPublicPoint(privateScalar, derived) == 0);
                    ECParameters parameters = PublicParameters(derived);
                    parameters.D = privateScalar;
                    key.ImportParameters(parameters);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateScalar);
                }
            }
            else
            {
                key.ImportPkcs8PrivateKey(der.Span, out int read);
                Require(read == der.Length);
            }

            Require(key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value);
            if (!point.IsEmpty)
            {
                CheckPublicPoint(key, point.Span);
            }
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException
            or NotSupportedException or TypeLoadException or BadImageFormatException)
        {
            throw new TokenStoreException(Unreadable);
        }
    }

    private static void ReadP256Curve(AsnReader parameters)
    {
        Require(parameters.ReadEncodedValue().Span.SequenceEqual(P256Oid));
        parameters.ThrowIfNotEmpty();
    }

    private static ReadOnlyMemory<byte> ValidateEcPrivateKey(ReadOnlyMemory<byte> encoded, out ReadOnlyMemory<byte> scalar)
    {
        AsnReader reader = new AsnReader(encoded, AsnEncodingRules.DER);
        AsnReader fields = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        Require(fields.TryReadInt32(out int version) && version == 1);
        Require(fields.TryReadPrimitiveOctetString(out scalar) && scalar.Length == 32);
        Require(scalar.Span.ContainsAnyExcept((byte)0) && scalar.Span.SequenceCompareTo(P256Order) < 0);
        Asn1Tag parameters = new Asn1Tag(TagClass.ContextSpecific, 0);
        if (fields.HasData && fields.PeekTag().HasSameClassAndValue(parameters))
        {
            ReadP256Curve(fields.ReadSequence(parameters));
        }

        ReadOnlyMemory<byte> point = default;
        if (fields.HasData)
        {
            AsnReader publicKey = fields.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
            Require(publicKey.TryReadPrimitiveBitString(out int unused, out point)
                && unused == 0 && point.Length == 65 && point.Span[0] == 4);
            publicKey.ThrowIfNotEmpty();
        }

        fields.ThrowIfNotEmpty();
        return point;
    }

    private static ECParameters PublicParameters(ReadOnlySpan<byte> point) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point[33..].ToArray() },
    };

    [DllImport("TimesIssuerKeychain", EntryPoint = "TimesCopyPublicPoint")]
    private static extern int CopyPublicPoint(byte[] scalar, [Out] byte[] point);

    private static void CheckPublicPoint(ECDsa key, ReadOnlySpan<byte> point)
    {
        using ECDsa verifier = ECDsa.Create(PublicParameters(point));
        // A pairwise consistency test uses the supplied point independently of what
        // the provider exports. Importing the scalar alone can re-export it on macOS.
        ReadOnlySpan<byte> challenge = "Times issuer P-256 key consistency"u8;
        Span<byte> signature = stackalloc byte[64];
        Require(key.TrySignData(challenge, signature, HashAlgorithmName.SHA256, out int count)
            && verifier.VerifyData(challenge, signature[..count], HashAlgorithmName.SHA256));
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
        // without using the call stack. Tag 17 alone cannot distinguish SET from SET OF.
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
                AsnReader children = level.ReadSetOf(skipSortOrderValidation: true);
                ValidateOpenSetOrder(children.Clone());
                levels.Push(children);
            }
            else if (tag.IsConstructed)
            {
                Require(tag.TagClass != TagClass.Universal || (UniversalTagNumber)tag.TagValue is
                    UniversalTagNumber.Sequence or UniversalTagNumber.External or UniversalTagNumber.Embedded
                    or UniversalTagNumber.UnrestrictedCharacterString);
                AsnReader contents = new(level.PeekContentBytes(), AsnEncodingRules.DER);
                level.ReadEncodedValue();
                if (tag.TagClass == TagClass.Universal && tag.TagValue == (int)UniversalTagNumber.External)
                {
                    ReadExternal(contents, levels);
                }
                else if (tag.TagClass == TagClass.Universal && tag.TagValue is (int)UniversalTagNumber.Embedded
                    or (int)UniversalTagNumber.UnrestrictedCharacterString)
                {
                    ReadEmbedded(contents);
                }
                else
                {
                    levels.Push(contents);
                }
            }
            else
            {
                ValidatePrimitive(level, tag);
            }
        }
    }

    private static void ValidateOpenSetOrder(AsnReader members)
    {
        ReadOnlyMemory<byte> previous = default;
        Asn1Tag previousTag = default;
        bool set = true;
        bool setOf = true;
        while (members.HasData)
        {
            Asn1Tag tag = members.PeekTag();
            ReadOnlyMemory<byte> current = members.ReadEncodedValue();
            if (!previous.IsEmpty)
            {
                set &= tag.TagClass > previousTag.TagClass
                    || tag.TagClass == previousTag.TagClass && tag.TagValue > previousTag.TagValue;
                setOf &= previous.Span.SequenceCompareTo(current.Span) <= 0;
                Require(set || setOf);
            }

            previous = current;
            previousTag = tag;
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
                case UniversalTagNumber.Real:
                    ValidateReal(reader.PeekContentBytes().Span);
                    break;
                case UniversalTagNumber.ObjectDescriptor:
                case UniversalTagNumber.GraphicString:
                case UniversalTagNumber.GeneralString:
                case UniversalTagNumber.VideotexString:
                    ValidateIso2022(reader.PeekContentBytes().Span,
                        tag.TagValue is (int)UniversalTagNumber.GraphicString or (int)UniversalTagNumber.ObjectDescriptor,
                        tag.TagValue == (int)UniversalTagNumber.VideotexString);
                    break;
                case UniversalTagNumber.Date:
                    ValidateDate(reader.PeekContentBytes().Span);
                    break;
                case UniversalTagNumber.TimeOfDay:
                    ValidateTimeOfDay(reader.PeekContentBytes().Span);
                    break;
                case UniversalTagNumber.DateTime:
                    ReadOnlySpan<byte> dateTime = reader.PeekContentBytes().Span;
                    Require(dateTime.Length == 14);
                    ValidateDate(dateTime[..8]);
                    ValidateTimeOfDay(dateTime[8..]);
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

    private static void ValidateDate(ReadOnlySpan<byte> value)
    {
        // X.690 8.26.2 encodes the calendar date as eight ASCII digits.
        Require(value.Length == 8 && !value.ContainsAnyExceptInRange((byte)'0', (byte)'9'));
        int year = (value[0] - '0') * 1000 + (value[1] - '0') * 100 + (value[2] - '0') * 10 + value[3] - '0';
        int month = (value[4] - '0') * 10 + value[5] - '0';
        int day = (value[6] - '0') * 10 + value[7] - '0';
        Require(month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year == 0 ? 400 : year, month));
    }

    private static void ValidateTimeOfDay(ReadOnlySpan<byte> value)
    {
        // X.680 38.4.2 and X.690 8.26.3: local HHMMSS, including end-of-day midnight.
        Require(value.Length == 6 && !value.ContainsAnyExceptInRange((byte)'0', (byte)'9'));
        int hour = (value[0] - '0') * 10 + value[1] - '0';
        int minute = (value[2] - '0') * 10 + value[3] - '0';
        int second = (value[4] - '0') * 10 + value[5] - '0';
        Require(hour < 24 && minute < 60 && second <= 60 || hour == 24 && minute == 0 && second == 0);
    }

    private static void ValidateIso2022(ReadOnlySpan<byte> value, bool graphicOnly, bool videotex)
    {
        // ISO 2022 designates sets G0..G3 independently of the left/right or single shift
        // that invokes one. A designation can change the width of a graphic character.
        Span<int> widths = [1, 0, 0, 0];
        Span<Range> designations = stackalloc Range[4];
        designations.Clear();
        Span<bool> sets96 = stackalloc bool[4];
        sets96.Clear();
        int left = 0, right = 1, single = -1;
        for (int i = 0; i < value.Length; i++)
        {
            byte current = value[i];
            if (current == 0x1b)
            {
                Require(single < 0);
                int start = ++i;
                while (i < value.Length && value[i] is >= 0x20 and <= 0x2f)
                {
                    i++;
                }

                Require(i < value.Length && value[i] is >= 0x30 and <= 0x7e);
                ReadGraphicEscape(value, start, i, videotex, widths, sets96, designations, ref left, ref right, ref single);
            }
            else if (current is 0x0e or 0x0f or 0x8e or 0x8f)
            {
                Require(single < 0);
                if (current < 0x80)
                {
                    left = current == 0x0e ? 1 : 0;
                }
                else
                {
                    single = current - 0x8c;
                }
            }
            else if (current is < 0x20 or >= 0x80 and < 0xa0)
            {
                Require(!graphicOnly && single < 0);
            }
            else
            {
                int set = single >= 0 ? single : current < 0x80 ? left : right;
                int width = widths[set];
                Require(width > 0);
                single = -1;
                if (!sets96[set] && current == 0x20)
                {
                    continue; // SPACE in the left half of a 94-character repertoire
                }

                byte low = (byte)((current & 0x80) + (sets96[set] ? 0x20 : 0x21));
                byte high = (byte)((current & 0x80) + (sets96[set] ? 0x7f : 0x7e));
                Require(width <= value.Length - i && !value.Slice(i, width).ContainsAnyExceptInRange(low, high));
                i += width - 1;
            }
        }

        Require(single < 0);
    }

    private static void ReadGraphicEscape(ReadOnlySpan<byte> value, int start, int end, bool videotex,
        Span<int> widths, Span<bool> sets96, Span<Range> designations, ref int left, ref int right, ref int single)
    {
        ReadOnlySpan<byte> intermediates = value[start..end];
        byte final = value[end];
        if (intermediates.IsEmpty)
        {
            switch (final)
            {
                case (byte)'N' or (byte)'O':
                    single = final - 'N' + 2;
                    break;
                case (byte)'n' or (byte)'o':
                    left = final - 'n' + 2;
                    break;
                case (byte)'~' or (byte)'}' or (byte)'|':
                    right = '~' - final + 1;
                    break;
            }
            return;
        }

        bool multibyte = intermediates[0] == '$';
        int slot;
        int nameStart;
        if (multibyte && intermediates.Length == 1)
        {
            Require(final is >= 0x40 and <= 0x42); // original multibyte G0 designations
            slot = '(';
            nameStart = end;
        }
        else
        {
            slot = intermediates[multibyte ? 1 : 0];
            nameStart = start + (multibyte ? 2 : 1);
            if (slot is < '(' or > '/' or ',')
            {
                Require(!multibyte);
                return; // e.g. designation of a control set
            }
        }

        int set = slot & 3;
        // ECMA-35 14.3.3 gives the widths of the registered multibyte sets.
        Require(!multibyte || final is >= 0x40 and < 0x70);
        int width = multibyte ? (final < 0x60 ? 2 : 3) : 1;
        bool is96 = slot >= '-';
        Range name = nameStart..(end + 1);
        if (widths[set] == width && sets96[set] == is96)
        {
            ReadOnlySpan<byte> previous = value[designations[set]];
            if (previous.IsEmpty)
            {
                previous = videotex ? "u"u8 : "B"u8; // X.690 table 3, initial G0
            }
            Require(!previous.SequenceEqual(value[name]));
        }
        designations[set] = name;
        widths[set] = width;
        sets96[set] = is96;
    }

    private static Asn1Tag Context(int number) => new(TagClass.ContextSpecific, number);

    private static void ReadExternal(AsnReader fields, Stack<AsnReader> levels)
    {
        // X.690 8.18. The identification is followed by exactly one encoding choice.
        bool direct = fields.PeekTag() == Asn1Tag.ObjectIdentifier;
        if (direct)
        {
            ReadObjectIdentifierBytes(fields);
        }

        bool indirect = fields.PeekTag() == Asn1Tag.Integer;
        if (indirect)
        {
            fields.ReadIntegerBytes();
        }

        Require(direct || indirect);
        if (fields.PeekTag() == new Asn1Tag(UniversalTagNumber.ObjectDescriptor))
        {
            ValidatePrimitive(fields, fields.PeekTag());
        }

        Asn1Tag encoding = fields.PeekTag();
        if (encoding.HasSameClassAndValue(Context(0)))
        {
            AsnReader explicitValue = fields.ReadSequence(Context(0));
            levels.Push(new AsnReader(explicitValue.ReadEncodedValue(), AsnEncodingRules.DER));
            explicitValue.ThrowIfNotEmpty();
        }
        else if (encoding == Context(1))
        {
            Require(fields.TryReadPrimitiveOctetString(out _, Context(1)));
        }
        else
        {
            Require(fields.TryReadPrimitiveBitString(out _, out _, Context(2)));
        }

        fields.ThrowIfNotEmpty();
    }

    private static void ReadEmbedded(AsnReader fields)
    {
        // X.680 36.5 and 44.5 use automatic tagging. The identification CHOICE
        // has an explicit wrapper and the descriptor is constrained to be absent.
        AsnReader identification = fields.ReadSequence(Context(0));
        Asn1Tag choice = identification.PeekTag();
        Require(choice.TagClass == TagClass.ContextSpecific);
        switch (choice.TagValue)
        {
            case 0:
            case 3:
                AsnReader pair = identification.ReadSequence(choice);
                if (choice.TagValue == 0)
                {
                    ReadObjectIdentifierBytes(pair, Context(0));
                }
                else
                {
                    pair.ReadIntegerBytes(Context(0));
                }

                ReadObjectIdentifierBytes(pair, Context(1));
                pair.ThrowIfNotEmpty();
                break;
            case 1:
            case 4:
                ReadObjectIdentifierBytes(identification, Context(choice.TagValue));
                break;
            case 2:
                identification.ReadIntegerBytes(Context(2));
                break;
            case 5:
                identification.ReadNull(Context(5));
                break;
            default:
                throw new TokenStoreException(Unreadable);
        }

        identification.ThrowIfNotEmpty();
        Require(fields.TryReadPrimitiveOctetString(out _, Context(2)));
        fields.ThrowIfNotEmpty();
    }

    private static void ValidateReal(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value is [>= 0x40 and <= 0x43])
        {
            return; // positive zero, infinities, NaN, and negative zero
        }

        if (value[0] == 3)
        {
            // Canonical NR3 (X.690 11.3.2), checked without converting to text.
            ReadOnlySpan<byte> number = value[1..];
            if (number.StartsWith("-"u8))
            {
                number = number[1..];
            }

            int end = number.IndexOf(".E"u8);
            Require(end > 0 && number[0] != '0' && number[end - 1] != '0'
                && !number[..end].ContainsAnyExceptInRange((byte)'0', (byte)'9'));
            ReadOnlySpan<byte> exponent = number[(end + 2)..];
            if (!exponent.SequenceEqual("+0"u8))
            {
                if (exponent.StartsWith("-"u8))
                {
                    exponent = exponent[1..];
                }

                Require(!exponent.IsEmpty && exponent[0] != '0'
                    && !exponent.ContainsAnyExceptInRange((byte)'0', (byte)'9'));
            }

            return;
        }

        // Canonical binary uses base two, zero scaling, a minimal signed
        // exponent and a minimal odd unsigned mantissa (X.690 11.3.1).
        Require((value[0] & 0xbc) == 0x80 && value.Length >= 3);
        int length = (value[0] & 3) + 1;
        int start = 1;
        if (length == 4)
        {
            length = value[1];
            start++;
            Require(length >= 4);
        }

        Require(start + length < value.Length && value[start + length] != 0 && (value[^1] & 1) == 1);
        Require(length == 1 || !(value[start] == 0 && value[start + 1] < 0x80
            || value[start] == 0xff && value[start + 1] >= 0x80));
    }

    private static void ReadObjectIdentifierBytes(AsnReader reader, Asn1Tag? tag = null)
    {
        Require(reader.PeekTag() == (tag ?? Asn1Tag.ObjectIdentifier));
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
