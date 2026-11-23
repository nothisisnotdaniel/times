using System.Buffers;
using System.Buffers.Text;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Times.LicenceIssuer;

namespace Times.Tests;

public sealed class SigningKeyStoreTests : IDisposable
{
    private const string Service = "Suite Pro signing key";
    private const string Kid = "maker";
    private const string Unreadable = "The stored signing key is not readable.";
    private const string SaveFailed = "The item could not be saved in the macOS Keychain.";

    private readonly TempFolder folder = new();
    private readonly FakeKeychain native = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly string apphost;
    private readonly MacKeychainSigningKeyStore store;

    public SigningKeyStoreTests()
    {
        apphost = Path.Combine(folder.Path, "Times.LicenceIssuer");
        File.WriteAllBytes(apphost, []);
        store = StoreFor(apphost);
    }

    public void Dispose() => folder.Dispose();

    private string Ledger => Path.Combine(folder.Path, "ledger.tsv");

    private MacKeychainSigningKeyStore StoreFor(string? path) => new(path, new MacKeychainItem(native));

    private int Run(ISigningKeyStore keys, params string[] args)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        using LicenceIssuerTests.CountingRandom random = new LicenceIssuerTests.CountingRandom();
        return new Issuer(output, error, keys, random, FixedTimeProvider.At20261027(), Ledger).Run(args);
    }

    private int Issue(ISigningKeyStore keys) =>
        Run(keys, "issue", "--kid", Kid, "--name", "Ada", "--until", "2027-12-31");

    [Fact]
    public void CreateKey_AddsRawDerWithOnlyTheApphostAndPrintsItsPublicPem()
    {
        Assert.Equal(0, Run(store, "create-key", "--kid", Kid));

        Item item = Assert.IsType<Item>(native.Stored);
        Assert.Equal([apphost], item.TrustedPaths);
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(item.Data, out int read);
        Assert.Equal(item.Data.Length, read);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, key.ExportParameters(false).Curve.Oid.Value);
        Assert.Equal(key.ExportSubjectPublicKeyInfoPem() + output.NewLine, output.ToString());
        Assert.Empty(error.ToString());
        Assert.Equal(1, native.Creates);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        AssertCleared(native.CreateBuffer!);
    }

    [Fact]
    public void TryCreate_PassesTheExactSuppliedDer()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] original = der.ToArray();

        Assert.True(store.TryCreate(Kid, der));
        Assert.Equal(original, native.Stored!.Data);
        Assert.Equal(original, der);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateKey_ExistingOrRacingItem_KeepsItsIdentityDataAndAccess(bool race)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] original = Legacy(key.ExportPkcs8PrivateKey());
        Item existing = new Item(original, ["original app", "another app"]);
        if (race)
        {
            native.AfterMissingLookup = () => native.Stored = existing;
        }
        else
        {
            native.Stored = existing;
        }

        Assert.Equal(1, Run(store, "create-key", "--kid", Kid));
        Assert.Equal($"A signing key named {Kid} already exists; it was left as it is." + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Same(existing, native.Stored);
        Assert.Equal(original, existing.Data);
        Assert.Equal(["original app", "another app"], existing.TrustedPaths);
        Assert.Equal(race ? 1 : 0, native.Creates);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        if (race)
        {
            AssertCleared(native.CreateBuffer!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Issue_Twice_VerifiesWithTheOriginalKeyAndMigratesOnlyLegacyData(bool legacy)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Item item = new Item(legacy ? Legacy(der) : der, ["previous app", "keeper"]);
        native.Stored = item;

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(0, Issue(store));
            Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
            Assert.Empty(error.ToString());
            Assert.Same(item, native.Stored);
            Assert.Equal(der, item.Data);
            Assert.Equal(["previous app", "keeper"], item.TrustedPaths);
            Assert.Equal(legacy ? 1 : 0, native.Updates);
        }

        Assert.Equal(2, File.ReadAllLines(Ledger).Length);
        Assert.Equal(der, store.Read(Kid));
        Assert.Equal(3, native.Frees);
        Assert.Equal(0, native.Creates);
        Assert.Equal(0, native.Deletes);
        if (legacy)
        {
            AssertCleared(native.UpdateBuffer!);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WriteRefusal_BlocksAllWritesAndStillIssuesWithEitherFormat(bool sharedHost, bool legacy)
    {
        string dotnet = Path.Combine(folder.Path, "dotnet");
        File.WriteAllBytes(dotnet, []);
        MacKeychainSigningKeyStore refused = StoreFor(sharedHost ? dotnet : null);
        Assert.Equal(MacKeychainSigningKeyStore.SharedHostRefused, refused.WriteRefusal);
        Assert.Equal(1, Run(refused, "create-key", "--kid", Kid));
        Assert.Empty(output.ToString());
        Assert.Equal(refused.WriteRefusal + error.NewLine, error.ToString());
        Assert.Equal(0, native.Finds);
        Assert.Null(native.Stored);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        Assert.Equal(refused.WriteRefusal, Assert.Throws<TokenStoreException>(() => refused.TryCreate(Kid, der)).Message);
        byte[] before = legacy ? Legacy(der) : der;
        Item item = new Item(before, ["authorized reader"]);
        native.Stored = item;
        Assert.Equal(der, refused.Read(Kid));
        Assert.Equal(0, Issue(refused));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Same(item, native.Stored);
        Assert.Equal(before, item.Data);
        Assert.Equal(["authorized reader"], item.TrustedPaths);
        Assert.Equal(0, native.AccessCreations);
        Assert.Equal(0, native.Creates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(0, native.Updates);
    }

    [Fact]
    public void Issue_FailedMigration_PreservesTheItemAndLedgerForRetry()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = Legacy(der);
        Item item = new Item(legacy, ["original app"]);
        native.Stored = item;
        native.RefuseUpdate = true;
        File.WriteAllText(Ledger, "previous entry\n");

        Assert.Equal(1, Issue(store));
        Assert.Equal(SaveFailed + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal("previous entry\n", File.ReadAllText(Ledger));
        Assert.Same(item, native.Stored);
        Assert.Equal(legacy, item.Data);
        Assert.Equal(["original app"], item.TrustedPaths);
        AssertCleared(native.UpdateBuffer!);
        Assert.Equal(1, native.Frees);

        native.RefuseUpdate = false;
        Assert.Equal(0, Issue(store));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Equal(der, item.Data);
        Assert.Equal(2, native.Updates);
        Assert.Equal(2, native.Frees);
        Assert.Equal(2, File.ReadAllLines(Ledger).Length);
    }

    [Fact]
    public void Read_Missing_IsNull()
    {
        Assert.Null(store.Read(Kid));
        Assert.Equal(1, Issue(store));
        Assert.Equal($"There is no signing key named {Kid}. Run create-key --kid {Kid} first." + error.NewLine,
            error.ToString());
        Assert.Empty(output.ToString());
        Assert.False(File.Exists(Ledger));
        Assert.Equal(0, native.Frees);
    }

    [Fact]
    public void Read_LegacyAtTheSizeLimit_AcceptsBase64Whitespace()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] stored = new byte[16 * 1024];
        stored.AsSpan().Fill((byte)' ');
        Legacy(der).CopyTo(stored, 1);
        native.Stored = new Item(stored, [apphost]);

        Assert.Equal(der, store.Read(Kid));
        Assert.Equal(der, native.Stored.Data);
        Assert.Equal(1, native.Updates);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("empty")]
    [InlineData("partial base64")]
    [InlineData("encoded junk")]
    [InlineData("wrong curve")]
    [InlineData("encoded wrong curve")]
    [InlineData("trailing DER")]
    [InlineData("encoded trailing DER")]
    [InlineData("BER algorithm")]
    [InlineData("encoded BER algorithm")]
    public void Issue_InvalidItem_ReportsOnlyTheFixedErrorAndPreservesData(string format)
    {
        using ECDsa key = ECDsa.Create(format.EndsWith("wrong curve", StringComparison.Ordinal)
            ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] original = format switch
        {
            "oversize" => Enumerable.Repeat((byte)0x5a, 16385).ToArray(),
            "empty" => [],
            "partial base64" => "QUJDREVG!!!!"u8.ToArray(),
            "encoded junk" => Legacy("not a key"u8),
            "wrong curve" => der,
            "encoded wrong curve" => Legacy(der),
            "trailing DER" => [.. der, 0],
            "encoded trailing DER" => Legacy([.. der, 0]),
            "BER algorithm" => WithIndefiniteAlgorithm(der),
            "encoded BER algorithm" => Legacy(WithIndefiniteAlgorithm(der)),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        Item item = new Item(original, ["original app"]);
        native.Stored = item;

        Assert.Equal(1, Issue(store));
        string message = format == "oversize" ? "The macOS Keychain item is too large." : Unreadable;
        Assert.Equal(message + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.False(File.Exists(Ledger));
        Assert.Same(item, native.Stored);
        Assert.Equal(original, item.Data);
        Assert.Equal(["original app"], item.TrustedPaths);
        Assert.Equal(1, native.Frees);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Creates);
        Assert.Equal(0, native.Deletes);
    }

    [Fact]
    public void DecodeLegacy_PartialDecode_ClearsTheEntireManagedBuffer()
    {
        byte[] partial = "QUJDREVG!!!!"u8.ToArray();
        Assert.Equal(Unreadable, Assert.Throws<TokenStoreException>(() => store.DecodeLegacy(IntPtr.Zero, partial)).Message);
        AssertCleared(partial);
    }

    private static byte[] Legacy(ReadOnlySpan<byte> der)
    {
        byte[] encoded = new byte[Base64.GetMaxEncodedToUtf8Length(der.Length)];
        Assert.Equal(OperationStatus.Done, Base64.EncodeToUtf8(der, encoded, out _, out int written));
        Assert.Equal(encoded.Length, written);
        return encoded;
    }

    private static byte[] WithIndefiniteAlgorithm(byte[] der)
    {
        AsnReader fields = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        AsnWriter writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(fields.ReadEncodedValue().Span);
            byte[] algorithm = [0x30, 0x80, .. fields.PeekContentBytes().Span, 0x00, 0x00];
            fields.ReadEncodedValue();
            writer.WriteEncodedValue(algorithm);
            writer.WriteEncodedValue(fields.ReadEncodedValue().Span);
        }

        return writer.Encode();
    }

    private static void AssertCleared(byte[] bytes)
    {
        Assert.NotEmpty(bytes);
        Assert.False(bytes.AsSpan().ContainsAnyExcept((byte)0));
    }

    private sealed class Item
    {
        public Item(byte[] data, string[] trustedPaths)
        {
            Data = data.ToArray();
            TrustedPaths = trustedPaths;
        }

        public byte[] Data { get; set; }
        public string[] TrustedPaths { get; }
    }

    // Models one generic password. A find retains the item and returns a separate, caller-owned native buffer.
    private sealed class FakeKeychain : MacKeychainItem.INativeCalls
    {
        private readonly HashSet<IntPtr> held = [];
        private readonly IntPtr itemRef = 1;
        private readonly IntPtr accessRef = 2;
        private IntPtr readPointer;
        private int readLength;
        private string? trustedPath;

        public Item? Stored { get; set; }
        public Action? AfterMissingLookup { get; set; }
        public bool RefuseUpdate { get; set; }
        public int Finds { get; private set; }
        public int AccessCreations { get; private set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Deletes { get; private set; }
        public int Frees { get; private set; }
        public byte[]? CreateBuffer { get; private set; }
        public byte[]? UpdateBuffer { get; private set; }

        public int Find(string service, string account, bool readData, out uint length, out IntPtr data, out IntPtr item)
        {
            Assert.Equal(Service, service);
            Assert.Equal(Kid, account);
            Finds++;
            length = 0;
            data = IntPtr.Zero;
            item = IntPtr.Zero;
            if (Stored is null)
            {
                if (!readData)
                {
                    AfterMissingLookup?.Invoke();
                }

                return -25300;
            }

            item = itemRef;
            Assert.True(held.Add(item));
            if (readData)
            {
                readLength = Stored.Data.Length;
                length = (uint)readLength;
                readPointer = Marshal.AllocHGlobal(Math.Max(1, readLength));
                Marshal.Copy(Stored.Data, 0, readPointer, readLength);
                data = readPointer;
            }

            return 0;
        }

        public IntPtr CreateAccess(string path, string description)
        {
            Assert.Equal(Service, description);
            Assert.True(held.Add(accessRef));
            AccessCreations++;
            trustedPath = path;
            return accessRef;
        }

        public int CreateFromContent(uint itemClass, ref MacKeychainItem.AttributeList attributes, byte[] data,
            IntPtr access, out IntPtr item)
        {
            Assert.Equal(0x67656e70u, itemClass); // kSecGenericPasswordItemClass
            Assert.Equal(accessRef, access);
            Assert.Contains(access, held);
            Dictionary<uint, string> values = [];
            for (int i = 0; i < attributes.Count; i++)
            {
                IntPtr pointer = attributes.Attributes + i * Marshal.SizeOf<MacKeychainItem.Attribute>();
                MacKeychainItem.Attribute attribute = Marshal.PtrToStructure<MacKeychainItem.Attribute>(pointer);
                values.Add(attribute.Tag, Marshal.PtrToStringUTF8(attribute.Data, (int)attribute.Length)!);
            }

            Assert.Equal(Service, values[0x73766365]); // svce
            Assert.Equal(Kid, values[0x61636374]); // acct
            Assert.Equal(Service, values[0x6c61626c]); // labl
            Creates++;
            CreateBuffer = data;
            if (Stored is not null)
            {
                item = IntPtr.Zero;
                return -25299;
            }

            Stored = new Item(data, [trustedPath!]);
            item = itemRef;
            Assert.True(held.Add(item));
            return 0;
        }

        public int ModifyAttributesAndData(IntPtr item, IntPtr attributes, ReadOnlyMemory<byte> data)
        {
            Assert.Equal(itemRef, item);
            Assert.Contains(item, held);
            Assert.Equal(IntPtr.Zero, attributes);
            Updates++;
            Assert.True(MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment));
            UpdateBuffer = segment.Array;
            if (RefuseUpdate)
            {
                return -1;
            }

            Stored!.Data = data.ToArray();
            return 0;
        }

        public int Delete(IntPtr item)
        {
            Assert.Contains(item, held);
            Deletes++;
            Stored = null;
            return 0;
        }

        public void FreeContent(IntPtr data)
        {
            try
            {
                Assert.Equal(readPointer, data);
                byte[] atRelease = new byte[readLength];
                Marshal.Copy(data, atRelease, 0, readLength);
                Assert.False(atRelease.AsSpan().ContainsAnyExcept((byte)0));
                Frees++;
            }
            finally
            {
                Marshal.FreeHGlobal(readPointer);
                readPointer = IntPtr.Zero;
            }
        }

        public void Release(IntPtr reference) => Assert.True(held.Remove(reference));
    }
}
