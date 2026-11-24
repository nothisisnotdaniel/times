using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Times.LicenceIssuer;
using Xunit.Abstractions;
using SampleRead = (int Status, bool Matches, bool Original);

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = MacKeychainSigningKeyStore.Service;
    private const string Account = "maker";

    private const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper log;

    public MacKeychainMigrationTests(ITestOutputHelper log)
    {
        this.log = log;
    }

    private void ReportStatus(string phase, string operation, int status) =>
        log.WriteLine("{0} | {1} returned {2}", phase, operation, status);

    private static bool IsAuthorizationDenied(int status) =>
        status is -25293 or -60005 or -2147416032; // errSecAuthFailed, errAuthorizationDenied, CSSMERR_CSP_OPERATION_AUTH_DENIED

    [MacKeychainTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyMigration_PreservesNativeRecordAndReaders(bool extraReader, bool oneTimeRead) =>
        ExerciseMigration(oneTimeRead ? "single-read-grant" : extraReader ? "multiple-readers" : "original-reader",
            extraReader, oneTimeRead);

    [MacKeychainTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeniedMigration_PreservesNativeRecordAccessAndLedgerForRetry(bool extraReader) =>
        ExerciseMigration(extraReader ? "multiple-readers-retry" : "original-reader-retry", extraReader, retry: true);

    [MacKeychainFact]
    public async Task OverlappingIssues_OriginalReader_VerifiesBothLicencesAcrossPhysicalReplacement()
    {
        IssuerEvidence evidence = new(log);
        using (Fixture fixture = new())
        using (TempFolder folder = new())
        using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (ManualResetEventSlim secondReading = new())
        using (ManualResetEventSlim firstIssued = new())
        {
            byte[] der = key.ExportPkcs8PrivateKey();
            try
            {
                fixture.AddLegacy(der);
                byte[] legacy = fixture.Legacy!;
                using Observation before = new(fixture, legacy, legacy);
                LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
                string ledger = Path.Combine(folder.Path, "ledger.tsv");
                TaskCompletionSource firstReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TestKeychainStore first = new(fixture.Reference, new MacKeychainItem.NativeCalls((operation, status) =>
                {
                    evidence.ObserveCall("issue-1", operation, status);
                    if (operation == "read password" && status == 0 && firstReading.TrySetResult())
                    {
                        Assert.True(secondReading.Wait(FixtureTimeout), "The second issuer did not enter its key read.");
                    }
                }));
                TestKeychainStore second = new(fixture.Reference, new MacKeychainItem.NativeCalls(
                    (operation, status) => evidence.ObserveCall("issue-2", operation, status)))
                {
                    BeforeRead = secondReading.Set,
                    // The two key-store calls overlap. Order the independent ledger appends.
                    AfterRead = () => Assert.True(firstIssued.Wait(FixtureTimeout), "The first issuer did not finish."),
                };
                void Issue(TestKeychainStore store, string name)
                {
                    using StringWriter output = new();
                    using StringWriter error = new();
                    Assert.Equal(0, RunIssue(store, ledger, output, error, name));
                    Assert.True(error.GetStringBuilder().Length == 0, "The fixture issuer produced an error.");
                    Assert.Equal(1, evidence.VerifyOutput(output.ToString(), original));
                }
                await RunOverlappingIssues(firstReading.Task, secondReading, () =>
                {
                    try
                    {
                        Issue(first, "Ada");
                    }
                    finally
                    {
                        firstIssued.Set();
                    }
                }, () => Issue(second, "Bob"));
                using Observation after = new(fixture, der, legacy, before);
                AssertObservation("issue-2", before, after, false, requireOriginal: false, requirePhysical: false);
                Assert.False(before.Same(before.Physical, after.Physical), "This fixture must exercise a regenerated physical ID.");
                Assert.Equal(2, File.ReadAllLines(ledger).Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
            }
        }
        evidence.Prove("overlapping-original-reader", permitted: true);
    }

    private static async Task RunOverlappingIssues(Task firstReading, ManualResetEventSlim secondReading,
        Action firstIssue, Action secondIssue)
    {
        Task first = Task.Run(firstIssue);
        Task? second = null;
        try
        {
            await firstReading.WaitAsync(FixtureTimeout);
            second = Task.Run(secondIssue);
            await Task.WhenAll(first, second).WaitAsync(FixtureTimeout);
        }
        finally
        {
            secondReading.Set();
            await Task.WhenAll(first, second ?? Task.CompletedTask).WaitAsync(ChildCleanupTimeout);
        }
    }

    [MacKeychainTheory]
    [InlineData("regular")]
    [InlineData("symlink")]
    [InlineData("fifo")]
    public void LookupGuard_UsesWriterLockAndRefusesUnsafeFiles(string kind)
    {
        using Fixture first = new();
        using Fixture second = new();
        (Fixture target, Fixture fixture) = string.CompareOrdinal(first.Path, second.Path) < 0
            ? (first, second) : (second, first);
        MacKeychainItem.NativeCalls calls = new();
        AssertLookupLocksTheSameFileAsAWriter(fixture.Reference, fixture.Directory);
        if (kind == "regular")
        {
            return;
        }

        string lockPath = Directory.GetFiles(fixture.Directory, ".fl*").Single();
        string saved = lockPath + ".original";
        File.Move(lockPath, saved);
        try
        {
            if (kind == "symlink")
            {
                File.CreateSymbolicLink(lockPath, saved);
            }
            else
            {
                Assert.Equal(0, mkfifo(lockPath, 0x180)); // 0600
            }

            // The target writer sorts first. Refusing the secondary must release it.
            Assert.Throws<TokenStoreException>(() => MacKeychainItem.Default.AddWithSearchList(
                target.Reference, [fixture.Reference], Service, Account, [], Environment.ProcessPath!, IssuerApphost));
            Assert.True(BusyLockFiles(target.Directory).Length == 0, "The target lock is still held.");
            Assert.Throws<TokenStoreException>(() => calls.BeginTransaction(fixture.Reference));
        }
        finally
        {
            File.Delete(lockPath);
            File.Move(saved, lockPath);
        }
    }

    private static string IssuerApphost => Path.Combine(AppContext.BaseDirectory, "Times.LicenceIssuer");

    [MacKeychainFact]
    public async Task CreateKey_ContendedSecondary_RefusesAndReleasesTargetForAnotherWriter()
    {
        using Fixture first = new();
        using Fixture second = new();
        (Fixture target, Fixture secondary) = string.CompareOrdinal(first.Path, second.Path) < 0
            ? (first, second) : (second, first);
        using TempFolder folder = new();
        using StringWriter output = new();
        using StringWriter error = new();
        using LicenceIssuerTests.CountingRandom random = new();
        List<int> lookupStatuses = [];
        MacKeychainItem.NativeCalls calls = new((operation, status) =>
        {
            if (operation == "open lookup lock")
            {
                lookupStatuses.Add(status);
            }
        });
        TestKeychainStore store = new(target.Reference, calls, secondary.Reference);
        Issuer issuer = new(output, error, store, random, FixedTimeProvider.At20261027(), Path.Combine(folder.Path, "ledger.tsv"));
        using SafeFileHandle competing = new(OpenReadOnly(Directory.GetFiles(secondary.Directory, ".fl*").Single(), 0), true);
        Assert.False(competing.IsInvalid);
        Assert.Equal(0, flock(competing, 2 | 4)); // LOCK_EX | LOCK_NB on the actual FileDL lock
        Task<int> creation = Task.Run(() => issuer.Run(["create-key", "--kid", Account]));
        Task<bool>? nextWriter = null;
        try
        {
            Assert.Equal(1, await creation.WaitAsync(FixtureTimeout));
            Assert.True(output.GetStringBuilder().Length == 0, "The refused issuer produced output.");
            Assert.True(error.ToString() == "The item could not be saved in the macOS Keychain." + error.NewLine,
                "The refused issuer returned an unexpected error.");
            Assert.Equal([35], lookupStatuses); // Darwin EWOULDBLOCK
            Assert.False(MacKeychainItem.Default.Exists(target.Reference, Service, Account));
            Assert.False(MacKeychainItem.Default.Exists(secondary.Reference, Service, Account));
            Assert.True(BusyLockFiles(target.Directory).Length == 0, "The refused issuer retained the target writer lock.");

            // The second writer must commit before the competing secondary lock is released.
            nextWriter = Task.Run(() => MacKeychainItem.Default.TryAddToKeychain(target.Reference,
                "Times fixture next writer", Account, [1], Environment.ProcessPath!));
            Assert.True(await nextWriter.WaitAsync(FixtureTimeout));
            Assert.True(MacKeychainItem.Default.Exists(target.Reference, "Times fixture next writer", Account));
        }
        finally
        {
            competing.Dispose();
            await creation.WaitAsync(FixtureTimeout);
            if (nextWriter is not null)
            {
                await nextWriter.WaitAsync(FixtureTimeout);
            }
        }
    }

    [MacKeychainTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateKey_StalledSecondary_AllowsAnIndependentNativeTargetCommit(bool preparation)
    {
        using Fixture target = new();
        using Fixture secondary = new();
        using ManualResetEventSlim resume = new();
        StalledLookup calls = new(preparation, resume);
        MacKeychainItem items = new(calls);
        if (!preparation)
        {
            secondary.AddLegacy([7]); // The committed duplicate will trigger secondary cleanup.
        }
        bool Add() => items.AddWithSearchList(target.Reference, [secondary.Reference], Service, Account,
            [1], Environment.ProcessPath!, IssuerApphost);
        Task first = Task.Run(() =>
        {
            if (preparation)
            {
                Assert.Throws<TokenStoreException>(() => Add());
            }
            else
            {
                Assert.False(Add());
            }
        });
        Task<bool>? other = null;
        try
        {
            await calls.Entered.Task.WaitAsync(FixtureTimeout);
            other = Task.Run(() => MacKeychainItem.Default.AddWithSearchList(target.Reference, [],
                "Times fixture other writer", Account, [2], Environment.ProcessPath!, IssuerApphost));
            Assert.True(await other.WaitAsync(FixtureTimeout));
            Assert.True(MacKeychainItem.Default.Exists(target.Reference, "Times fixture other writer", Account));
            Assert.False(calls.Released.Task.IsCompleted);
            Assert.False(MacKeychainItem.Default.Exists(target.Reference, Service, Account));
        }
        finally
        {
            resume.Set();
            try
            {
                await Task.WhenAll(first, other ?? Task.CompletedTask).WaitAsync(ChildCleanupTimeout);
            }
            finally
            {
                if (calls.Entered.Task.IsCompleted)
                {
                    await calls.Released.Task.WaitAsync(ChildCleanupTimeout);
                }
            }
        }
    }

    private sealed class StalledLookup : MacKeychainItem.NativeCalls
    {
        private readonly bool preparation;
        private readonly ManualResetEventSlim resume;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StalledLookup(bool preparation, ManualResetEventSlim resume)
        {
            this.preparation = preparation;
            this.resume = resume;
        }

        private void Stall()
        {
            Entered.TrySetResult();
            Assert.True(resume.Wait(FixtureTimeout + ChildCleanupTimeout), "The fixture secondary was not released.");
        }

        internal override IDisposable LockForLookup(IntPtr keychain, byte[] path)
        {
            try
            {
                if (preparation)
                {
                    Stall();
                }
                IDisposable guard = base.LockForLookup(keychain, path);
                return new FixtureCleanup(() =>
                {
                    try
                    {
                        if (!preparation)
                        {
                            Stall();
                        }
                    }
                    finally
                    {
                        guard.Dispose();
                        Released.TrySetResult();
                    }
                });
            }
            catch
            {
                Released.TrySetResult();
                throw;
            }
        }
    }

    [MacKeychainTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public void DocumentedUpdate_OnMatchingDisposableItem_ReportsAuthorizationAndPreservation(
        bool extraReader, bool oneTimeRead, bool allowWriter) =>
        MeasurePublicUpdate(extraReader, oneTimeRead, allowWriter, useSecItem: false);

    [MacKeychainTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public void SecItemUpdate_OnMatchingDisposableItem_ReportsAuthorizationAndPreservation(
        bool extraReader, bool oneTimeRead, bool allowWriter) =>
        MeasurePublicUpdate(extraReader, oneTimeRead, allowWriter, useSecItem: true);

    private void MeasurePublicUpdate(bool extraReader, bool oneTimeRead, bool allowWriter, bool useSecItem)
    {
        using Fixture fixture = new();
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] sample = generated.ExportPkcs8PrivateKey();
        try
        {
            fixture.AddLegacy(sample, extraReader, oneTimeRead, allowWriter);
            using Observation before = new(fixture, fixture.Legacy!, fixture.Legacy!);
            MacKeychainItem.NativeCalls calls = new();
            Assert.Equal(0, calls.FindUnique(fixture.Reference, Service, Account, out IntPtr item));
            int status;
            try
            {
                // The executing process and its authorization are identical to the issuer fixture's.
                status = useSecItem ? TimesFixtureUpdate(fixture.Reference, item, sample, (uint)sample.Length)
                    : SecKeychainItemModifyAttributesAndData(item, 0, (uint)sample.Length, sample);
            }
            finally
            {
                calls.Release(item);
            }
            string phase = allowWriter ? "control-allowed" : "control-denied";
            ReportStatus(phase, "control update", status);
            using Observation after = new(fixture, sample, fixture.Legacy!, before);
            AssertObservation(phase, before, after, oneTimeRead, requireOriginal: false, requirePhysical: false,
                controlLostReaders: extraReader);
            Assert.Equal(0, status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sample);
        }
    }

    [MacKeychainFact]
    public void SeparateReadGrant_CannotAuthorizeAnExcludedWriter() =>
        ExerciseMigration("excluded-writer", extraReader: true, oneTimeRead: true, allowWriter: false);

    [MacKeychainFact]
    public void CreateKey_SecondaryWithoutLock_CreatesAndReleasesFileLock()
    {
        using Fixture target = new();
        using Fixture secondary = new();
        File.Delete(Directory.GetFiles(secondary.Directory, ".fl*").Single());

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] data = key.ExportPkcs8PrivateKey();
        try
        {
            Assert.True(MacKeychainItem.Default.AddWithSearchList(target.Reference, [secondary.Reference],
                Service, Account, data, Environment.ProcessPath!, IssuerApphost));
            target.AssertData(data);
            Assert.True(Directory.GetFiles(secondary.Directory, ".fl*").Length == 1, "The lookup did not leave one lock file.");
            Assert.True(BusyLockFiles(target.Directory).Length == 0, "The target lock is still held.");
            Assert.True(BusyLockFiles(secondary.Directory).Length == 0, "The secondary lock is still held.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    [MacKeychainFact]
    public void ExplicitAdd_RefusesWhenTheDisposableDatabaseDisappears()
    {
        using Fixture target = new();
        using Fixture other = new();
        string saved = target.Path + ".saved";
        File.Move(target.Path, saved);
        try
        {
            Assert.Throws<TokenStoreException>(() =>
                MacKeychainItem.Default.TryAddToKeychain(target.Reference, Service, Account, [1], Environment.ProcessPath!));
            Assert.False(File.Exists(target.Path));
            Assert.False(MacKeychainItem.Default.Exists(other.Reference, Service, Account));
        }
        finally
        {
            File.Move(saved, target.Path);
        }
        Assert.False(MacKeychainItem.Default.Exists(target.Reference, Service, Account));
    }

    [MacKeychainTheory]
    [InlineData("invalid id")]
    [InlineData("maker")]
    public void Probe_RejectsInvalidIdOrNonDatabase(string account)
    {
        using Fixture fixture = new();
        string candidate = fixture.Path;
        if (account == Account)
        {
            candidate = Path.Combine(fixture.Directory, "fixture.fifo");
            Assert.Equal(0, mkfifo(candidate, 0x180));
            Assert.Equal(1, MacKeychainItem.NativeCalls.Probe(candidate, Service, account));
            candidate = Path.Combine(fixture.Directory, "fixture.txt");
            File.WriteAllText(candidate, "kych");
        }

        Assert.Equal(1, MacKeychainItem.NativeCalls.Probe(candidate, Service, account));
    }

    [MacKeychainTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateKey_GuardedProbeSeesCommittedSecondary(bool competingInsert)
    {
        using Fixture target = new();
        using Fixture secondary = new();
        using ECDsa sample = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = sample.ExportPkcs8PrivateKey();
        MacKeychainItem.NativeCalls calls = new();
        try
        {
            Assert.False(MacKeychainItem.Default.Exists(secondary.Reference, Service, Account));
            byte[]? id = null;
            string[]? entries = null;
            if (competingInsert)
            {
                entries = secondary.AddLegacy(der);
                id = secondary.Identity();
            }
            using Observation? before = competingInsert ? new(secondary, secondary.Legacy!, secondary.Legacy!) : null;
            bool created = MacKeychainItem.Default.AddWithSearchList(target.Reference, [secondary.Reference],
                Service, Account, der, Environment.ProcessPath!, IssuerApphost);
            Assert.Equal(!competingInsert, created);
            Assert.Equal(created, MacKeychainItem.Default.Exists(target.Reference, Service, Account));
            if (competingInsert)
            {
                secondary.AssertData(secondary.Legacy!);
                secondary.AssertPolicyAndIdentity(entries!, id!);
                using Observation after = new(secondary, secondary.Legacy!, secondary.Legacy!, before);
                AssertObservation("denied-write", before!, after, false, requireOriginal: true, requirePhysical: true);
            }
            else
            {
                Assert.Equal(0, calls.Find(target.Reference, Service, Account, out IntPtr marked, createdOnly: true));
                calls.Release(marked);
            }

            Assert.True(BusyLockFiles(target.Directory).Length == 0, "The target lock is still held.");
            Assert.True(BusyLockFiles(secondary.Directory).Length == 0, "The secondary lock is still held.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    private static int RunIssue(ISigningKeyStore store, string ledger, TextWriter output, TextWriter error,
        string name = "Ada")
    {
        using LicenceIssuerTests.CountingRandom random = new();
        Issuer issuer = new(output, error, store, random, FixedTimeProvider.At20261027(), ledger);
        return issuer.Run(["issue", "--kid", Account, "--name", name, "--until", "2027-12-31"]);
    }

    private sealed class IssuerEvidence
    {
        private readonly ITestOutputHelper log;
        private int updates, creates, deletes, licences;

        public IssuerEvidence(ITestOutputHelper log) => this.log = log;

        public void ObserveCall(string phase, string operation, int status)
        {
            switch (operation)
            {
                case "provider update": Interlocked.Increment(ref updates); break;
                case "provider add": Interlocked.Increment(ref creates); break;
                case "provider delete": Interlocked.Increment(ref deletes); break;
            }
            log.WriteLine("{0} | {1} returned {2}", phase, operation, status);
        }

        public int VerifyOutput(string output, LicencePublicKeys original)
        {
            using StringReader lines = new(output);
            int verified = 0;
            while (lines.ReadLine() is { } token)
            {
                Assert.True(LicenceKey.TryVerify(token, original, LicenceRevocations.None).IsValid,
                    "Issuer output failed verification with the original fixture key.");
                verified++;
                Interlocked.Increment(ref licences);
            }
            return verified;
        }

        public void AssertCounts(int expectedUpdates, int expectedLicences)
        {
            Assert.Equal(expectedUpdates, updates);
            Assert.Equal(0, creates);
            Assert.Equal(0, deletes);
            Assert.Equal(expectedLicences, licences);
        }

        public void Prove(string scenario, bool permitted)
        {
            AssertCounts(permitted ? 1 : 0, permitted ? 2 : 0);
            log.WriteLine("factory401-proof {0} updates={1} creates={2} deletes={3} licences={4}",
                scenario, updates, creates, deletes, licences);
        }
    }

    private void ExerciseMigration(string scenario, bool extraReader, bool oneTimeRead = false,
        bool allowWriter = true, bool retry = false)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        IssuerEvidence evidence = new(log);
        using (Fixture fixture = new())
        using (TempFolder folder = new())
        using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            byte[] der = key.ExportPkcs8PrivateKey();
            try
            {
                string[] access = fixture.AddLegacy(der, extraReader, oneTimeRead, allowWriter);
                byte[] legacy = fixture.Legacy!;
                byte[] identity = fixture.Identity();
                using Observation before = new(fixture, legacy, legacy);
                LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
                string ledger = Path.Combine(folder.Path, "ledger.tsv");
                File.WriteAllText(ledger, "previous entry\n");
                string phase = "issue-1";
                ReadGrant grant = new((operation, status) => evidence.ObserveCall(
                    oneTimeRead && operation == "read password" && status != 0 ? "denied-read" : phase, operation, status),
                    item => ObserveStaged(fixture, before, phase, item));
                TestKeychainStore store = new(fixture.Reference, grant);

                void Attempt(bool permitted, bool readOnlyDirectory = false)
                {
                    bool second = phase is "issue-2" or "retry-2";
                    byte[] previous = second ? der : legacy;
                    using Observation start = new(fixture, previous, previous, before);
                    if (oneTimeRead)
                    {
                        grant.AllowOnce(fixture, legacy);
                    }
                    byte[] previousLedger = File.ReadAllBytes(ledger);
                    using StringWriter output = new();
                    using StringWriter error = new();
                    if (readOnlyDirectory)
                    {
                        File.SetUnixFileMode(fixture.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    }
                    int result;
                    try
                    {
                        result = RunIssue(store, ledger, output, error, second ? "Bob" : "Ada");
                    }
                    finally
                    {
                        if (readOnlyDirectory)
                        {
                            File.SetUnixFileMode(fixture.Directory, OwnerOnly);
                        }
                    }
                    Assert.False(grant.HasOutstandingGrant);
                    int verified = evidence.VerifyOutput(output.ToString(), original);
                    using Observation after = new(fixture, der, previous, before);
                    if (result == 0)
                    {
                        AssertObservation(phase, before, after, oneTimeRead, requireOriginal: false, requirePhysical: false);
                        Assert.True(error.GetStringBuilder().Length == 0, "The fixture issuer produced an error.");
                    }
                    else
                    {
                        AssertObservation(phase, start, after, oneTimeRead, requireOriginal: true, requirePhysical: true);
                        Assert.True(error.ToString() == MigrationFailed + error.NewLine, "Unexpected migration error.");
                        Assert.True(output.GetStringBuilder().Length == 0, "The refused issuer produced output.");
                        Assert.True(previousLedger.AsSpan().SequenceEqual(File.ReadAllBytes(ledger)), "The fixture ledger changed.");
                        fixture.AssertData(previous);
                    }
                    Assert.Equal(permitted ? 0 : 1, result);
                    Assert.Equal(permitted ? 1 : 0, verified);
                    if (!permitted)
                    {
                        fixture.AssertPolicyAndIdentity(access, identity);
                        evidence.AssertCounts(0, 0);
                    }
                }

                if (retry)
                {
                    phase = "denied-write";
                    Attempt(false, readOnlyDirectory: true);
                }

                phase = retry ? "retry-1" : allowWriter ? "issue-1" : "denied-write";
                Attempt(!extraReader);
                if (!extraReader)
                {
                    evidence.AssertCounts(1, 1);
                    phase = retry ? "retry-2" : "issue-2";
                    Attempt(true);
                    Assert.Equal(3, File.ReadAllLines(ledger).Length);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
            }
        }
        evidence.Prove(scenario, permitted: !extraReader);
    }

    private static void AssertIssued(ISigningKeyStore store, string ledger, LicencePublicKeys original)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        Assert.True(RunIssue(store, ledger, output, error) == 0, "The fixture issuer did not issue a licence.");
        Assert.True(error.GetStringBuilder().Length == 0, "The fixture issuer produced an error.");
        Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
    }

    private sealed class ReadGrant : MacKeychainItem.NativeCalls
    {
        private readonly Action<IntPtr> observeStaged;
        private Fixture? source;
        private byte[]? sample;
        private IntPtr grantedData;
        private IntPtr stagedItem;

        public ReadGrant(Action<string, int> reportStatus, Action<IntPtr> observeStaged) : base(reportStatus)
        {
            this.observeStaged = observeStaged;
        }

        public override int UpdateData(IntPtr item, ReadOnlyMemory<byte> data)
        {
            int status = base.UpdateData(item, data);
            if (status == 0)
            {
                stagedItem = CFRetain(item);
            }
            return status;
        }

        public override MacKeychainItem.IFileTransaction BeginTransaction(IntPtr keychain, bool nonInteractive = false) =>
            new ObservedTransaction(base.BeginTransaction(keychain, nonInteractive), ObservePending);

        private void ObservePending()
        {
            IntPtr item = stagedItem;
            stagedItem = IntPtr.Zero;
            if (item == IntPtr.Zero)
            {
                return;
            }
            try
            {
                observeStaged(item);
            }
            finally
            {
                Release(item);
            }
        }

        // Observe after the issuer has made its decision, while its transaction is still open.
        private sealed class ObservedTransaction : MacKeychainItem.IFileTransaction
        {
            private readonly MacKeychainItem.IFileTransaction transaction;
            private readonly Action observe;
            public ObservedTransaction(MacKeychainItem.IFileTransaction transaction, Action observe)
            {
                this.transaction = transaction;
                this.observe = observe;
            }
            public void Commit()
            {
                observe();
                transaction.Commit();
            }
            public void Dispose()
            {
                try
                {
                    observe();
                }
                finally
                {
                    transaction.Dispose();
                }
            }
        }

        public bool HasOutstandingGrant => sample is not null;

        public void AllowOnce(Fixture fixture, byte[] expected)
        {
            source = fixture;
            sample = expected;
        }

        public override int CopyContent(IntPtr item, out uint length, out IntPtr data)
        {
            int status = base.CopyContent(item, out length, out data);
            if (source is null)
            {
                return status;
            }

            ClearAndFree(data, length);
            data = IntPtr.Zero;
            length = 0;
            Assert.True(status != 0, "The issuer unexpectedly had permanent decrypt access.");
            Assert.True(status is -25308 or -25293 or -25243 or -60005 or -128, $"Unexpected read denial {status}.");
            Assert.NotNull(sample);
            // The hosted session has no SecurityAgent UI. A separately trusted reader
            // confirms the exact generated sample on the real item for this single grant.
            source.VerifyReaders(sample);
            length = (uint)sample.Length;
            grantedData = data = Marshal.AllocHGlobal(sample.Length);
            Marshal.Copy(sample, 0, data, sample.Length);
            sample = null;
            return 0;
        }

        private void ClearAndFree(IntPtr data, uint length)
        {
            if (data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[length], 0, data, checked((int)length));
                base.FreeContent(data);
            }
        }

        public override void FreeContent(IntPtr data)
        {
            if (data == grantedData && data != IntPtr.Zero)
            {
                // ReadContent has already cleared the complete allocation.
                Marshal.FreeHGlobal(data);
                grantedData = IntPtr.Zero;
                return;
            }

            base.FreeContent(data);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte previousInteraction;
        private readonly string password = Guid.NewGuid().ToString("N");
        private readonly List<string> readers = [];
        public string Directory { get; }
        public string Path { get; }
        public IntPtr Reference { get; }
        public byte[]? Legacy { get; private set; }

        public Fixture()
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException();
            }

            string root = Environment.GetEnvironmentVariable("TIMES_TEST_KEYCHAIN_ROOT")!;
            string parent = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Keychains");
            Assert.True(System.IO.Path.IsPathFullyQualified(root));
            root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            Assert.True(root.StartsWith(parent + "/", StringComparison.Ordinal), "The fixture root is outside the harness.");
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(root));
            for (DirectoryInfo? entry = new(root); entry is not null && entry.FullName != parent; entry = entry.Parent)
            {
                Assert.True(entry.LinkTarget is null, "A fixture directory is a symbolic link.");
            }

            Directory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory, OwnerOnly);
            Path = System.IO.Path.Combine(Directory, "issuer.keychain-db");
            Assert.Equal(0, SecKeychainGetUserInteractionAllowed(out previousInteraction));
            Assert.Equal(0, SecKeychainSetUserInteractionAllowed(0));
            byte[] encodedPassword = Encoding.UTF8.GetBytes(password);
            try
            {
                Assert.Equal(0, SecKeychainCreate(Path, (uint)encodedPassword.Length, encodedPassword, 0, 0, out IntPtr reference));
                Reference = reference;
                Assert.NotEqual(IntPtr.Zero, Reference);
                Assert.True(Path == Encoding.UTF8.GetString(new MacKeychainItem.NativeCalls().KeychainPath(Reference)),
                    "Keychain Services opened a different database.");
                Assert.Equal(0, TimesFixtureAdd(Reference, CheckService, null, null, 1, [1], 1));
                string[] markerPolicy = AccessEntries(Path, CheckService);
                Assert.True(PartitionIds(markerPolicy).Length > 0, "The marker lacks a partition.");
                Assert.True(markerPolicy.Any(entry => entry.Contains(" integrity", StringComparison.Ordinal)),
                    "The marker lacks integrity protection.");
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedPassword);
            }
        }

        public string[] AddLegacy(byte[] der, bool extra = false, bool oneTimeRead = false, bool allowWriter = true)
        {
            Legacy = EncodeLegacy(der);
            string[] originalPartitions = PartitionIds(AccessEntries(Path, CheckService));
            string[] paths = extra ? [CompileReader("reader-one"), CompileReader("reader-two")] : [];
            List<string> partitions = [];
            foreach (string path in paths)
            {
                RunFixtureProcess(path, ["register"]);
                partitions.Add(PartitionIds(AccessEntries(Path, Marker(path))).Single());
                readers.Add(path);
            }
            Assert.Equal(paths.Length, partitions.Distinct(StringComparer.Ordinal).Count());
            Assert.False(partitions.Intersect(originalPartitions, StringComparer.Ordinal).Any(),
                "A fixture reader shares the issuer partition.");

            // Establish the original readers at creation. All later observations and updates keep this policy.
            Assert.Equal(0, TimesFixtureAdd(Reference, Service, paths.FirstOrDefault(), paths.LastOrDefault(),
                oneTimeRead ? 0 : 1, Legacy, (uint)Legacy.Length));
            string[] expected = allowWriter ? [.. originalPartitions, .. partitions] : [.. partitions];
            if (extra)
            {
                RunSecurity("set-generic-password-partition-list", "-s", Service, "-a", Account, "-S",
                    string.Join(',', expected), "-k", password, Path);
                VerifyReaders(Legacy);
            }
            string[] access = AccessEntries(Path);
            Assert.True(access.Any(entry => entry.Contains(" decrypt", StringComparison.Ordinal)), "The fixture lacks a decrypt ACL.");
            Assert.True(expected.Order(StringComparer.Ordinal).SequenceEqual(PartitionIds(access).Order(StringComparer.Ordinal)),
                "The fixture was created with different partitions.");
            return access;
        }

        private const string CheckService = "Times fixture partition check";
        private string? excludedReader;
        private static string Marker(string path) => "Times fixture " + System.IO.Path.GetFileName(path);

        private string CompileReader(string name)
        {
            string path = System.IO.Path.Combine(Directory, name);
            string source = System.IO.Path.Combine(Directory, "reader.c");
            if (!File.Exists(source))
            {
                using Stream embedded = typeof(Fixture).Assembly.GetManifestResourceStream("Times.Tests.MacKeychainFixtureReader.c")!;
                using Stream copy = File.Create(source);
                embedded.CopyTo(copy);
            }
            RunFixtureProcess("/usr/bin/xcrun", ["clang", "-std=c11", "-Wall", "-Werror", "-Wno-deprecated-declarations",
                "-framework", "Security", "-framework", "CoreFoundation", $"-DFIXTURE_DB=\"{Path}\"",
                $"-DREADER_SERVICE=\"{Marker(path)}\"", $"-DFIXTURE_SERVICE=\"{Service}\"", source, "-o", path]);
            return path;
        }

        public SampleRead[] ProbeReaders(byte[] expected, byte[] original)
        {
            excludedReader ??= CompileReader("reader-excluded");
            byte[] digests = [.. SHA256.HashData(expected), .. SHA256.HashData(original)];
            return readers.Append(excludedReader).Select(reader =>
            {
                string[] result = RunFixtureProcess(reader, ["measure"], digests).Trim().Split(' ');
                Assert.True(result.Length == 3 && result[1] is "0" or "1" && result[2] is "0" or "1",
                    "Invalid fixture measurement.");
                return (int.Parse(result[0], System.Globalization.CultureInfo.InvariantCulture), result[1] == "1", result[2] == "1");
            }).ToArray();
        }

        public void VerifyReaders(byte[] expected)
        {
            Assert.Equal(2, readers.Count);
            byte[] digest = SHA256.HashData(expected);
            foreach (string reader in readers)
            {
                RunFixtureProcess(reader, ["verify"], digest);
            }
        }

        private static string[] PartitionIds(string[] entries)
        {
            string partition = entries.Single(entry => entry.Contains(" partition_id", StringComparison.Ordinal));
            string description = partition.Split('\n').Select(line => line.Trim())
                .Single(line => line.StartsWith("description:", StringComparison.Ordinal))["description:".Length..].Trim();
            Assert.False(description.Contains("invalid partition ID", StringComparison.Ordinal) || description == "<NULL>",
                "The fixture has an invalid partition list.");
            string[] ids = description.Split(',', StringSplitOptions.TrimEntries);
            Assert.False(ids.Any(string.IsNullOrEmpty), "The fixture contains an empty partition.");
            return ids;
        }

        public byte[] Identity()
        {
            MacKeychainItem.NativeCalls calls = new();
            Assert.Equal(0, calls.Find(Reference, Service, Account, out IntPtr item));
            try
            {
                Assert.Equal(0, calls.CopyRecordId(item, out IntPtr id));
                try
                {
                    byte[] bytes = new byte[checked((int)CFDataGetLength(id))];
                    Assert.NotEmpty(bytes);
                    Marshal.Copy(CFDataGetBytePtr(id), bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    calls.Release(id);
                }
            }
            finally
            {
                calls.Release(item);
            }
        }

        public void AssertPolicyAndIdentity(string[] access, byte[] identity)
        {
            Assert.True(access.SequenceEqual(AccessEntries(Path), StringComparer.Ordinal), "Fixture access policy changed.");
            Assert.True(identity.AsSpan().SequenceEqual(Identity()), "Fixture record identifier changed.");
        }

        public void AssertData(byte[] expected)
        {
            if (readers.Count != 0)
            {
                VerifyReaders(expected);
                return;
            }

            byte[] actual = Assert.IsType<byte[]>(MacKeychainItem.Default.Read(Reference, Service, Account, 16384,
                static (_, data) => data.ToArray()));
            try
            {
                Assert.True(CryptographicOperations.FixedTimeEquals(expected, actual));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Reference != IntPtr.Zero)
                {
                    int result = SecKeychainDelete(Reference);
                    CFRelease(Reference);
                    Assert.Equal(0, result);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(Legacy);
                SecKeychainSetUserInteractionAllowed(previousInteraction);
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    private static void AssertLookupLocksTheSameFileAsAWriter(IntPtr keychain, string directory)
    {
        MacKeychainItem.NativeCalls native = new MacKeychainItem.NativeCalls();
        string writerLock;
        using (native.BeginTransaction(keychain))
        {
            writerLock = BusyLockFiles(directory).Single();
        }

        using (IDisposable guard = native.LockForLookup(keychain, native.KeychainPath(keychain)))
        {
            Assert.IsType<SafeFileHandle>(guard); // a read-only guard on the local temporary volume
            Assert.True(BusyLockFiles(directory).SequenceEqual([writerLock]), "The shared guard did not lock the writer's file.");
        }

        Assert.True(BusyLockFiles(directory).Length == 0, "A fixture lock is still held.");
    }

    private static string[] BusyLockFiles(string directory)
    {
        List<string> busy = [];
        foreach (string path in Directory.EnumerateFiles(directory, ".fl*"))
        {
            int fd = OpenReadOnly(path, 0);
            Assert.True(fd >= 0);
            using SafeFileHandle handle = new SafeFileHandle(fd, ownsHandle: true);
            if (flock(handle, 2 | 4) != 0) // LOCK_EX | LOCK_NB
            {
                Assert.Equal(35, Marshal.GetLastPInvokeError()); // EWOULDBLOCK
                busy.Add(path);
            }
        }

        return busy.ToArray();
    }

    // Bind the issuer's lookup to the fixture's Keychain without changing the user's search order.
    private sealed class TestKeychainStore : ISigningKeyStore
    {
        private readonly MacKeychainSigningKeyStore store;
        private readonly MacKeychainItem items;
        private readonly IntPtr keychain;
        private readonly IntPtr[] secondaries;

        public TestKeychainStore(IntPtr keychain, MacKeychainItem.NativeCalls? calls = null, IntPtr secondary = default)
        {
            Assert.NotEqual(IntPtr.Zero, keychain);
            items = new MacKeychainItem(calls ?? new MacKeychainItem.NativeCalls());
            store = new MacKeychainSigningKeyStore(IssuerApphost, items);
            Assert.Null(store.WriteRefusal);
            this.keychain = keychain;
            secondaries = secondary == IntPtr.Zero ? [] : [secondary];
        }

        public bool IsSupported => store.IsSupported;
        public string? WriteRefusal => store.WriteRefusal;
        public Action? BeforeRead { get; init; }
        public Action? AfterRead { get; init; }
        public byte[]? Read(string keyId)
        {
            BeforeRead?.Invoke();
            byte[]? value = store.Read(keychain, keyId);
            try
            {
                AfterRead?.Invoke();
                return value;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(value);
                throw;
            }
        }
        public bool Exists(string keyId) => items.Exists(keychain, Service, keyId)
            || secondaries.Any(other => items.Exists(other, Service, keyId));
        public bool TryCreate(string keyId, byte[] pkcs8) => items.AddWithSearchList(keychain, secondaries,
            Service, keyId, pkcs8, Environment.ProcessPath!, IssuerApphost);
    }

    private static string[] AccessEntries(string path, string service = Service)
    {
        string record = RunSecurity("dump-keychain", "-a", path).Split("keychain:")
            .Single(item => item.Contains(service, StringComparison.Ordinal));
        Match version = Regex.Match(record, @"(?m)^version: ([0-9]+)\s*$");
        Assert.True(uint.TryParse(version.Groups[1].Value, out uint dbVersion) && dbVersion >= 0x200,
            "The fixture database version lacks partition support.");
        // Compare every ACL, including the integrity entry. Enumeration order is immaterial.
        return Regex.Split(record, @"(?m)^    entry [0-9]+:\s*$").Skip(1)
            .Select(entry => entry.Trim()).Order(StringComparer.Ordinal).ToArray();
    }

    private static string RunFixtureProcess(string file, string[] args, byte[]? input = null)
    {
        ProcessStartInfo info = new(file)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(info)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task stderr = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (input is not null)
        {
            child.StandardInput.BaseStream.Write(input);
        }
        child.StandardInput.Close();
        WaitForFixture(child);
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    private static string RunSecurity(params string[] arguments)
    {
        ProcessStartInfo info = new("/usr/bin/security")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        WaitForFixture(process);
        stderr.GetAwaiter().GetResult();
        return output.GetAwaiter().GetResult();
    }

    private static void WaitForFixture(Process process)
    {
        if (!process.WaitForExit(FixtureTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(ChildCleanupTimeout), "The disposable helper did not exit during cleanup.");
            Assert.Fail("The disposable Keychain helper timed out.");
        }
        Assert.Equal(0, process.ExitCode);
    }

    private static string? NativeSkip => !OperatingSystem.IsMacOS() ? "Requires the macOS file Keychain." :
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIMES_TEST_KEYCHAIN_ROOT"))
            ? "Requires an isolated TIMES_TEST_KEYCHAIN_ROOT." : null;

    private sealed class MacKeychainFactAttribute : FactAttribute
    {
        public MacKeychainFactAttribute() => Skip = NativeSkip;
    }

    private sealed class MacKeychainTheoryAttribute : TheoryAttribute
    {
        public MacKeychainTheoryAttribute() => Skip = NativeSkip;
    }

    [DllImport(Security)]
    private static extern int SecKeychainCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint passwordLength,
        byte[] password, byte prompt, IntPtr initialAccess, out IntPtr keychain);

    [DllImport(Security)]
    private static extern int SecKeychainGetUserInteractionAllowed(out byte state);

    [DllImport(Security)]
    private static extern int SecKeychainSetUserInteractionAllowed(byte state);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixtureUpdate(IntPtr database, IntPtr item, byte[] data, uint length);

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixtureAdd(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? first, [MarshalAs(UnmanagedType.LPUTF8Str)] string? second,
        int includeCaller, byte[] sample, uint length);

    [DllImport("libc")]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ushort mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainDelete(IntPtr keychain);

    [DllImport("libc", EntryPoint = "open")]
    private static extern int OpenReadOnly([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr reference);
}
