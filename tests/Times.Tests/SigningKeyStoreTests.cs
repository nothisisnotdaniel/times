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

    public void Dispose()
    {
        folder.Dispose();
        native.AssertReleased();
    }

    private string Ledger => Path.Combine(folder.Path, "ledger.tsv");

    private MacKeychainSigningKeyStore StoreFor(string? path) => new(path, new MacKeychainItem(native));

    private int Run(ISigningKeyStore keys, params string[] args)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        return Run(keys, output, error, args);
    }

    private int Run(ISigningKeyStore keys, TextWriter stdout, TextWriter stderr, params string[] args)
    {
        using LicenceIssuerTests.CountingRandom random = new LicenceIssuerTests.CountingRandom();
        return new Issuer(stdout, stderr, keys, random, FixedTimeProvider.At20261027(), Ledger).Run(args);
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
        Assert.Equal(0, native.Creates);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(0, native.Frees);
        Assert.Equal(0, native.DataRequests);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("after lookup")]
    [InlineData("before secondary lock")]
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
            case "before secondary lock":
                native.BeforeOtherLock = () => native.OtherStored = existing;
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
    public void CreateKey_OverlappingDefaults_OnlyOneIssuerPrintsItsPublicPem()
    {
        using StringWriter winner = new StringWriter();
        using StringWriter winnerError = new StringWriter();
        native.AfterSnapshot = () =>
        {
            native.DefaultIsOther = true;
            Assert.Equal(0, Run(StoreFor(apphost), winner, winnerError, "create-key", "--kid", Kid));
            native.DefaultIsOther = false;
        };

        Assert.Equal(1, Run(store, "create-key", "--kid", Kid));
        Assert.Empty(output.ToString());
        Assert.Contains("already exists", error.ToString());
        Assert.Empty(winnerError.ToString());
        Assert.Null(native.Stored);
        Item only = Assert.IsType<Item>(native.OtherStored);
        Assert.Equal([apphost], only.TrustedPaths);
        using ECDsa created = ECDsa.Create();
        created.ImportPkcs8PrivateKey(only.Data, out _);
        Assert.Equal(created.ExportSubjectPublicKeyInfoPem() + winner.NewLine, winner.ToString());
        Assert.Equal(1, native.Creates);
        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal([4, 5, 4, 5], native.LockOrder);
    }

    [Fact]
    public void CreateKey_SecondaryWriterCannotCommitAfterItsLastLookupUntilTargetIsCommitted()
    {
        bool attempted = false;
        native.AtCreateCommit = () =>
        {
            // A file-Keychain writer needs its database lock to commit. An unlocked
            // secondary here would allow both IDs to become durable.
            Assert.True(native.OtherIsLocked);
            Assert.True(native.CheckedOtherUnderLock);
            attempted = true;
        };
        Assert.Equal(0, Run(store, "create-key", "--kid", Kid));
        string pem = output.ToString();
        Assert.True(attempted);
        native.DefaultIsOther = true;
        Assert.Equal(1, Run(store, "create-key", "--kid", Kid));
        Assert.Empty(output.ToString());
        Assert.Null(native.OtherStored);
        using ECDsa only = ECDsa.Create();
        only.ImportPkcs8PrivateKey(native.Stored!.Data, out _);
        Assert.Equal(only.ExportSubjectPublicKeyInfoPem() + output.NewLine, pem);
    }

    [Fact]
    public void Issue_TwoExistingIds_UsesThePreviouslySelectedKeychain()
    {
        using ECDsa fallback = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] untouched = fallback.ExportPkcs8PrivateKey();
        native.Stored = new Item(untouched, ["default reader"]);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = other.ExportPkcs8PrivateKey();
        native.OtherStored = new Item(Legacy(der), ["other reader"]);
        LicencePublicKeys previous = LicencePublicKeys.FromTable([(Kid, other.ExportSubjectPublicKeyInfoPem())]);

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(0, Issue(store));
            Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), previous, LicenceRevocations.None).IsValid);
        }

        Assert.Equal(1, native.Updates);
        Assert.Equal(der, native.OtherStored.Data);
        Assert.Equal(["other reader"], native.OtherStored.TrustedPaths);
        Assert.Equal(untouched, native.Stored.Data);
        Assert.Equal(["default reader"], native.Stored.TrustedPaths);
    }

    [Fact]
    public void Issue_NewKeyInUnlistedDefault_IsStillReadable()
    {
        native.DefaultNotSearched = true;
        Assert.Equal(0, Run(store, "create-key", "--kid", Kid));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, output.ToString().Trim())]);
        Assert.Equal(0, Issue(store));
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Equal(0, native.Updates);
    }

    [Fact]
    public void CreateKey_UnavailableSecondaryLock_RefusesWithoutPublishingOrWriting()
    {
        native.BeforeOtherLock = () => throw new TokenStoreException("The item could not be saved in the macOS Keychain.");
        Assert.Equal(1, Run(store, "create-key", "--kid", Kid));
        Assert.Empty(output.ToString());
        Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
        Assert.Null(native.Stored);
        Assert.Null(native.OtherStored);
        Assert.Equal(0, native.Creates);
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
        Assert.Equal(legacy ? 4 : 3, native.Frees);
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
    public void Issue_FailedMigration_PreservesTheItemAndLedgerForRetry(bool failCommitting)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = Legacy(der);
        Item item = new Item(legacy, ["original app"]);
        native.Stored = item;
        native.RefuseUpdate = !failCommitting;
        native.RefuseCommit = failCommitting;
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
        Assert.Equal(2, native.Frees);

        native.RefuseUpdate = false;
        native.RefuseCommit = false;
        Assert.Equal(0, Issue(store));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Equal(der, item.Data);
        Assert.Equal(Item.OriginalPartition, item.Partition);
        Assert.Equal(2, native.Updates);
        Assert.Equal(4, native.Frees);
        Assert.Equal(2, File.ReadAllLines(Ledger).Length);
    }

    [Fact]
    public void Issue_AnotherIssuerMigratesAfterRead_UpdatesOnceAndBothLicencesVerify()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        native.Stored = new Item(Legacy(der), [apphost]);
        using StringWriter first = new StringWriter();
        using StringWriter firstError = new StringWriter();
        native.BeforeOwnerLock = () => Assert.Equal(0, Run(StoreFor(apphost), first, firstError,
            "issue", "--kid", Kid, "--name", "Bob", "--until", "2027-12-31"));

        Assert.Equal(0, Issue(store));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Kid, key.ExportSubjectPublicKeyInfoPem())]);
        Assert.True(LicenceKey.TryVerify(first.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
        Assert.Empty(firstError.ToString());
        Assert.Empty(error.ToString());
        Assert.Equal(2, File.ReadAllLines(Ledger).Length);
        Assert.Equal(1, native.Updates);
        Assert.Equal(der, native.Stored.Data);
        Assert.Equal([apphost], native.Stored.TrustedPaths);
        Assert.Equal(Item.OriginalPartition, native.Stored.Partition);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("replaced")]
    [InlineData("deleted")]
    [InlineData("additional partition")]
    public void Issue_StaleOrUnpreservableItem_RefusesBeforeWriting(string change)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] legacy = Legacy(key.ExportPkcs8PrivateKey());
        byte[] expected = change == "changed" ? newKey.ExportPkcs8PrivateKey() : legacy;
        Item original = new Item(legacy, [apphost]);
        Item? current = change switch
        {
            "deleted" => null,
            "replaced" => new Item(legacy, ["replacement reader"]),
            _ => original,
        };
        string partition = change == "additional partition" ? Item.OriginalPartition + ",teamid:keeper" : Item.OriginalPartition;
        native.Stored = original;
        native.BeforeOwnerLock = () =>
        {
            native.Stored = current;
            if (current is not null)
            {
                current.Data = expected;
                current.Partition = partition;
            }
        };
        File.WriteAllText(Ledger, "previous entry\n");

        Assert.Equal(1, Issue(store));
        Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal("previous entry\n", File.ReadAllText(Ledger));
        Assert.Same(current, native.Stored);
        if (current is not null)
        {
            Assert.Equal(expected, current.Data);
            Assert.Equal(partition, current.Partition);
            Assert.Equal(change == "replaced" ? ["replacement reader"] : new[] { apphost }, current.TrustedPaths);
        }

        Assert.Equal(0, native.Updates);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(0, native.Creates);
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
        Assert.Equal(Unreadable, Assert.Throws<TokenStoreException>(() => MacKeychainSigningKeyStore.DecodeLegacy(partial)).Message);
        AssertCleared(partial);
    }

    public static TheoryData<string, bool> InvalidPkcs8Fields
    {
        get
        {
            TheoryData<string, bool> cases = [];
            foreach (string invalidField in new[] { "outer version", "algorithm", "named curve", "inner version",
                "inner curve", "zero scalar", "scalar order", "scalar above order", "different public point",
                "attribute order", "attribute value order", "invalid UTF-8 attribute", "odd BMPString", "BMP surrogate",
                "non-ASCII IA5String", "non-numeric string", "invalid PrintableString", "invalid UniversalString" })
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
            byte[] scalar = ec.ReadOctetString();
            if (field == "zero scalar")
            {
                Array.Clear(scalar);
            }
            else if (field == "scalar order" || field == "scalar above order")
            {
                scalar = Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
                if (field == "scalar above order")
                {
                    scalar[^1]++;
                }
            }

            inner.WriteOctetString(scalar);
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

            if (field == "different public point")
            {
                using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ECPoint point = other.ExportParameters(false).Q;
                using (inner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                {
                    inner.WriteBitString([4, .. point.X!, .. point.Y!]);
                }
            }
            else if (ec.HasData)
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
                    byte[] value = field switch
                    {
                        "invalid UTF-8 attribute" => [0x0c, 1, 0xff],
                        "odd BMPString" => [0x1e, 1, 1],
                        "BMP surrogate" => [0x1e, 2, 0xd8, 0x01],
                        "non-ASCII IA5String" => [0x16, 1, 0xff],
                        "non-numeric string" => [0x12, 1, (byte)'A'],
                        "invalid PrintableString" => [0x13, 1, (byte)'@'],
                        "invalid UniversalString" => [0x1c, 4, 0, 0x11, 0, 0],
                        _ => [],
                    };
                    if (value.Length != 0)
                    {
                        writer.WriteEncodedValue(value);
                    }
                    else if (field == "sorted attributes")
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, "maker");
                        writer.WriteCharacterString(UniversalTagNumber.BMPString, "A\u2713");
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
        public const string OriginalPartition = "teamid:writer";
        public Item(byte[] data, string[] trustedPaths)
        {
            Data = data.ToArray();
            TrustedPaths = trustedPaths;
        }

        public byte[] Data { get; set; }
        public string[] TrustedPaths { get; }
        public string Partition { get; set; } = OriginalPartition;
        public IntPtr Reference { get; set; }
    }

    // Search order is independent of the default. Locks, references and outstanding native
    // allocations are tracked separately so two issuer calls can overlap at explicit boundaries.
    private sealed class FakeKeychain : MacKeychainItem.INativeCalls
    {
        private const int AccessRef = 2;
        private const int DefaultKeychain = 4;
        private const int OtherKeychain = 5;
        private readonly List<IntPtr> held = [];
        private readonly HashSet<IntPtr> locked = [];
        private readonly Dictionary<IntPtr, IntPtr> owners = [];
        private readonly Dictionary<IntPtr, int> buffers = [];
        private int nextReference = 10;
        private string? trustedPath;
        private bool creating;

        public Item? Stored { get; set; }
        public Item? OtherStored { get; set; }
        public bool DefaultIsOther { get; set; }
        public bool DefaultNotSearched { get; set; }
        public Action? AfterMissingLookup { get; set; }
        public Action? AfterSnapshot { get; set; }
        public Action? BeforeOtherLock { get; set; }
        public Action? BeforeOwnerLock { get; set; }
        public Action? AtCreateCommit { get; set; }
        public bool OtherIsLocked => locked.Contains(OtherKeychain);
        public bool CheckedOtherUnderLock { get; private set; }
        public List<int> LockOrder { get; } = [];
        public bool RefuseUpdate { get; set; }
        public bool RefuseCommit { get; set; }
        public int Finds { get; private set; }
        public int DataRequests { get; private set; }
        public int AccessCreations { get; private set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Deletes { get; private set; }
        public int Frees { get; private set; }
        public byte[]? CreateBuffer { get; private set; }
        public byte[]? UpdateBuffer { get; private set; }

        public void AssertReleased()
        {
            Assert.Empty(held);
            Assert.Empty(locked);
            Assert.Empty(buffers);
        }

        public int Find(IntPtr keychain, string service, string account, IntPtr length, IntPtr data, out IntPtr item)
        {
            Assert.Equal(Service, service);
            Assert.Equal(Kid, account);
            Assert.Equal(length == IntPtr.Zero, data == IntPtr.Zero);
            Assert.True(keychain == IntPtr.Zero || keychain == DefaultKeychain || keychain == OtherKeychain);
            Finds++;
            if (data != IntPtr.Zero)
            {
                DataRequests++;
            }
            else if (keychain == OtherKeychain && OtherIsLocked)
            {
                CheckedOtherUnderLock = true;
            }

            bool other = keychain == OtherKeychain || (keychain == IntPtr.Zero && (OtherStored is not null || DefaultNotSearched));
            Item? found = other ? OtherStored : Stored;
            if (found is null)
            {
                item = IntPtr.Zero;
                if (data == IntPtr.Zero && keychain == IntPtr.Zero)
                {
                    AfterMissingLookup?.Invoke();
                }

                return -25300;
            }

            item = Retain(found, other ? OtherKeychain : DefaultKeychain);
            if (data != IntPtr.Zero)
            {
                int size = found.Data.Length;
                IntPtr pointer = Marshal.AllocHGlobal(Math.Max(1, size));
                buffers.Add(pointer, size);
                Marshal.Copy(found.Data, 0, pointer, size);
                Marshal.WriteIntPtr(data, pointer);
                Marshal.WriteInt32(length, size);
            }

            return 0;
        }

        private IntPtr Retain(Item item, IntPtr owner)
        {
            if (item.Reference == IntPtr.Zero)
            {
                item.Reference = ++nextReference;
                owners.Add(item.Reference, owner);
            }

            held.Add(item.Reference);
            return item.Reference;
        }

        public int CopyDefaultKeychain(out IntPtr keychain)
        {
            keychain = DefaultIsOther ? OtherKeychain : DefaultKeychain;
            held.Add(keychain);
            return 0;
        }

        public int CopyItemKeychain(IntPtr item, out IntPtr keychain)
        {
            Assert.Contains(item, held);
            keychain = owners[item];
            held.Add(keychain);
            return 0;
        }

        public bool WithSearchList(IntPtr target, Func<IReadOnlyList<IntPtr>, bool> action)
        {
            Action? scheduled = AfterSnapshot;
            AfterSnapshot = null;
            scheduled?.Invoke();
            return action([target == DefaultKeychain ? OtherKeychain : DefaultKeychain]);
        }

        public byte[] KeychainPath(IntPtr keychain) => [(byte)keychain];

        public IDisposable LockForLookup(IntPtr keychain, byte[] path)
        {
            Assert.Equal(KeychainPath(keychain), path);
            return BeginTransaction(keychain);
        }

        public MacKeychainItem.IFileTransaction BeginTransaction(IntPtr keychain)
        {
            if (keychain == OtherKeychain)
            {
                BeforeOtherLock?.Invoke();
            }

            Action? scheduled = BeforeOwnerLock;
            BeforeOwnerLock = null;
            scheduled?.Invoke();
            Assert.True(locked.Add(keychain));
            LockOrder.Add((int)keychain);
            return new Transaction(this, keychain);
        }

        private sealed class Transaction : MacKeychainItem.IFileTransaction
        {
            private readonly FakeKeychain db;
            private readonly IntPtr owner;
            private readonly Item? before;
            private readonly byte[]? beforeData;
            private readonly string? beforePartition;
            private bool committed;

            public Transaction(FakeKeychain db, IntPtr owner)
            {
                this.db = db;
                this.owner = owner;
                before = db.GetItem(owner);
                beforeData = before?.Data;
                beforePartition = before?.Partition;
            }

            public void Commit()
            {
                if (db.creating)
                {
                    Assert.Contains(DefaultKeychain, db.locked);
                    Assert.Contains(OtherKeychain, db.locked);
                    db.AtCreateCommit?.Invoke();
                    db.creating = false;
                }

                if (db.RefuseCommit)
                {
                    throw new TokenStoreException("Commit failed");
                }

                committed = true;
            }

            public void Dispose()
            {
                if (!committed)
                {
                    db.SetItem(owner, before);
                    if (before is not null)
                    {
                        before.Data = beforeData!;
                        before.Partition = beforePartition!;
                    }
                }

                Assert.True(db.locked.Remove(owner));
            }
        }

        private Item? GetItem(IntPtr owner) => owner == DefaultKeychain ? Stored : OtherStored;

        private void SetItem(IntPtr owner, Item? item)
        {
            if (owner == DefaultKeychain)
            {
                Stored = item;
            }
            else
            {
                OtherStored = item;
            }
        }

        public int CopyRecordId(IntPtr item, out IntPtr recordId)
        {
            Assert.Contains(item, held);
            recordId = -item;
            held.Add(recordId);
            return 0;
        }

        public bool Equal(IntPtr first, IntPtr second) => first == second;

        public void CheckAccessForUpdate(IntPtr owner, IntPtr item)
        {
            Assert.Equal(owner, owners[item]);
            if (WritableItem(item).Partition != Item.OriginalPartition)
            {
                throw new TokenStoreException("Access would change");
            }
        }

        private Item WritableItem(IntPtr item)
        {
            Assert.Contains(item, held);
            IntPtr owner = owners[item];
            Assert.Contains(owner, locked);
            Item current = Assert.IsType<Item>(GetItem(owner));
            Assert.Equal(current.Reference, item);
            return current;
        }

        public IntPtr CreateAccess(string path, string description)
        {
            Assert.Equal(Service, description);
            held.Add(AccessRef);
            AccessCreations++;
            trustedPath = path;
            return AccessRef;
        }

        public int CreateFromContent(uint itemClass, ref MacKeychainItem.AttributeList attributes, byte[] data,
            IntPtr keychain, IntPtr access, out IntPtr item)
        {
            Assert.Equal(0x67656e70u, itemClass);
            Assert.Contains(keychain, locked);
            Assert.Equal((IntPtr)AccessRef, access);
            Assert.Contains(access, held);
            Dictionary<uint, string> values = [];
            for (int i = 0; i < attributes.Count; i++)
            {
                IntPtr pointer = attributes.Attributes + i * Marshal.SizeOf<MacKeychainItem.Attribute>();
                MacKeychainItem.Attribute attribute = Marshal.PtrToStructure<MacKeychainItem.Attribute>(pointer);
                values.Add(attribute.Tag, Marshal.PtrToStringUTF8(attribute.Data, (int)attribute.Length)!);
            }

            Assert.Equal(Service, values[0x73766365]);
            Assert.Equal(Kid, values[0x61636374]);
            Assert.Equal(Service, values[0x6c61626c]);
            Creates++;
            CreateBuffer = data;
            if (GetItem(keychain) is not null)
            {
                item = IntPtr.Zero;
                return -25299;
            }

            Item created = new Item(data, [trustedPath!]);
            SetItem(keychain, created);
            item = Retain(created, keychain);
            creating = true;
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
            updated.Partition = Item.OriginalPartition; // macOS assigns the writer's partition
            return 0;
        }

        public int Delete(IntPtr item)
        {
            Deletes++;
            throw new InvalidOperationException("Signing keys must never be deleted.");
        }

        public void FreeContent(IntPtr data)
        {
            Assert.True(buffers.Remove(data, out int length));
            try
            {
                byte[] atRelease = new byte[length];
                Marshal.Copy(data, atRelease, 0, length);
                Assert.False(atRelease.AsSpan().ContainsAnyExcept((byte)0));
                Frees++;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        public void Release(IntPtr reference)
        {
            if (reference != IntPtr.Zero)
            {
                Assert.True(held.Remove(reference));
            }
        }
    }
}
