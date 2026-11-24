using System.Runtime.InteropServices;
using SampleRead = (int Status, bool Matches, bool Original);

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // Use an independent, metadata-only search and the original persistent reference.
    // Keep every outcome so a changed reader cannot hide the other measurements.
    private sealed class Observation : IDisposable
    {
        private readonly MacKeychainItem.NativeCalls calls = new();
        public IntPtr Persistent { get; }
        public IntPtr Policy { get; }
        public byte[]? ProtectedValue { get; }
        public int LookupStatus { get; }
        public int ResolveStatus { get; }
        public bool Bound { get; }
        public SampleRead Caller { get; }
        public SampleRead ResolvedRead { get; }
        public SampleRead[] Readers { get; }

        public Observation(Fixture fixture, byte[] expected, byte[] original, Observation? baseline = null)
        {
            LookupStatus = calls.FindUnique(fixture.Reference, Service, Account, out IntPtr unique);
            IntPtr resolved = IntPtr.Zero, resolvedId = IntPtr.Zero;
            try
            {
                if (LookupStatus == 0 && unique != IntPtr.Zero)
                {
                    if (calls.CopyPersistentReference(unique, out IntPtr persistent) == 0)
                    {
                        Persistent = persistent;
                    }
                    if (calls.CopyAccessSnapshot(unique, out IntPtr policy) == 0)
                    {
                        Policy = policy;
                    }
                    ProtectedValue = calls.DataStamp(unique);
                }
                IntPtr anchor = baseline is null ? Persistent : baseline.Persistent;
                ResolveStatus = calls.ResolvePassword(anchor, fixture.Reference, Service, Account, out resolved);
                Bound = ResolveStatus == 0 && resolved != IntPtr.Zero
                    && calls.CopyPersistentReference(resolved, out resolvedId) == 0 && Same(anchor, resolvedId)
                    && Same(anchor, Persistent) && ProtectedValue is not null
                    && ProtectedValue.AsSpan().SequenceEqual(calls.DataStamp(resolved));
                Caller = ReadSample(unique, expected, original);
                ResolvedRead = ReadSample(resolved, expected, original);
                Readers = fixture.ProbeReaders(expected, original);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                calls.Release(resolvedId);
                calls.Release(resolved);
                calls.Release(unique);
            }
        }

        public bool Same(IntPtr first, IntPtr second) =>
            first != IntPtr.Zero && second != IntPtr.Zero && calls.Equal(first, second);

        public bool SameComponent(Observation other, int index) =>
            Policy != IntPtr.Zero && other.Policy != IntPtr.Zero
            && Same(CFArrayGetValueAtIndex(Policy, index), CFArrayGetValueAtIndex(other.Policy, index));

        public bool HasIntegrity => Policy != IntPtr.Zero && CFBagGetCount(CFArrayGetValueAtIndex(Policy, 3)) == 1;
        public bool HasPartitions => Policy != IntPtr.Zero && CFBagGetCount(CFArrayGetValueAtIndex(Policy, 2)) == 1;
        public IEnumerable<SampleRead> SuccessfulReads =>
            Readers.Append(Caller).Append(ResolvedRead).Where(read => read.Status == 0);

        public void Dispose()
        {
            calls.Release(Policy);
            calls.Release(Persistent);
        }
    }

    private static SampleRead ReadSample(IntPtr item, byte[] expected, byte[] original)
    {
        int status = TimesFixtureRead(item, expected, (uint)expected.Length, original, (uint)original.Length,
            out int matches, out int retained);
        return (status, matches == 1, retained == 1);
    }

    private static bool ReadDenied(int status) => IsAuthorizationDenied(status) || status is -25308 or -25243 or -128;

    private void Compare(string phase, string label, bool value) => ReportStatus(phase, label, value ? 1 : 0);

    private void AssertObservation(string phase, Observation before, Observation after, bool oneTimeRead,
        bool requireOriginal, bool requirePhysical)
    {
        bool logical = before.Bound && after.Bound && before.Same(before.Persistent, after.Persistent);
        bool originalEnvelope = before.ProtectedValue is not null
            && before.ProtectedValue.AsSpan().SequenceEqual(after.ProtectedValue);
        bool completeAccess = before.Same(before.Policy, after.Policy);
        bool owner = before.SameComponent(after, 0);
        bool entries = before.SameComponent(after, 1);
        bool partitions = before.HasPartitions && after.HasPartitions && before.SameComponent(after, 2);
        bool rights = before.SameComponent(after, 4);
        bool prompts = before.SameComponent(after, 5);
        // A successful normal password read verifies the provider's integrity protection.
        bool integrityRule = before.HasIntegrity && after.HasIntegrity && before.SameComponent(after, 3);
        bool integrity = integrityRule && before.SuccessfulReads.Any() && after.SuccessfulReads.Any();
        int[] callerStatuses = [before.Caller.Status, after.Caller.Status, before.ResolvedRead.Status, after.ResolvedRead.Status];
        bool caller = oneTimeRead ? callerStatuses.All(ReadDenied) : callerStatuses.All(status => status == 0);
        bool readers = caller && before.Readers.Length == after.Readers.Length
            && before.Readers[..^1].All(read => read.Status == 0) && after.Readers[..^1].All(read => read.Status == 0);
        bool excluded = ReadDenied(before.Readers[^1].Status) && ReadDenied(after.Readers[^1].Status);
        bool original = after.SuccessfulReads.Any() && after.SuccessfulReads.All(read => read.Original);
        bool value = after.SuccessfulReads.Any() && after.SuccessfulReads.All(read => read.Matches);

        ReportStatus(phase, "find password record", after.LookupStatus);
        ReportStatus(phase, "resolve persistent reference", after.ResolveStatus);
        void ReportReads(string point, Observation view)
        {
            ReportStatus(phase, point + " caller read", view.Caller.Status);
            ReportStatus(phase, point + " persistent read", view.ResolvedRead.Status);
            for (int i = 0; i < view.Readers.Length - 1; i++)
            {
                ReportStatus(phase, point + (i == 0 ? " first reader" : " second reader"), view.Readers[i].Status);
            }
            ReportStatus(phase, point + " excluded reader", view.Readers[^1].Status);
        }
        ReportReads("before", before);
        ReportReads("after", after);
        Compare(phase, "persistent reference resolves", logical);
        Compare(phase, "control identity equal", logical);
        Compare(phase, "original protected value retained", originalEnvelope);
        Compare(phase, "control access equal", completeAccess);
        Compare(phase, "ordinary entries equal", entries);
        Compare(phase, "reader access equal", readers);
        Compare(phase, "partition set equal", partitions);
        Compare(phase, "authorization tags equal", rights);
        Compare(phase, "prompt policy equal", prompts);
        Compare(phase, "owner policy equal", owner);
        Compare(phase, "integrity policy equal", integrity);
        Compare(phase, "excluded reader refused", excluded);
        Compare(phase, "original data retained", original);
        Compare(phase, "fixture value equal", value);

        Assert.True(before.SuccessfulReads.Any() && before.SuccessfulReads.All(read => read.Original),
            "The original fixture value was not verified.");
        Assert.True(logical, "The original persistent reference and unique scoped lookup did not identify the same item.");
        Assert.True(owner && entries && rights && prompts && integrityRule, "A fixture policy component changed.");
        Assert.True(caller && excluded, "Unexpected caller or excluded-reader authorization.");
        Assert.True(partitions && integrity && readers, "Fixture partitions, integrity or reader access changed.");
        Assert.True(requireOriginal ? original : value, "The expected fixture value was not verified.");
        if (requirePhysical)
        {
            Assert.True(originalEnvelope && completeAccess, "Refusal changed the original protected value or access entries.");
        }
    }

    private void ObserveStaged(Fixture fixture, Observation before, string phase, IntPtr live, byte[] der,
        bool committing, IssuerEvidence evidence)
    {
        MacKeychainItem.NativeCalls calls = new();
        int search = calls.FindUnique(fixture.Reference, Service, Account, out IntPtr unique);
        int resolve = calls.ResolvePassword(before.Persistent, fixture.Reference, Service, Account, out IntPtr resolved);
        IntPtr writtenId = IntPtr.Zero, livePolicy = IntPtr.Zero;
        bool resolvedProtectionMissing = false;
        try
        {
            ReportStatus(phase, "staged unique lookup", search);
            ReportStatus(phase, "staged persistent lookup", resolve);
            int idStatus = calls.CopyPersistentReference(live, out writtenId);
            int policyStatus = calls.CopyAccessSnapshot(live, out livePolicy);
            ReportStatus(phase, "staged write identity", idStatus);
            ReportStatus(phase, "staged live policy", policyStatus);
            byte[]? writtenStamp = calls.DataStamp(live, der.Length);
            bool uniqueVerified = search == 0 && VerifyView("unique", unique, writtenStamp);
            bool resolvedVerified = resolve == 0 && VerifyView("resolved", resolved, writtenStamp);
            if (committing)
            {
                Assert.True(idStatus == 0 && before.Same(before.Persistent, writtenId)
                    && policyStatus == 0 && uniqueVerified && resolvedVerified,
                    "The staged write failed independent verification.");
                evidence.VerifiedStagedWrite();
            }
            else if (resolve == 0 && resolved != IntPtr.Zero && search == -25300 && policyStatus == -25300
                && resolvedProtectionMissing)
            {
                evidence.ObservedMissingStagedView(phase);
            }
        }
        finally
        {
            calls.Release(writtenId);
            calls.Release(livePolicy);
            calls.Release(unique);
            calls.Release(resolved);
        }

        bool VerifyView(string reference, IntPtr item, byte[]? writtenStamp)
        {
            if (item == IntPtr.Zero)
            {
                return false;
            }
            int policyStatus = calls.CopyAccessSnapshot(item, out IntPtr policy);
            int idStatus = calls.CopyPersistentReference(item, out IntPtr id);
            try
            {
                ReportStatus(phase, "staged " + reference + " policy", policyStatus);
                bool identity = idStatus == 0 && before.Same(id, writtenId) && before.Same(id, before.Persistent);
                byte[]? stamp = calls.DataStamp(item, der.Length);
                bool sameValue = writtenStamp is not null && stamp is not null && writtenStamp.AsSpan().SequenceEqual(stamp);
                SampleRead read = ReadSample(item, der, fixture.Legacy!);
                ReportStatus(phase, "staged " + reference + " read", read.Status);
                if (reference == "resolved")
                {
                    resolvedProtectionMissing = policyStatus == -25300 && read.Status == -25300;
                }
                // The normal authorized read authenticates the protected data. The new
                // digest may differ from the original, but both staged views must agree.
                bool effective = policyStatus == 0 && before.HasIntegrity && before.HasPartitions
                    && before.Same(policy, livePolicy) && Enumerable.Range(0, 6).All(i => before.Same(
                        CFArrayGetValueAtIndex(before.Policy, i), CFArrayGetValueAtIndex(policy, i)));
                Compare(phase, "staged " + reference + " identity equal", identity);
                Compare(phase, "staged " + reference + " protected value equal", sameValue);
                Compare(phase, "staged " + reference + " DER equal", read.Status == 0 && read.Matches);
                Compare(phase, "staged " + reference + " effective access equal", effective && read.Status == 0);
                return identity && sameValue && effective && read.Status == 0 && read.Matches;
            }
            finally
            {
                calls.Release(id);
                calls.Release(policy);
            }
        }
    }

    [MacKeychainTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AccessPolicy_ComparedByOwnerEntryTagDelegationAndRight(int field)
    {
        MacKeychainItem.NativeCalls calls = new();
        IntPtr first = IntPtr.Zero, other = IntPtr.Zero, firstPolicy = IntPtr.Zero, otherPolicy = IntPtr.Zero;
        try
        {
            Assert.Equal(0, TimesFixtureAccessVariant(0, out first));
            Assert.Equal(0, TimesFixtureAccessVariant(field, out other));
            Assert.Equal(0, TimesCopyAccessPolicy(first, out firstPolicy));
            Assert.Equal(0, TimesCopyAccessPolicy(other, out otherPolicy));
            Assert.Equal(field == 0, calls.Equal(firstPolicy, otherPolicy));
            Assert.Equal(field != 1, calls.Equal(CFArrayGetValueAtIndex(firstPolicy, 0), CFArrayGetValueAtIndex(otherPolicy, 0)));
            Assert.Equal(field is 0 or 1, calls.Equal(CFArrayGetValueAtIndex(firstPolicy, 1), CFArrayGetValueAtIndex(otherPolicy, 1)));
        }
        finally
        {
            calls.Release(otherPolicy);
            calls.Release(firstPolicy);
            calls.Release(other);
            calls.Release(first);
        }
    }

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixtureAccessVariant(int field, out IntPtr access);

    [MacKeychainTheory]
    [InlineData(0, 1)] // small descriptor
    [InlineData(1, 0)] // entries
    [InlineData(2, 0)] // aggregate nodes
    [InlineData(3, 0)] // aggregate bytes
    [InlineData(4, 0)] // depth
    public void AccessPolicy_BoundsTheCompleteInMemoryDescriptor(int kind, int fits) =>
        Assert.Equal(fits, TimesFixturePolicyFits(TimesPolicyFits, kind));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool PolicyFits(IntPtr owner, uint count, IntPtr entries);

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixturePolicyFits(PolicyFits fits, int kind);

    [DllImport("TimesIssuerKeychain")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool TimesPolicyFits(IntPtr owner, uint count, IntPtr entries);

    [DllImport("TimesIssuerKeychain")]
    private static extern int TimesCopyAccessPolicy(IntPtr access, out IntPtr policy);

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixtureRead(IntPtr item, byte[] expected, uint expectedSize, byte[] original,
        uint originalSize, out int matches, out int retained);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFBagGetCount(IntPtr bag);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRetain(IntPtr value);
}
