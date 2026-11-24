using System.Buffers;
using System.Buffers.Text;
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
        byte[] original = RandomNumberGenerator.GetBytes(32);
        SigningKeyStoreTests.Item competing = new(original, ["competing reader"]);
        fixture.Calls.CacheOtherMiss = true;
        fixture.Calls.AfterMissingLookup = () => fixture.Calls.OtherStored = competing;
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("already exists", error.ToString(), StringComparison.Ordinal);
        Assert.True(fixture.Calls.CheckedOtherUnderLock);
        Assert.Same(competing, fixture.Calls.OtherStored);
        Assert.True(CryptographicOperations.FixedTimeEquals(original, competing.Data));
        Assert.Equal(["competing reader"], competing.TrustedPaths);
        Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, competing.Partition);
        Assert.Null(fixture.Calls.Stored);
        Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
    }

    [Theory]
    [InlineData("reader-list")]
    [InlineData("malformed-legacy")]
    [InlineData("oversized-protected-legacy")]
    [InlineData("fresh-view")]
    [InlineData("revision")]
    public void RefusedPolicy_PrecedesAllPrivateKeyDecoding(string reason)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] stored = reason == "oversized-protected-legacy" ? Enumerable.Repeat((byte)'M', 16385).ToArray() : EncodeLegacy(der);
        try
        {
            bool multiReader = reason is "reader-list" or "malformed-legacy" or "oversized-protected-legacy";
            if (reason == "malformed-legacy")
            {
                stored[^1] = (byte)'!';
            }
            string[] readers = multiReader ? ["first reader", "second reader"] : ["original reader"];
            SigningKeyStoreTests.Item original = new(stored, readers);
            fixture.Calls.Stored = original;
            if (reason == "fresh-view")
            {
                fixture.Calls.CommittedReadView = new(stored, ["different reader"]);
            }
            if (reason == "revision")
            {
                fixture.Calls.AfterCopyContent = () => fixture.Calls.DatabaseVersion++;
            }
            int keyUses = 0;
            MacKeychainItem items = new(fixture.Calls);
            TokenStoreException refused = Assert.Throws<TokenStoreException>(() =>
                items.ReadAndMigrate(IntPtr.Zero, Service, Account, 16384, true, bytes =>
                {
                    keyUses++;
                    return bytes.ToArray();
                }));
            Assert.Equal(MigrationFailed, refused.Message);
            Assert.Equal(0, keyUses);

            File.WriteAllText(fixture.Ledger, "previous entry\n");
            byte[] ledger = File.ReadAllBytes(fixture.Ledger);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine);
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(ledger.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Ledger)));
            Assert.Same(original, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(stored, original.Data));
            Assert.True(original.TrustedPaths.SequenceEqual(readers));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, original.Partition);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    [Theory]
    [InlineData(false, 40, false)]
    [InlineData(false, 41, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 40, true)]
    public void MetadataReply_RequiresTheRunningVerifierImage(bool authenticated, int status, bool accepted)
    {
        bool resumed = false;
        int result = MacKeychainItem.NativeCalls.AuthenticatedReply(() => authenticated, () =>
        {
            resumed = true;
            return status;
        }, CancellationToken.None);
        Assert.Equal(authenticated, resumed);
        Assert.Equal(accepted, result == 40);
        if (!authenticated)
        {
            Assert.NotEqual(41, result);
        }
    }

    [Fact]
    public void CreateKey_UnverifiedMissingReply_PreservesCompetingKey()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        try
        {
            SigningKeyStoreTests.Item competing = new(der, ["original reader"]);
            fixture.Calls.OtherStored = competing;
            fixture.Calls.CacheOtherMiss = true;
            fixture.Calls.SecondaryReply = () => MacKeychainItem.NativeCalls.AuthenticatedReply(
                () => false, () => 41, CancellationToken.None);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, fixture.Create(output, error));
            Assert.Empty(output.ToString());
            Assert.True(error.ToString() == "The item could not be saved in the macOS Keychain." + error.NewLine);
            Assert.Null(fixture.Calls.Stored);
            Assert.Same(competing, fixture.Calls.OtherStored);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, competing.Data));
            Assert.Equal(["original reader"], competing.TrustedPaths);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
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
        int setupUpdates = fixture.Calls.Updates;
        // A fresh issuer process has no in-memory record of the earlier create.
        MacKeychainSigningKeyStore reopened = new(fixture.Apphost, new MacKeychainItem(fixture.Calls));
        LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, pem)]);
        AssertIssued(reopened, fixture.Ledger, original);
        AssertIssued(reopened, fixture.Ledger, original);
        Assert.Same(duplicate, fixture.Calls.OtherStored);
        Assert.Equal(["higher-priority reader"], duplicate.TrustedPaths);
        Assert.Equal(setupUpdates, fixture.Calls.Updates);
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
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
            Assert.True(output.GetStringBuilder().Length == 0, "The refused issuer produced output.");
            Assert.True(File.ReadAllText(fixture.Ledger) == "previous entry\n", "The fixture ledger changed.");
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.True(ReferenceEquals(item, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]), "The stored reader list changed.");
            Assert.True(item.Partition == "teamid:original-reader", "The partition changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Issue_ProviderChangesPolicyOrRecord_RollsBackBeforeIssuingAndCanRetry(bool replaceRecord)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
        fixture.Calls.Stored = item;
        fixture.Calls.AfterUpdate = updated =>
        {
            if (replaceRecord)
            {
                fixture.Calls.Stored = new SigningKeyStoreTests.Item(updated.Data, updated.TrustedPaths);
            }
            else
            {
                updated.Partition = "different partition";
            }
        };
        File.WriteAllText(fixture.Ledger, "previous entry\n");
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(output.GetStringBuilder().Length == 0, "The refused issuer produced output.");
            Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
            Assert.True(ReferenceEquals(item, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.Partition == SigningKeyStoreTests.Item.OriginalPartition, "The partition changed.");
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]), "The stored reader list changed.");
            Assert.True(File.ReadAllText(fixture.Ledger) == "previous entry\n", "The fixture ledger changed.");

            fixture.Calls.AfterUpdate = null;
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, item.Data));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            Assert.Equal(["original reader"], item.TrustedPaths);
            Assert.Equal(3, File.ReadAllLines(fixture.Ledger).Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Issue_SameNameReplacementAlreadyHoldingExactDer_RefusesBeforeAnyWrite(bool instanceBound, bool raw)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item initial = new(legacy, ["original reader"]);
            byte[] replacement = raw ? der : legacy;
            SigningKeyStoreTests.Item competing = new(replacement, ["original reader"]);
            fixture.Calls.InstanceBoundPersistentReferences = instanceBound;
            fixture.Calls.Stored = initial;
            fixture.Calls.BeforeOwnerLock = () => fixture.Calls.Stored = competing;
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
            Assert.True(File.ReadAllText(fixture.Ledger) == "previous entry\n", "The fixture ledger changed.");
            Assert.True(ReferenceEquals(competing, fixture.Calls.Stored), "The competing item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(replacement, competing.Data));
            Assert.True(competing.TrustedPaths.SequenceEqual(["original reader"]), "The competing reader list changed.");
            Assert.True(competing.Partition == SigningKeyStoreTests.Item.OriginalPartition, "The competing partition changed.");
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoleReader_StagedViewUnavailableOrDisagrees_RollsBackAndCanRetry(bool missingUnique)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
            fixture.Calls.Stored = item;
            fixture.Calls.AfterUpdate = updated =>
            {
                if (missingUnique)
                {
                    fixture.Calls.MissNextUniqueLookup = true;
                }
                else
                {
                    fixture.Calls.NextPolicyObservation = new(updated.Data, ["different staged reader"]);
                }
            };
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            byte[] ledger = File.ReadAllBytes(fixture.Ledger);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine);
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(ledger.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Ledger)));
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            Assert.Equal(1, fixture.Calls.Updates);

            fixture.Calls.AfterUpdate = null;
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, item.Data));
            Assert.Equal(2, fixture.Calls.Updates);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Equal(3, File.ReadAllLines(fixture.Ledger).Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Fact]
    public void Issue_RollbackError_RetriesRollbackBeforeRestoringAutocommit()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, [fixture.Apphost]);
            fixture.Calls.Stored = item;
            fixture.Calls.RefuseCommit = true;
            fixture.Calls.RollbackFailures = 1;
            File.WriteAllBytes(fixture.Ledger, [1, 2, 3]);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.Equal(MigrationFailed + error.NewLine, error.ToString());
            Assert.Empty(output.ToString());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Ledger));
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.Equal([fixture.Apphost], item.TrustedPaths);
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            Assert.Equal(2, fixture.Calls.RollbackAttempts);
            Assert.Equal(1, fixture.Calls.RestoreAttempts);
            Assert.False(fixture.Calls.RecoveryPending);
            Assert.Equal(0, fixture.Calls.VerifiedRollbacks);

            fixture.Calls.RefuseCommit = false;
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, item.Data));
            Assert.Equal(2, fixture.Calls.Updates);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Fact]
    public void Issue_CommittedMigrationSurvivesCleanupErrorAndRecoversBeforeNextRead()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, [fixture.Apphost]);
            fixture.Calls.Stored = item;
            fixture.Calls.RestoreFailures = 2;
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.True(fixture.Calls.RecoveryPending);
            Assert.Equal(2, fixture.Calls.RestoreAttempts);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, item.Data));
            Assert.Equal([fixture.Apphost], item.TrustedPaths);
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.False(fixture.Calls.RecoveryPending);
            Assert.Equal(3, fixture.Calls.RestoreAttempts);
            Assert.Equal(0, fixture.Calls.RollbackAttempts);
            Assert.Equal(1, fixture.Calls.Updates);
            Assert.Equal(2, File.ReadAllLines(fixture.Ledger).Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Fact]
    public void CreateKey_CommittedInsertStillReturnsItsPublicKeyAfterCleanupError()
    {
        using PortableFixture fixture = new();
        fixture.Calls.RestoreFailures = 2;
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(0, fixture.Create(output, error));
        Assert.Empty(error.ToString());
        SigningKeyStoreTests.Item item = Assert.IsType<SigningKeyStoreTests.Item>(fixture.Calls.Stored);
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(item.Data, out int consumed);
        Assert.Equal(item.Data.Length, consumed);
        Assert.Equal(key.ExportSubjectPublicKeyInfoPem() + output.NewLine, output.ToString());
        Assert.Equal([fixture.Apphost], item.TrustedPaths);
        Assert.True(fixture.Calls.RecoveryPending);
        Assert.False(Assert.IsType<byte[]>(fixture.Calls.CreateBuffer).AsSpan().ContainsAnyExcept((byte)0));
        AssertIssued(fixture.Store, fixture.Ledger, LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]));
        Assert.False(fixture.Calls.RecoveryPending);
        Assert.Same(item, fixture.Calls.Stored);
        Assert.Equal(1, fixture.Calls.Creates);
        Assert.Equal(1, fixture.Calls.Updates);
        Assert.Equal(0, fixture.Calls.Deletes);
    }

    [Fact]
    public void CreateKey_PendingRollbackIsRecoveredBeforeAnExistenceCheck()
    {
        using PortableFixture fixture = new();
        fixture.Calls.RefuseCommit = true;
        fixture.Calls.RollbackFailures = 2;
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.True(fixture.Calls.RecoveryPending);
        Assert.False(Assert.IsType<byte[]>(fixture.Calls.CreateBuffer).AsSpan().ContainsAnyExcept((byte)0));
        fixture.Calls.RefuseCommit = false;
        Assert.False(fixture.Store.Exists(Account));
        Assert.False(fixture.Calls.RecoveryPending);
        Assert.Null(fixture.Calls.Stored);
        Assert.Null(fixture.Calls.OtherStored);
        error.GetStringBuilder().Clear();
        Assert.Equal(0, fixture.Create(output, error));
        Assert.Empty(error.ToString());
        AssertIssued(fixture.Store, fixture.Ledger, LicencePublicKeys.FromTable([(Account, output.ToString().Trim())]));
    }

    [Fact]
    public void CreateKey_RefusedDestinationSetterRollsBackThePublicReservationAndClearsTheInput()
    {
        using PortableFixture fixture = new();
        fixture.Calls.RefuseUpdate = true;
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.Null(fixture.Calls.Stored);
        Assert.Null(fixture.Calls.OtherStored);
        Assert.False(File.Exists(fixture.Ledger));
        Assert.Equal(1, fixture.Calls.Creates);
        Assert.Equal(1, fixture.Calls.Updates);
        Assert.Equal(0, fixture.Calls.Deletes);
        Assert.Same(fixture.Calls.CreateBuffer, fixture.Calls.UpdateBuffer);
        Assert.False(Assert.IsType<byte[]>(fixture.Calls.CreateBuffer).AsSpan().ContainsAnyExcept((byte)0));
        fixture.Calls.AssertReleased();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25294)]
    public void CreationPassword_IsAsciiHexAndClearedAfterTheProviderReturns(int result)
    {
        byte[] entropy = new byte[32];
        byte[] password = new byte[64];
        bool consumed = false;
        int status = MacKeychainItem.NativeCalls.WithScratchPassword(entropy, password, encoded =>
        {
            Assert.Equal(64, encoded.Length);
            Span<byte> decoded = stackalloc byte[32];
            try
            {
                Assert.Equal(OperationStatus.Done, Convert.FromHexString(encoded, decoded, out int read, out int written));
                Assert.Equal(encoded.Length, read);
                Assert.Equal(decoded.Length, written);
                Assert.True(CryptographicOperations.FixedTimeEquals(entropy, decoded));
                consumed = true;
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        });
        Assert.Equal(result, status);
        Assert.True(consumed);
        Assert.False(entropy.AsSpan().ContainsAnyExcept((byte)0));
        Assert.False(password.AsSpan().ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void SoleReader_FileStagedGroupMissing_VerifiesRollbackThenRefusesBeforeAnotherSetter()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
            fixture.Calls.Stored = item;
            fixture.Calls.InstanceBoundPersistentReferences = false;
            fixture.Calls.StagedGroupUnavailable = true;
            fixture.Calls.AfterUpdate = _ => fixture.Calls.MissNextUniqueLookup = true;
            File.WriteAllBytes(fixture.Ledger, [1, 2, 3]);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using StringWriter output = new();
                using StringWriter error = new();
                Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
                Assert.True(error.ToString() == MigrationFailed + error.NewLine);
                Assert.Empty(output.ToString());
                Assert.Same(item, fixture.Calls.Stored);
                Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
                Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]));
                Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Ledger));
                Assert.Equal(1, fixture.Calls.Updates);
                Assert.Equal(1, fixture.Calls.VerifiedRollbacks);
                Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Deletes);
            }

            // A distinct generated envelope at the same names gets its own decision.
            fixture.Calls.Stored = new(legacy, ["original reader"]);
            fixture.Calls.AfterUpdate = null;
            fixture.Calls.StagedGroupUnavailable = false;
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            AssertIssued(fixture.Store, fixture.Ledger, original);
            Assert.Equal(2, fixture.Calls.Updates);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, fixture.Calls.Stored.Data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Issue_AdditionalReaderOrPartition_RefusesBeforeTheSetter(bool extraApplication, bool extraPartition,
        bool readOnly)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            string[] readers = extraApplication ? ["original reader", "another reader"] : ["original reader"];
            string partition = SigningKeyStoreTests.Item.OriginalPartition + (extraPartition ? ",teamid:keeper" : "");
            SigningKeyStoreTests.Item initial = new(legacy, [.. readers]) { Partition = partition };
            fixture.Calls.Stored = initial;
            MacKeychainSigningKeyStore store = readOnly
                ? new MacKeychainSigningKeyStore(null, new MacKeychainItem(fixture.Calls)) : fixture.Store;
            Assert.Equal(readOnly, store.WriteRefusal is not null);
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(store, fixture.Ledger, output, error));
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
            Assert.True(File.ReadAllText(fixture.Ledger) == "previous entry\n", "The fixture ledger changed.");
            Assert.True(ReferenceEquals(initial, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, initial.Data));
            Assert.True(readers.SequenceEqual(initial.TrustedPaths), "The stored reader list changed.");
            Assert.True(partition == initial.Partition, "The partition changed.");
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Empty(fixture.Calls.LockOrder);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Fact]
    public void WriteRefusal_UnpartitionedLegacyWithTwoReaders_UsesAuthorizedReadWithoutMutation()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["first reader", "second reader"]) { Partition = "" };
            fixture.Calls.Stored = item;
            fixture.Calls.RemainingReads = 1;
            MacKeychainSigningKeyStore sharedHost = new(null, new MacKeychainItem(fixture.Calls));
            Assert.Equal(MacKeychainSigningKeyStore.SharedHostRefused, sharedHost.WriteRefusal);

            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(sharedHost, fixture.Ledger, original);
            Assert.Single(File.ReadAllLines(fixture.Ledger));
            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.Empty(fixture.Calls.LockOrder);
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["first reader", "second reader"]));
            Assert.Empty(item.Partition);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstRead_AuthorizationDeniedForPartitionedReaders_RefusesWithoutPromptOrRetry(bool sharedHost)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["first reader", "second reader"]);
            fixture.Calls.Stored = item;
            fixture.Calls.RefuseRead = true;
            MacKeychainSigningKeyStore store = sharedHost
                ? new(null, new MacKeychainItem(fixture.Calls)) : fixture.Store;
            Assert.Equal(sharedHost, store.WriteRefusal is not null);
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            byte[] previous = File.ReadAllBytes(fixture.Ledger);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine);
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(previous.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Ledger)));
            Assert.True(fixture.Calls.InteractionAllowed);
            Assert.Equal(1, fixture.Calls.DataRequests);
            Assert.Empty(fixture.Calls.LockOrder);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["first reader", "second reader"]));
            Assert.True(item.Partition == SigningKeyStoreTests.Item.OriginalPartition);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }

    [Fact]
    public void WriteRefusal_AuthorizedSingleReaderLegacy_IssuesWithOneReadAndNoWriter()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
            fixture.Calls.Stored = item;
            fixture.Calls.RemainingReads = 1;
            MacKeychainSigningKeyStore store = new(null, new MacKeychainItem(fixture.Calls));
            Assert.Equal(MacKeychainSigningKeyStore.SharedHostRefused, store.WriteRefusal);
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            AssertIssued(store, fixture.Ledger, original);
            Assert.Single(File.ReadAllLines(fixture.Ledger));
            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.True(ReferenceEquals(item, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
            Assert.Empty(fixture.Calls.LockOrder);
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRefusal_DisagreementAcrossTheOnlyRead_RefusesBeforeSigning(bool changedVersion)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
            fixture.Calls.Stored = item;
            fixture.Calls.RemainingReads = 1;
            if (changedVersion)
            {
                // Same logical item, bytes and endpoint policies, different committed revision.
                fixture.Calls.AfterCopyContent = () => fixture.Calls.DatabaseVersion++;
            }
            else
            {
                // Independent pre-read observation. The authorized password copy and
                // post-read policy both come from the sole-reader item.
                fixture.Calls.NextPolicyObservation = new(legacy, ["original reader", "second reader"]);
            }
            MacKeychainSigningKeyStore store = new(null, new MacKeychainItem(fixture.Calls));
            Assert.Equal(MacKeychainSigningKeyStore.SharedHostRefused, store.WriteRefusal);
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            byte[] previous = File.ReadAllBytes(fixture.Ledger);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine);
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(previous.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Ledger)));
            Assert.Equal(1, fixture.Calls.DataRequests);
            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.Empty(fixture.Calls.LockOrder);
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Same(item, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, item.Partition);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WriteRefusal_AgreementInStaleView_RequiresCurrentPolicyAndData(bool legacy, bool policyChanged)
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] stored = legacy ? EncodeLegacy(der) : der;
        try
        {
            SigningKeyStoreTests.Item cached = new(stored, ["original reader"]);
            SigningKeyStoreTests.Item committed = new(stored, policyChanged ? ["other reader"] : ["original reader"]);
            if (!policyChanged)
            {
                committed.Data[^1] ^= 1;
            }
            fixture.Calls.Stored = cached;
            fixture.Calls.CommittedReadView = committed;
            fixture.Calls.RemainingReads = 1;
            MacKeychainSigningKeyStore store = new(null, new MacKeychainItem(fixture.Calls));
            Assert.Equal(MacKeychainSigningKeyStore.SharedHostRefused, store.WriteRefusal);
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            byte[] previous = File.ReadAllBytes(fixture.Ledger);
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(store, fixture.Ledger, output, error));
            Assert.True(error.ToString() == MigrationFailed + error.NewLine);
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(previous.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Ledger)));
            Assert.Equal(1, fixture.Calls.DataRequests);
            Assert.Equal(0, fixture.Calls.RemainingReads);
            Assert.Empty(fixture.Calls.LockOrder);
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
            Assert.Same(cached, fixture.Calls.Stored);
            Assert.True(CryptographicOperations.FixedTimeEquals(stored, cached.Data));
            Assert.True(cached.TrustedPaths.SequenceEqual(["original reader"]));
            Assert.Equal(SigningKeyStoreTests.Item.OriginalPartition, cached.Partition);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
            CryptographicOperations.ZeroMemory(der);
        }
    }

    [Fact]
    public async Task Issue_WaitsForTheSameLedgerBeforePublishingOnce()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        using StringWriter output = new();
        using StringWriter error = new();
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? issue = null;
        try
        {
            fixture.Calls.Stored = new SigningKeyStoreTests.Item(der, ["original reader"]);
            File.WriteAllText(fixture.Ledger, "previous entry\n");
            using (FileStream prior = new(fixture.Ledger, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                issue = Task.Run(() => RunIssue(fixture.Store, fixture.Ledger, output, error,
                    ledgerContended: () => waiting.TrySetResult()));
                await waiting.Task.WaitAsync(FixtureTimeout);
                Assert.True(output.GetStringBuilder().Length == 0);
            }
            Assert.Equal(0, await issue.WaitAsync(FixtureTimeout));
            Assert.True(error.GetStringBuilder().Length == 0);
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            Assert.Equal(1, new IssuerEvidence(log).VerifyOutput(output.ToString(), original));
            string[] rows = File.ReadAllLines(fixture.Ledger);
            Assert.Equal(2, rows.Length);
            Assert.True(rows[0] == "previous entry" && rows[1].Split('\t')[1] == "Ada");
        }
        finally
        {
            try
            {
                if (issue is not null)
                {
                    await issue.WaitAsync(FixtureTimeout);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
            }
        }
    }

    [Fact]
    public async Task Issue_RegeneratedPhysicalId_PreservesTheLogicalItemAndTwoVerifiableLicences()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ManualResetEventSlim secondReading = new();
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        SigningKeyStoreTests.Item item = new(legacy, ["original reader"]);
        fixture.Calls.Stored = item;
        fixture.Calls.AfterUpdate = updated => updated.PhysicalVersion++;
        try
        {
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            TaskCompletionSource firstReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Calls.BeforeOwnerLock = () =>
            {
                firstReading.SetResult();
                Assert.True(secondReading.Wait(FixtureTimeout), "The competing issuer did not start.");
            };
            MacKeychainSigningKeyStore second = new(fixture.Apphost, new MacKeychainItem(fixture.Calls), secondReading.Set);
            await RunOverlappingIssues(firstReading.Task, secondReading,
                () => AssertIssued(fixture.Store, fixture.Ledger, original),
                () => AssertIssued(second, fixture.Ledger, original));
            Assert.Equal(2, File.ReadAllLines(fixture.Ledger).Length);
            Assert.True(ReferenceEquals(item, fixture.Calls.Stored), "The stored item changed.");
            Assert.Equal(1, item.PhysicalVersion);
            Assert.True(CryptographicOperations.FixedTimeEquals(der, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["original reader"]), "The stored reader list changed.");
            Assert.True(item.Partition == SigningKeyStoreTests.Item.OriginalPartition, "The partition changed.");
            Assert.Equal(1, fixture.Calls.Updates);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(der);
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
    public async Task CreateKey_UnavailableDestinationOrStalledProbe_ReleasesLocksAndPublishesNothing(bool stall)
    {
        if (!stall)
        {
            AssertLostDestinationAtInsertion();
            return;
        }
        using PortableFixture fixture = new();
        TaskCompletionSource validating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SettableTimeProvider time = new(DateTimeOffset.UnixEpoch);
        StalledImageCheck children = new();
        fixture.Calls.SecondaryReply = () => MacKeychainItem.NativeCalls.BoundedProbe(_ => children, (probe, cancel) =>
        {
            using CancellationTokenRegistration stop = cancel.Register(probe.Stop);
            return MacKeychainItem.NativeCalls.AuthenticatedReply(() =>
            {
                validating.SetResult();
                Assert.True(probe.Stopped.Wait(FixtureTimeout));
                return false;
            }, () =>
            {
                probe.Resumed = true;
                return 41;
            }, cancel);
        }, MacKeychainItem.NativeCalls.ProbeTimeout, time);
        Task creation = Task.Run(() => AssertCreationRefused(fixture));
        await validating.Task.WaitAsync(FixtureTimeout);
        time.WaitForTimer(1, FixtureTimeout).Fire();
        await creation.WaitAsync(FixtureTimeout);
        Assert.True(children.Reaped);
        Assert.False(children.Resumed);
    }

    private sealed class StalledImageCheck : IDisposable
    {
        public ManualResetEventSlim Stopped { get; } = new();
        public bool Resumed { get; set; }
        public bool Reaped { get; private set; }

        public void Stop() => Stopped.Set();

        public void Dispose()
        {
            Assert.True(Stopped.IsSet);
            Stopped.Dispose();
            Reaped = true;
        }
    }

    private static void AssertLostDestinationAtInsertion()
    {
        using PortableFixture fixture = new();
        bool reached = false;
        fixture.Calls.BeforeDestinationInsert = target =>
        {
            Assert.Equal((IntPtr)4, target);
            fixture.Calls.DefaultIsOther = true;
            fixture.Calls.DestinationUnavailable = true;
            reached = true;
        };
        AssertCreationRefused(fixture);
        Assert.True(reached);
        Assert.False(Assert.IsType<byte[]>(fixture.Calls.CreateBuffer).AsSpan().ContainsAnyExcept((byte)0));
    }

    private static void AssertCreationRefused(PortableFixture fixture)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.Equal(1, fixture.Create(output, error));
        Assert.Empty(output.ToString());
        Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
        Assert.Null(fixture.Calls.Stored);
        Assert.Null(fixture.Calls.OtherStored);
        Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
        fixture.Calls.AssertReleased();
    }

    private static readonly TimeSpan ChildCleanupTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateKey_StalledSecondary_ReleasesTargetBeforeTheSecondaryResumes(bool preparation)
    {
        using PortableFixture fixture = new();
        using ManualResetEventSlim resume = new();
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] sample = RandomNumberGenerator.GetBytes(32);
        SigningKeyStoreTests.Item competitor = new(sample, ["another reader"]);
        fixture.Calls.OtherStored = competitor;
        fixture.Calls.CacheOtherMiss = true;
        void Stall()
        {
            stalled.TrySetResult();
            if (!resume.Wait(FixtureTimeout))
            {
                throw new TokenStoreException("The fixture secondary did not resume.");
            }
        }
        fixture.Calls.LookupWorker = acquire => MacKeychainItem.NativeCalls.LookupTransaction.Acquire(() =>
        {
            if (preparation)
            {
                Stall();
            }
            IDisposable guard = acquire();
            return new FixtureCleanup(() =>
            {
                if (!preparation)
                {
                    Stall();
                }
                guard.Dispose();
            });
        }, () => finished.TrySetResult(), preparation ? TimeSpan.Zero : FixtureTimeout);

        using StringWriter output = new();
        using StringWriter error = new();
        Task<int> creation = Task.Run(() => fixture.Create(output, error));
        try
        {
            await stalled.Task.WaitAsync(FixtureTimeout);
            if (preparation)
            {
                Assert.Equal(1, await creation.WaitAsync(FixtureTimeout));
            }
            // A separately authorized writer can commit to the same target before the secondary resumes.
            using (MacKeychainItem.IFileTransaction target = fixture.Calls.BeginTransaction((IntPtr)4))
            {
                fixture.Calls.Stored = new SigningKeyStoreTests.Item([7], ["target writer"]);
                target.Commit();
            }
            Assert.True(fixture.Calls.Stored.Data.AsSpan().SequenceEqual(new byte[] {7}));
            Assert.False(finished.Task.IsCompleted);
        }
        finally
        {
            resume.Set();
            await finished.Task.WaitAsync(ChildCleanupTimeout);
            await creation.WaitAsync(ChildCleanupTimeout);
        }
        Assert.Equal(1, await creation);
        Assert.Empty(output.ToString());
        Assert.Equal((preparation ? "The item could not be saved in the macOS Keychain."
            : "A signing key named maker already exists; it was left as it is.") + error.NewLine, error.ToString());
        Assert.Same(competitor, fixture.Calls.OtherStored);
        Assert.True(CryptographicOperations.FixedTimeEquals(sample, competitor.Data));
        Assert.Equal(["another reader"], competitor.TrustedPaths);
        Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Updates + fixture.Calls.Deletes);
    }

    private sealed class FixtureCleanup : IDisposable
    {
        private readonly Action cleanup;
        public FixtureCleanup(Action cleanup) => this.cleanup = cleanup;
        public void Dispose() => cleanup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupTransaction_ReleasesTheProviderGuardAfterSuccessOrTimeout(bool stall)
    {
        using ManualResetEventSlim available = new(!stall);
        using MemoryStream providerGuard = new();
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable Acquire()
        {
            if (!available.Wait(FixtureTimeout))
            {
                throw new TokenStoreException("The fixture provider timed out.");
            }
            return providerGuard;
        }

        try
        {
            if (stall)
            {
                Assert.Throws<TokenStoreException>(() => MacKeychainItem.NativeCalls.LookupTransaction.Acquire(
                    Acquire, () => released.SetResult(), TimeSpan.Zero));
            }
            else
            {
                using IDisposable guard = MacKeychainItem.NativeCalls.LookupTransaction.Acquire(
                    Acquire, () => released.SetResult(), FixtureTimeout);
                Assert.True(providerGuard.CanRead);
                Assert.False(released.Task.IsCompleted);
            }
        }
        finally
        {
            available.Set();
            await released.Task.WaitAsync(ChildCleanupTimeout);
        }
        Assert.False(providerGuard.CanRead);
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
                // GraphicString has no C0 or C1 repertoire, even if the next character is graphic.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x21, 0x40, 65], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x22, 0x43, 65], false, legacy);
                // Designate the C1 repertoire and use its NEXT LINE function (8-bit and 7-bit).
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 4, 0x1b, 0x22, 0x43, 0x85], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 5, 0x1b, 0x22, 0x43, 0x1b, 0x45], true, legacy);
                void General(bool valid, params byte[] encoding) => cases.Add("1.3.6.1.4.1.32473.11",
                    [0x1b, checked((byte)encoding.Length), .. encoding], valid, legacy);
                General(false, 0x1b, 0x22, 0x7e, 0x85); // Empty C1 repertoire.
                General(false, 0x1b, 0x22, 0x30, 0x85); // Private C1 repertoire.
                General(false, 0x1b, 0x22, 0x43, 0x80); // Unassigned position in ISO-IR 77.
                General(false, 0x1b, 0x22, 0x47, 0x85); // ISO-IR 105 only assigns the single shifts.
                General(false, 0x1b, 0x22, 0x43, 0x85, 0x1b, 0x45);
                General(false, 0x1b, 0x22, 0x43, 0x1b, 0x45, 0x85);
                General(true, 0x1b, 0x22, 0x43, 0x85, 0x85);
                General(true, 0x1b, 0x22, 0x43, 0x1b, 0x45, 0x1b, 0x45);
                General(false, 0x1b, 0x21, 0x45, 0x01); // Unassigned C0 position in ISO-IR 106.
                General(false, 0x1b, 0x28, 0x4a); // Nothing uses the newly designated G0.
                General(false, 0x1b, 0x28, 0x4a, 0x20); // SPACE is independent of the 94-character set.
                General(false, 0x1b, 0x28, 0x4a, 0x1b, 0x28, 0x42, 0x41);
                General(true, 0x1b, 0x28, 0x4a, 0x5c);
                General(false, 0x1b, 0x6e); // LS2 without a G2 repertoire or character.
                General(false, 0x1b, 0x2d, 0x46, 0xd3, 0x0e); // LS1 with no GL character.
                General(false, 0x1b, 0x2d, 0x46, 0xd3, 0x0e, 0xd3); // GR does not use LS1.
                General(false, 0x1b, 0x2d, 0x46, 0xd3, 0x0e, 0x0f, 0x41);
                General(true, 0x1b, 0x2d, 0x46, 0x0e, 0x53, 0x0f, 0x41);
                General(true, 0x1b, 0x2d, 0x46, 0xd3); // ISO-IR 126 capital sigma.
                General(true, 0x1b, 0x2d, 0x43, 0xa1); // ISO-IR 109 capital H with stroke.
                General(true, 0x1b, 0x2d, 0x43, 0x0e, 0x21);
                foreach (byte reserved in new byte[] { 0xa5, 0xae, 0xbe, 0xc3, 0xd0, 0xe3, 0xf0 })
                {
                    General(false, 0x1b, 0x2d, 0x43, reserved);
                    General(false, 0x1b, 0x2d, 0x43, 0x0e, (byte)(reserved & 0x7f));
                }
                General(true, 0x1b, 0x28, 0x49, 0x5f); // Last assigned JIS X 0201 kana cell.
                General(false, 0x1b, 0x28, 0x49, 0x60);
                General(false, 0x1b, 0x24, 0x42, 0x29, 0x21); // Unassigned JIS X 0208 row.
                General(true, 0x1b, 0x24, 0x28, 0x46, 0x21, 0x30); // Blissymbol ordinal zero.
                General(false, 0x1b, 0x24, 0x28, 0x46, 0x21, 0x2f);
                General(true, 0x1b, 0x2d, 0x47, 0xac); // ISO-IR 127 Arabic comma.
                General(false, 0x1b, 0x2d, 0x47, 0xa1);
                General(true, 0x1b, 0x2d, 0x47, 0x0e, 0x2c);
                General(false, 0x1b, 0x2d, 0x47, 0x0e, 0x21);
                foreach (byte unassigned in new byte[] { 0xa4, 0xa5, 0xaa, 0xae, 0xd2, 0xff })
                {
                    General(false, 0x1b, 0x2d, 0x46, unassigned);
                    General(false, 0x1b, 0x2d, 0x46, 0x0e, (byte)(unassigned & 0x7f));
                }
                General(false, 0x1b, 0x28, 0x21, 0x47, 0x41); // Unregistered extended G0.
                General(true, 0x1b, 0x28, 0x21, 0x42, 0x41); // Invariant ISO-IR 170.
                General(false, 0x1b, 0x2d, 0x70, 0xe9); // Unregistered 96-character set.
                General(false, 0x1b, 0x24, 0x28, 0x5a, 0x24, 0x22); // Unregistered multibyte set.
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 4, 0x1b, 0x22, 0x43, 65], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 3, 0x1b, 0x22, 0x43], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 4, 0x1b, 0x21, 0x40, 0x0d], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 8, 0x1b, 0x22, 0x43, 0x85, 0x1b, 0x22, 0x43, 0x85], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.11", [0x1b, 8, 0x1b, 0x21, 0x45, 0x0d, 0x1b, 0x21, 0x40, 0x0d], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x28, 0x42, 65], false, legacy);
                // ASCII is already G0. Designating it as G1 and invoking it is redundant.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x29, 0x42, 0xc1], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 5, 0x1b, 0x29, 0x42, 0x0e, 65], false, legacy);
                // JIS Roman yen, first designated into G0 or into the higher G1.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x28, 0x4a, 0x5c], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x29, 0x4a, 0xdc], false, legacy);
                // ISO-IR 100 is a 96-character repertoire. Its lowest slot is G1.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x2d, 0x41, 0xe9], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 5, 0x1b, 0x2e, 0x41, 0x8e, 0xe9], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 8, 0x1b, 0x28, 0x4a, 0x5c, 0x1b, 0x28, 0x4a, 0x5c], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 8, 0x1b, 0x28, 0x4a, 0x5c, 0x1b, 0x28, 0x42, 0x5c], true, legacy);
                // JIS X 0208 designates a two-byte repertoire. A shift or a new
                // designation can only occur between complete graphic characters.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 5, 0x1b, 0x24, 0x42, 0x24, 0x22], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x24, 0x42, 0x24], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 7, 0x1b, 0x24, 0x42, 0x24, 0x1b, 0x28, 0x42], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 7, 0x1b, 0x24, 0x42, 0x24, 0x22, 0x24, 0x24], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 6, 0x1b, 0x24, 0x29, 0x42, 0x0e, 0x24], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "20240229"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "15820101"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "15811231"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "15000101"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 8, .. "20230229"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.6", [0x1f, 31, 10, .. "2024-02-29"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "123456"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "000000"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "240000"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "235960"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 8, .. "12:34:56"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "240001"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "126000"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "123461"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.7", [0x1f, 32, 6, .. "1X0000"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20240229123456"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "15820101000000"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "15000101120000"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20240615123456"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20230229123456"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20240229126000"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 15, .. "20240229T123456"u8], false, legacy);
                foreach (string duration in new[] { "1D", "0W", "2.50W", "1Y3MT0.00S", "T12H5M0S" })
                {
                    cases.Add("1.3.6.1.4.1.32473.9", DurationAttribute(duration), true, legacy);
                    cases.Add("1.3.6.1.4.1.32473.10", TimeAttribute("P" + duration), true, legacy);
                }
                foreach (string duration in new[] { "", "P1D", "01D", "1D0YT1S", "0DT1H", "T", "1WT2S", "1.5DT1H", "T1,5S", "1.X", "TT1S" })
                {
                    cases.Add("1.3.6.1.4.1.32473.9", DurationAttribute(duration), false, legacy);
                    cases.Add("1.3.6.1.4.1.32473.10", TimeAttribute("P" + duration), false, legacy);
                }
                cases.Add("1.3.6.1.4.1.32473.10", TimeAttribute("1D"), false, legacy);
                foreach (string time in new[] { "1985", "23:20:30Z", "19C", "1985-04", "-0002-04-12",
                    "+11985-04-12", "1985-102T23:50:30Z", "1985-W15-5", "2020-W53", "15:27.50+05:30",
                    "24:00:00.0", "23Z", "1500-01-01", "1500-01-01T12:00:00", "23:59:60Z", "23:59:60.5Z",
                    "2016-12-31T23:59:60Z", "00:59:60+01", "18:29:60-05:30", "00:00:00+01/00:59:60",
                    "2017-01-01T00:59:60+01", "2016-12-31T18:29:60-05:30", "2016-366T23:59:60Z",
                    "2016-W52-7T00:59:60+01", "2015-06-30T23:59:60Z",
                    "1972-06-30T23:59:60Z", "2015-07-01T00:59:60+01",
                    "1985-04-12/P1D", "P1W/1985-W15", "R2/P1Y6M",
                    "R/12:30+02/13:30", "12:30Z/13:30Z", "12:30-05/13:30-04" })
                {
                    cases.Add("1.3.6.1.4.1.32473.10", TimeAttribute(time), true, legacy);
                }
                foreach (string time in new[] { "", "19x5", "1985-02-29", "1985-366", "1985-W54", "+1985",
                    "-0000", "2021-W53", "25:01", "24:00.1", "23:20:30Zx", "23:20:61Z", "12:30,0",
                    "12:00:60Z", "23:58:60Z", "2016-12-31T12:00:60Z", "23:59:60+01", "00:00:00+01/00:00:60",
                    "2016-12-15T23:59:60Z", "2016-12-30T23:59:60Z", "2017-01-02T00:59:60+01",
                    "2016-12-31T00:59:60+01", "2016-365T23:59:60Z", "2016-W52-1T23:59:60Z",
                    "2016-06-30T23:59:60Z", "2016-07-01T00:59:60+01", "1971-12-31T23:59:60Z",
                    "2416-12-31T23:59:60Z", "+12016-12-31T23:59:60Z",
                    "12:30+02:00", "12-15:01", "R/1985", "P1D/P2D", "12:30+02/13:30+02",
                    "12:30/13:30Z", "1985/12:30", "1985-W15/1985-04", "1985//1986" })
                {
                    cases.Add("1.3.6.1.4.1.32473.10", TimeAttribute(time), false, legacy);
                }
                foreach (bool absolute in new[] { true, false })
                {
                    byte tag = absolute ? (byte)35 : (byte)36;
                    void Iri(ReadOnlySpan<byte> label, bool valid)
                    {
                        cases.Add("1.3.6.1.4.1.32473.12", [0x1f, tag, checked((byte)label.Length), .. label], valid, legacy);
                    }
                    Iri(absolute ? "/ISO/Registration_Authority/19785.CBEFF"u8 : "Patron-formats/TLV-encoded"u8, true);
                    Iri(absolute ? "/2/例/𐀀"u8 : "例/𐀀"u8, true);
                    Iri("1"u8, !absolute);
                    Iri("/1"u8, absolute);
                    Iri(absolute ? "/0/39"u8 : "123456789012345678901234567890"u8, true);
                    Iri(absolute ? "/3"u8 : "/relative"u8, false);
                    Iri(absolute ? "/1/40"u8 : "0/40"u8, !absolute);
                    Iri(absolute ? "/ITU-R/40"u8 : "ITU-R/40"u8, !absolute);
                    Iri(absolute ? "/ITU-R/4/1"u8 : "ITU-R/4/1"u8, !absolute);
                    Iri(absolute ? "/ITU-R/5/101"u8 : "ITU-R/5/101"u8, true);
                    Iri(absolute ? "/ITU-R/R-Recommendation/BR"u8 : "ITU-R/R-Recommendation/BR"u8, true);
                    Iri(absolute ? "/ITU-R/r-recommendation/BR"u8 : "ITU-R/r-recommendation/BR"u8, !absolute);
                    Iri(absolute ? "/itu-r/R-Recommendation/BR"u8 : "itu-r/R-Recommendation/BR"u8, !absolute);
                    Iri([], false);
                    Iri(absolute ? "/2//1"u8 : "a//b"u8, false);
                    Iri(absolute ? "/2/"u8 : "a/"u8, false);
                    Iri(absolute ? "/2/01"u8 : "01"u8, false);
                    Iri(absolute ? "/2/-a"u8 : "a-"u8, false);
                    Iri(absolute ? "/2/𐀀a--b"u8 : "𐀀a--b"u8, false);
                    Iri(absolute ? "/2/%20"u8 : "a b"u8, false);
                    Iri(absolute ? "/2/\ue000"u8 : "\ue000"u8, false);
                    Iri(absolute ? [(byte)'/', 0xc0, 0xaf] : [0xed, 0xa0, 0x80], false);
                }
            }

            return cases;
        }
    }

    private static byte[] DurationAttribute(string sample) =>
        [0x1f, 34, checked((byte)sample.Length), .. System.Text.Encoding.ASCII.GetBytes(sample)];

    private static byte[] TimeAttribute(string sample) =>
        [0x0e, checked((byte)sample.Length), .. System.Text.Encoding.ASCII.GetBytes(sample)];

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
                Assert.True(error.ToString() == "The stored signing key is not readable." + error.NewLine,
                    "Unexpected stored-key error.");
                Assert.True(output.GetStringBuilder().Length == 0, "The refused issuer produced output.");
                Assert.True(File.ReadAllText(fixture.Ledger) == "previous entry\n", "The fixture ledger changed.");
            }

            Assert.True(ReferenceEquals(item, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(valid ? der : stored, item.Data));
            Assert.True(item.TrustedPaths.SequenceEqual(["example reader"]), "The stored reader list changed.");
            Assert.True(item.Partition == SigningKeyStoreTests.Item.OriginalPartition, "The partition changed.");
            Assert.Equal(valid && legacy ? 1 : 0, fixture.Calls.Updates);
            Assert.Equal(0, fixture.Calls.Creates + fixture.Calls.Deletes);
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
