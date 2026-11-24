using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using Times.LicenceIssuer;

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    [Fact]
    public void CreateKey_CommittedSecondaryInsertAfterCachedMiss_RefusesPublication()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SigningKeyStoreTests.Item competing = new(key.ExportPkcs8PrivateKey(), ["competing reader"]);
        fixture.Calls.CacheOtherMiss = true;
        fixture.Calls.AfterMissingLookup = () => fixture.Calls.OtherStored = competing;
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("already exists", error.ToString(), StringComparison.Ordinal);
        Assert.True(fixture.Calls.CheckedOtherUnderLock);
        Assert.Same(competing, fixture.Calls.OtherStored);
        Assert.Equal(["competing reader"], competing.TrustedPaths);
        Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, competing.Partition);
        Assert.Null(fixture.Calls.Stored);
        Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Issue_PreservesPublishedKeyAndHistoricalSearchOrder(bool createdHere)
    {
        using PortableFixture fixture = new();
        using ECDsa earlier = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa later = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem;
        if (createdHere)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(0, fixture.Create(output, error));
            Assert.Empty(error.ToString());
            pem = output.ToString().Trim();
        }
        else
        {
            fixture.Calls.Stored = new SigningKeyStoreTests.Item(earlier.ExportPkcs8PrivateKey(), ["default reader"]);
            pem = later.ExportSubjectPublicKeyInfoPem();
        }

        SigningKeyStoreTests.Item duplicate = new(later.ExportPkcs8PrivateKey(), ["higher-priority reader"]);
        fixture.Calls.OtherStored = duplicate;
        // A fresh issuer process has no in-memory record of the earlier create.
        MacKeychainSigningKeyStore reopened = new(fixture.Apphost, new MacKeychainItem(fixture.Calls));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, pem)]);
        AssertIssued(reopened, fixture.Ledger, original);
        AssertIssued(reopened, fixture.Ledger, original);
        Assert.Same(duplicate, fixture.Calls.OtherStored);
        Assert.Equal(["higher-priority reader"], duplicate.TrustedPaths);
        Assert.Equal(0, fixture.Calls.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneTimeRead_WithDifferentStoredReader_PreservesAccess(bool denyWrite)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        SigningKeyStoreTests.Item item = new(legacy, ["original reader"]) { Partition = "teamid:original-reader" };
        fixture.Calls.Stored = item;
        fixture.Calls.RemainingReads = 1;
        fixture.Calls.RefuseUpdate = denyWrite;
        File.WriteAllText(fixture.Ledger, "previous entry\n");
        try
        {
            if (denyWrite)
            {
                using StringWriter output = new();
                using StringWriter error = new();
                Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
                Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
                Assert.Empty(output.ToString());
                Assert.Equal("previous entry\n", File.ReadAllText(fixture.Ledger));
            }
            else
            {
                AssertIssued(fixture.Store, fixture.Ledger, LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]));
            }

            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(denyWrite ? legacy : der, item.Data));
            Assert.Equal(["original reader"], item.TrustedPaths);
            Assert.Equal("teamid:original-reader", item.Partition);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Fact]
    public void Issue_TwoPublishedKeysAfterAChangeOfDefault_RefusesBeforeReadingEither()
    {
        using PortableFixture fixture = new();
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(0, fixture.Create(output, error));
        string firstPem = output.ToString();
        SigningKeyStoreTests.Item first = Assert.IsType<SigningKeyStoreTests.Item>(fixture.Calls.Stored);
        fixture.Calls.Stored = null; // the first database is offline for the second creation
        fixture.Calls.DefaultIsOther = true;
        output.GetStringBuilder().Clear();
        Assert.Equal(0, fixture.Create(output, error));
        Assert.NotEqual(firstPem, output.ToString());
        fixture.Calls.Stored = first;
        File.WriteAllText(fixture.Ledger, "previous entry\n");
        foreach (bool otherDefault in new[] { true, false })
        {
            fixture.Calls.DefaultIsOther = otherDefault;
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.Contains("Multiple published signing keys", error.ToString(), StringComparison.Ordinal);
            Assert.Empty(output.ToString());
            Assert.Equal("previous entry\n", File.ReadAllText(fixture.Ledger));
        }

        Assert.Equal(0, fixture.Calls.DataRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateKey_UnavailableDestinationOrStalledProbe_ReleasesLocksAndPublishesNothing(bool stall)
    {
        using PortableFixture fixture = new();
        if (stall)
        {
            // This child cannot finish until the parent closes stdin. A zero deadline
            // exercises the production timeout independent of process startup speed.
            ProcessStartInfo info = new(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/cat")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows())
            {
                foreach (string argument in new[] { "/d", "/q", "/c", "set /p fixture=" })
                {
                    info.ArgumentList.Add(argument);
                }
            }

            fixture.Calls.DuringProbe = () =>
            {
                using Process child = Process.Start(info)!;
                try
                {
                    MacKeychainItem.NativeCalls.WaitForProbe(child, TimeSpan.Zero);
                }
                finally
                {
                    Assert.True(child.HasExited, "The timed-out probe was still running.");
                }
            };
        }
        else
        {
            fixture.Calls.DestinationUnavailable = true;
        }

        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Empty(output.ToString());
        Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
        Assert.Null(fixture.Calls.Stored);
        Assert.Null(fixture.Calls.OtherStored);
        Assert.Equal(0, fixture.Calls.Creates);
        fixture.Calls.AssertReleased();
    }

    public static TheoryData<string, byte[], bool, bool> AttributeCases
    {
        get
        {
            TheoryData<string, byte[], bool, bool> cases = [];
            foreach (bool legacy in new[] { false, true })
            {
                // The two example attribute types have a single REAL or EXTERNAL value.
                cases.Add("1.3.6.1.4.1.32473.1", [0x09, 0], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.1", [0x09, 3, 0x80, 0, 3], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.1", [0x09, 1, 0x80], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.2", [0x28, 9, 6, 2, 42, 3, 0xa0, 3, 2, 1, 0], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.2", [0x28, 0], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.2", [0x28, 4, 6, 2, 42, 3], false, legacy);
                // Ordinary SET fields are sorted by class and tag number, ignoring the constructed bit.
                cases.Add("1.3.6.1.4.1.32473.3", [0x31, 5, 0x30, 0, 0x13, 1, 65], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.4", [0x31, 5, 0xa0, 0, 0x81, 1, 1], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 2, 0x1b, 0x28], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 1, 65], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "20240229"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "20230229"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 10, .. "2024-02-29"u8], false, legacy);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(AttributeCases))]
    public void Pkcs8Attribute_IssuesExactDerOrLeavesOriginalIntact(string oid, byte[] value, bool valid, bool legacy)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = WithAttribute(key, oid, value);
        byte[] stored = legacy ? EncodeLegacy(der) : der;
        SigningKeyStoreTests.Item item = new(stored, ["example reader"]);
        fixture.Calls.Stored = item;
        File.WriteAllText(fixture.Ledger, "previous entry\n");
        try
        {
            if (valid)
            {
                LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
                AssertIssued(fixture.Store, fixture.Ledger, original);
                AssertIssued(fixture.Store, fixture.Ledger, original);
                Assert.Equal(3, File.ReadAllLines(fixture.Ledger).Length);
            }
            else
            {
                using StringWriter output = new();
                using StringWriter error = new();
                Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
                Assert.Equal("The stored signing key is not readable." + error.NewLine, error.ToString());
                Assert.Empty(output.ToString());
                Assert.Equal("previous entry\n", File.ReadAllText(fixture.Ledger));
            }

            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(valid ? der : stored, item.Data));
            Assert.Equal(["example reader"], item.TrustedPaths);
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            Assert.Equal(valid && legacy ? 1 : 0, fixture.Calls.Updates);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pkcs8WithoutOptionalPublicPoint_IssuesWithOriginalKey(bool legacy)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] scalar = key.ExportParameters(true).D!;
        AsnWriter writer = new(AsnEncodingRules.DER);
        byte[]? der = null;
        byte[]? stored = null;
        try
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(0);
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("1.2.840.10045.2.1");
                    writer.WriteObjectIdentifier(ECCurve.NamedCurves.nistP256.Oid.Value!);
                }

                using (writer.PushOctetString())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    writer.WriteOctetString(scalar);
                }
            }

            der = writer.Encode();
            stored = legacy ? EncodeLegacy(der) : der;
            fixture.Calls.Stored = new SigningKeyStoreTests.Item(stored, ["original reader"]);
            AssertIssued(fixture.Store, fixture.Ledger, LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]));
            Assert.True(CryptographicOperations.FixedTimeEquals(der, fixture.Calls.Stored.Data));
        }
        finally
        {
            writer.Reset();
            CryptographicOperations.ZeroMemory(scalar);
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    private static byte[] WithAttribute(ECDsa key, string oid, byte[] value)
    {
        byte[] source = key.ExportPkcs8PrivateKey();
        AsnWriter writer = new(AsnEncodingRules.DER);
        try
        {
            AsnReader fields = new AsnReader(source, AsnEncodingRules.DER).ReadSequence();
            using (writer.PushSequence())
            {
                while (fields.HasData)
                {
                    writer.WriteEncodedValue(fields.ReadEncodedValue().Span);
                }

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(oid);
                    using (writer.PushSetOf())
                    {
                        writer.WriteEncodedValue(value);
                    }
                }
            }

            return writer.Encode();
        }
        finally
        {
            writer.Reset();
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private static byte[] EncodeLegacy(byte[] der)
    {
        byte[] encoded = new byte[Base64.GetMaxEncodedToUtf8Length(der.Length)];
        Assert.Equal(OperationStatus.Done, Base64.EncodeToUtf8(der, encoded, out _, out _));
        return encoded;
    }

    private sealed class PortableFixture : IDisposable
    {
        private readonly TempFolder folder = new();
        public SigningKeyStoreTests.FakeKeychain Calls { get; } = new();
        public string Ledger => Path.Combine(folder.Path, "ledger.tsv");
        public string Apphost => Path.Combine(folder.Path, "Times.LicenceIssuer");
        public MacKeychainSigningKeyStore Store { get; }

        public PortableFixture()
        {
            File.WriteAllBytes(Apphost, []);
            Store = new MacKeychainSigningKeyStore(Apphost, new MacKeychainItem(Calls));
        }

        public int Create(TextWriter output, TextWriter error)
        {
            using LicenceIssuerTests.CountingRandom random = new();
            return new Issuer(output, error, Store, random, FixedTimeProvider.At20261027(), Ledger)
                .Run(["create-key", "--kid", Account]);
        }

        public void Dispose()
        {
            folder.Dispose();
            Calls.AssertReleased();
        }
    }
}
