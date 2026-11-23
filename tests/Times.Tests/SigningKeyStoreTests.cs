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
    private const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";

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
        Assert.Equal(0, native.Frees);
        Assert.Equal(0, native.DataRequests);
        if (race)
        {
            AssertCleared(native.CreateBuffer!);
        }
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("after lookup")]
    [InlineData("during add")]
    public void CreateKey_IdInAnotherKeychain_RefusesAndPreservesTheCompetingKey(string arrival)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] original = Legacy(key.ExportPkcs8PrivateKey());
        Item existing = new Item(original, ["other reader"]);
        switch (arrival)
        {
            case "existing":
                native.OtherStored = existing;
                break;
            case "after lookup":
                native.AfterMissingLookup = () => native.OtherStored = existing;
                break;
            case "during add":
                native.AfterAdd = () => native.OtherStored = existing;
                break;
        }

        Assert.Equal(1, Run(store, "create-key", "--kid", Kid));
        Assert.Empty(output.ToString());
        Assert.Equal($"A signing key named {Kid} already exists; it was left as it is." + error.NewLine, error.ToString());
        Assert.Null(native.Stored);
        Assert.Same(existing, native.OtherStored);
        Assert.Equal(original, existing.Data);
        Assert.Equal(["other reader"], existing.TrustedPaths);
        Assert.Equal(Item.OriginalPartition, existing.Partition);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(0, native.Frees);
        Assert.Equal(0, native.DataRequests);
    }

    [Fact]
    public void Issue_DefaultKeyWinsEvenWhenAnotherKeychainIsSearchedFirst()
    {
        Assert.Equal(0, Run(store, "create-key", "--kid", Kid));
        LicencePublicKeys created = LicencePublicKeys.FromTable([(Kid, output.ToString().Trim())]);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        native.OtherStored = new Item(Legacy(other.ExportPkcs8PrivateKey()), ["other reader"]);

        Assert.Equal(0, Issue(store));
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), created, LicenceRevocations.None).IsValid);
        Assert.Equal(0, native.Updates);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Issue_Twice_VerifiesWithTheOriginalKeyAndMigratesOnlyLegacyData(bool legacy, bool otherKeychain)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Item item = new Item(legacy ? Legacy(der) : der, ["previous app", "keeper"]);
        if (otherKeychain)
        {
            native.OtherStored = item;
        }
        else
        {
            native.Stored = item;
        }

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(0, Issue(store));
            Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
            Assert.Empty(error.ToString());
            Assert.Same(item, otherKeychain ? native.OtherStored : native.Stored);
            Assert.Equal(der, item.Data);
            Assert.Equal(["previous app", "keeper"], item.TrustedPaths);
            Assert.Equal(Item.OriginalPartition, item.Partition);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Issue_FailedMigration_PreservesTheItemAndLedgerForRetry(bool failRestoringPartition)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = Legacy(der);
        Item item = new Item(legacy, ["original app"]);
        native.Stored = item;
        native.RefuseUpdate = !failRestoringPartition;
        native.RefusePartition = failRestoringPartition;
        File.WriteAllText(Ledger, "previous entry\n");

        Assert.Equal(1, Issue(store));
        Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal("previous entry\n", File.ReadAllText(Ledger));
        Assert.Same(item, native.Stored);
        Assert.Equal(legacy, item.Data);
        Assert.Equal(["original app"], item.TrustedPaths);
        Assert.Equal(Item.OriginalPartition, item.Partition);
        AssertCleared(native.UpdateBuffer!);
        Assert.Equal(1, native.Frees);

        native.RefuseUpdate = false;
        native.RefusePartition = false;
        Assert.Equal(0, Issue(store));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Equal(der, item.Data);
        Assert.Equal(Item.OriginalPartition, item.Partition);
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

    public static TheoryData<string, bool> InvalidPkcs8Fields
    {
        get
        {
            TheoryData<string, bool> cases = [];
            foreach (string invalidField in new[] { "outer version", "algorithm", "named curve", "inner version",
                "inner curve", "attribute order", "attribute value order", "invalid UTF-8 attribute" })
            {
                cases.Add(invalidField, false);
                cases.Add(invalidField, true);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidPkcs8Fields))]
    public void Issue_InvalidPkcs8Field_LeavesKeyAccessAndLedgerIntact(string field, bool legacy)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] invalid = WithPkcs8Fields(key.ExportPkcs8PrivateKey(), field);
        byte[] original = legacy ? Legacy(invalid) : invalid;
        Item item = new Item(original, ["previous reader"]);
        native.Stored = item;
        File.WriteAllText(Ledger, "previous entry\n");

        Assert.Equal(1, Issue(store));
        Assert.Equal(Unreadable + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal("previous entry\n", File.ReadAllText(Ledger));
        Assert.Same(item, native.Stored);
        Assert.Equal(original, item.Data);
        Assert.Equal(["previous reader"], item.TrustedPaths);
        Assert.Equal(Item.OriginalPartition, item.Partition);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Creates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(1, native.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_SortedAttributes_PreservesTheirExactDer(bool legacy)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = WithPkcs8Fields(key.ExportPkcs8PrivateKey(), "sorted attributes");
        native.Stored = new Item(legacy ? Legacy(der) : der, [apphost]);
        Assert.Equal(der, store.Read(Kid));
        Assert.Equal(der, native.Stored.Data);
        Assert.Equal(legacy ? 1 : 0, native.Updates);
    }

    // The key and public point are always P-256. BER writer scopes let the test choose the
    // order of SET OF members, including encodings that a DER writer would silently sort.
    private static byte[] WithPkcs8Fields(byte[] der, string field)
    {
        AsnReader pkcs8 = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        pkcs8.ReadInteger();
        pkcs8.ReadSequence();
        AsnReader ec = new AsnReader(pkcs8.ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
        ec.ReadInteger();
        AsnWriter inner = new AsnWriter(AsnEncodingRules.DER);
        Asn1Tag parameters = new Asn1Tag(TagClass.ContextSpecific, 0);
        using (inner.PushSequence())
        {
            inner.WriteInteger(field == "inner version" ? 2 : 1);
            inner.WriteEncodedValue(ec.ReadEncodedValue().Span);
            if (ec.HasData && ec.PeekTag().HasSameClassAndValue(parameters))
            {
                ec.ReadEncodedValue();
            }

            if (field == "inner curve")
            {
                using (inner.PushSequence(parameters))
                {
                    inner.WriteObjectIdentifier(ECCurve.NamedCurves.nistP384.Oid.Value!);
                }
            }

            if (ec.HasData)
            {
                inner.WriteEncodedValue(ec.ReadEncodedValue().Span);
            }
        }

        AsnWriter writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(field == "outer version" ? 2 : 0);
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(field == "algorithm" ? "1.2.840.113549.1.1.1" : "1.2.840.10045.2.1");
                writer.WriteObjectIdentifier((field == "named curve" ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256).Oid.Value!);
            }

            writer.WriteOctetString(inner.Encode());
            using (writer.PushSetOf(parameters))
            {
                WriteAttribute(field == "attribute order" ? "1.2.3.2" : "1.2.3.1");
                WriteAttribute(field == "attribute order" ? "1.2.3.1" : "1.2.3.2");
            }
        }

        return writer.Encode();

        void WriteAttribute(string oid)
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(oid);
                using (writer.PushSetOf())
                {
                    writer.WriteInteger(field == "attribute value order" ? 2 : 1);
                    writer.WriteInteger(field == "attribute value order" ? 1 : 2);
                    if (field == "invalid UTF-8 attribute")
                    {
                        writer.WriteEncodedValue([0x0c, 0x01, 0xff]);
                    }
                }
            }
        }
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
        public const string OriginalPartition = "teamid:original-reader,teamid:keeper";
        public Item(byte[] data, string[] trustedPaths)
        {
            Data = data.ToArray();
            TrustedPaths = trustedPaths;
        }

        public byte[] Data { get; set; }
        public string[] TrustedPaths { get; }
        public string Partition { get; set; } = OriginalPartition;
    }

    // The second keychain comes first on the search list. A native add checks only its target database.
    private sealed class FakeKeychain : MacKeychainItem.INativeCalls
    {
        private readonly HashSet<IntPtr> held = [];
        private const int ItemRef = 1;
        private const int AccessRef = 2;
        private const int OtherItemRef = 3;
        private const int DefaultKeychain = 4;
        private const int OtherKeychain = 5;
        private const int PartitionRef = 6;
        private IntPtr writing;
        private string? savedPartition;
        private IntPtr readPointer;
        private int readLength;
        private string? trustedPath;

        public Item? Stored { get; set; }
        public Item? OtherStored { get; set; }
        public Action? AfterMissingLookup { get; set; }
        public Action? AfterAdd { get; set; }
        public bool RefuseUpdate { get; set; }
        public bool RefusePartition { get; set; }
        public int Finds { get; private set; }
        public int DataRequests { get; private set; }
        public int AccessCreations { get; private set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Deletes { get; private set; }
        public int Frees { get; private set; }
        public byte[]? CreateBuffer { get; private set; }
        public byte[]? UpdateBuffer { get; private set; }

        public int Find(IntPtr keychain, string service, string account, IntPtr length, IntPtr data, out IntPtr item)
        {
            Assert.Equal(Service, service);
            Assert.Equal(Kid, account);
            Assert.Equal(length == IntPtr.Zero, data == IntPtr.Zero);
            if (data != IntPtr.Zero)
            {
                DataRequests++;
            }

            Assert.True(keychain == IntPtr.Zero || keychain == DefaultKeychain || keychain == OtherKeychain);
            Finds++;
            item = IntPtr.Zero;
            bool other = keychain == OtherKeychain || (keychain == IntPtr.Zero && OtherStored is not null);
            Item? found = other ? OtherStored : Stored;
            if (found is null)
            {
                if (length == IntPtr.Zero && keychain == IntPtr.Zero)
                {
                    AfterMissingLookup?.Invoke();
                }

                return -25300;
            }

            item = other ? OtherItemRef : ItemRef;
            Assert.True(held.Add(item));
            if (data != IntPtr.Zero)
            {
                readLength = found.Data.Length;
                Marshal.WriteInt32(length, readLength);
                readPointer = Marshal.AllocHGlobal(Math.Max(1, readLength));
                Marshal.Copy(found.Data, 0, readPointer, readLength);
                Marshal.WriteIntPtr(data, readPointer);
            }

            return 0;
        }

        public int CopyDefaultKeychain(out IntPtr keychain)
        {
            keychain = DefaultKeychain;
            return 0;
        }

        public int CopyItemKeychain(IntPtr item, out IntPtr keychain)
        {
            Assert.Contains(item, held);
            keychain = item == ItemRef ? DefaultKeychain : OtherKeychain;
            return 0;
        }

        public IEnumerable<IntPtr> OtherKeychains(IntPtr target)
        {
            Assert.Equal((IntPtr)DefaultKeychain, target);
            return [(IntPtr)OtherKeychain];
        }

        public bool WriteTransaction(IntPtr keychain, Func<bool> change)
        {
            Assert.Equal(IntPtr.Zero, writing);
            writing = keychain;
            Item? original = keychain == DefaultKeychain ? Stored : OtherStored;
            byte[]? originalData = original?.Data;
            string? partition = original?.Partition;
            bool commit = false;
            try
            {
                commit = change();
                return commit;
            }
            finally
            {
                if (!commit)
                {
                    if (keychain == DefaultKeychain)
                    {
                        Stored = original;
                    }
                    else
                    {
                        OtherStored = original;
                    }

                    if (original is not null)
                    {
                        original.Data = originalData!;
                        original.Partition = partition!;
                    }
                }

                writing = IntPtr.Zero;
            }
        }

        public IntPtr CopyPartition(IntPtr item)
        {
            savedPartition = WritableItem(item).Partition;
            Assert.True(held.Add(PartitionRef));
            return PartitionRef;
        }

        public void PreservePartition(IntPtr item, IntPtr original)
        {
            Assert.Equal((IntPtr)PartitionRef, original);
            Assert.Contains(original, held);
            if (RefusePartition)
            {
                throw new TokenStoreException("Access denied");
            }

            WritableItem(item).Partition = savedPartition!;
        }

        private Item WritableItem(IntPtr item)
        {
            Assert.Contains(item, held);
            Assert.Equal(item == ItemRef ? (IntPtr)DefaultKeychain : OtherKeychain, writing);
            return item == ItemRef ? Stored! : OtherStored!;
        }

        public IntPtr CreateAccess(string path, string description)
        {
            Assert.Equal(Service, description);
            Assert.True(held.Add(AccessRef));
            AccessCreations++;
            trustedPath = path;
            return AccessRef;
        }

        public int CreateFromContent(uint itemClass, ref MacKeychainItem.AttributeList attributes, byte[] data,
            IntPtr keychain, IntPtr access, out IntPtr item)
        {
            Assert.Equal(0x67656e70u, itemClass); // kSecGenericPasswordItemClass
            Assert.Equal((IntPtr)DefaultKeychain, keychain);
            Assert.Equal(keychain, writing);
            Assert.Equal((IntPtr)AccessRef, access);
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
            item = ItemRef;
            Assert.True(held.Add(item));
            AfterAdd?.Invoke();
            return 0;
        }

        public int ModifyAttributesAndData(IntPtr item, IntPtr attributes, ReadOnlyMemory<byte> data)
        {
            Item updated = WritableItem(item);
            Assert.Equal(IntPtr.Zero, attributes);
            Updates++;
            Assert.True(MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment));
            UpdateBuffer = segment.Array;
            if (RefuseUpdate)
            {
                return -1;
            }

            updated.Data = data.ToArray();
            updated.Partition = "teamid:writer";
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

        public void Release(IntPtr reference)
        {
            if (reference != DefaultKeychain && reference != OtherKeychain && reference != IntPtr.Zero)
            {
                Assert.True(held.Remove(reference));
            }
        }
    }
}
