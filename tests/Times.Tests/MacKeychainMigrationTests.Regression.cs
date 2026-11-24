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

    [Fact]
    public void Issue_SameNameReplacementAlreadyHoldingExactDer_RefusesBeforeAnyWrite()
    {
        using PortableFixture fixture = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportPkcs8PrivateKey();
        byte[] legacy = EncodeLegacy(der);
        try
        {
            SigningKeyStoreTests.Item initial = new(legacy, ["original reader"]);
            SigningKeyStoreTests.Item competing = new(der, ["original reader"]);
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
            Assert.True(CryptographicOperations.FixedTimeEquals(der, competing.Data));
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
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Issue_AdditionalReaderOrPartition_RefusesBeforeTheSetter(bool extraApplication, bool extraPartition)
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
            using StringWriter output = new();
            using StringWriter error = new();
            Assert.Equal(1, RunIssue(fixture.Store, fixture.Ledger, output, error));
            Assert.True(output.GetStringBuilder().Length == 0);
            Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
            Assert.False(File.Exists(fixture.Ledger));
            Assert.True(ReferenceEquals(initial, fixture.Calls.Stored), "The stored item changed.");
            Assert.True(CryptographicOperations.FixedTimeEquals(legacy, initial.Data));
            Assert.True(readers.SequenceEqual(initial.TrustedPaths), "The stored reader list changed.");
            Assert.True(partition == initial.Partition, "The partition changed.");
            Assert.Equal(0, fixture.Calls.Updates + fixture.Calls.Creates + fixture.Calls.Deletes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            CryptographicOperations.ZeroMemory(legacy);
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
            MacKeychainSigningKeyStore second = new(fixture.Apphost, new MacKeychainItem(fixture.Calls));
            string secondLedger = Path.ChangeExtension(fixture.Ledger, ".second.tsv");
            await RunOverlappingIssues(firstReading.Task, secondReading,
                () => AssertIssued(fixture.Store, fixture.Ledger, original),
                () =>
                {
                    secondReading.Set();
                    AssertIssued(second, secondLedger, original);
                });
            Assert.Single(File.ReadAllLines(fixture.Ledger));
            Assert.Single(File.ReadAllLines(secondLedger));
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
    public void CreateKey_UnavailableDestinationOrStalledProbe_ReleasesLocksAndPublishesNothing(bool stall)
    {
        using PortableFixture fixture = new();
        using Process? child = stall ? StartStalledProbe() : null;
        if (stall)
        {
            fixture.Calls.DuringProbe = () => MacKeychainItem.NativeCalls.WaitForProbe(child!, TimeSpan.Zero);
        }
        else
        {
            fixture.Calls.DestinationUnavailable = true;
        }

        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            Assert.Equal(1, fixture.Create(output, error));
            Assert.Empty(output.ToString());
            Assert.Equal("The item could not be saved in the macOS Keychain." + error.NewLine, error.ToString());
            Assert.Null(fixture.Calls.Stored);
            Assert.Null(fixture.Calls.OtherStored);
            Assert.Equal(0, fixture.Calls.Creates);
            fixture.Calls.AssertReleased();
        }
        finally
        {
            if (child is not null)
            {
                child.StandardInput.Close();
                Assert.True(child.WaitForExit(ChildCleanupTimeout), "The fixture probe did not exit during cleanup.");
            }
        }
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

    private static Process StartStalledProbe()
    {
        // The child waits for input, so a zero production deadline is independent of startup speed.
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

        return Process.Start(info)!;
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
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x28, 0x42, 65], false, legacy);
                // ASCII is already G0. Designating it as G1 and invoking it is redundant.
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 4, 0x1b, 0x29, 0x42, 0xc1], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.5", [0x19, 5, 0x1b, 0x29, 0x42, 0x0e, 65], false, legacy);
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
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20240615123456"u8], true, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20230229123456"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 14, .. "20240229126000"u8], false, legacy);
                cases.Add("1.3.6.1.4.1.32473.8", [0x1f, 33, 15, .. "20240229T123456"u8], false, legacy);
                foreach (string duration in new[] { "1D", "0W", "2.50W", "1Y3MT0.00S", "T12H5M0S" })
                {
                    cases.Add("1.3.6.1.4.1.32473.9", DurationAttribute(duration), true, legacy);
                }
                foreach (string duration in new[] { "", "P1D", "01D", "1D0YT1S", "0DT1H", "T", "1WT2S", "1.5DT1H", "T1,5S", "1.X", "TT1S" })
                {
                    cases.Add("1.3.6.1.4.1.32473.9", DurationAttribute(duration), false, legacy);
                }
            }

            return cases;
        }
    }

    private static byte[] DurationAttribute(string sample) =>
        [0x1f, 34, checked((byte)sample.Length), .. System.Text.Encoding.ASCII.GetBytes(sample)];

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
