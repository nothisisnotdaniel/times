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
        public IntPtr Physical { get; }
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
                    if (calls.CopyRecordId(unique, out IntPtr physical) == 0)
                    {
                        Physical = physical;
                    }
                }
                IntPtr anchor = baseline is null ? Persistent : baseline.Persistent;
                ResolveStatus = calls.ResolvePassword(anchor, fixture.Reference, Service, Account, out resolved);
                Bound = ResolveStatus == 0 && resolved != IntPtr.Zero
                    && calls.CopyRecordId(resolved, out resolvedId) == 0 && Same(Physical, resolvedId);
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
            calls.Release(Physical);
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
        bool requireOriginal, bool requirePhysical, bool authorizationCorrect = true)
    {
        bool logical = before.Bound && after.Bound;
        bool physical = before.Same(before.Physical, after.Physical);
        bool completeAccess = before.Same(before.Policy, after.Policy);
        bool owner = before.SameComponent(after, 0);
        bool entries = before.SameComponent(after, 1);
        bool partitions = before.HasPartitions && after.HasPartitions && before.SameComponent(after, 2);
        bool rights = before.SameComponent(after, 4);
        bool prompts = before.SameComponent(after, 5);
        // A successful normal password read verifies the provider's integrity protection.
        bool integrity = before.HasIntegrity && after.HasIntegrity && before.SameComponent(after, 3)
            && before.SuccessfulReads.Any() && after.SuccessfulReads.Any();
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
        Compare(phase, "control identity equal", physical);
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
        Assert.True(authorizationCorrect, "Unexpected provider authorization result.");
        Assert.True(logical, "The original persistent reference and unique scoped lookup did not identify the same item.");
        Assert.True(owner && entries && rights && prompts && partitions && integrity, "The effective fixture policy changed.");
        Assert.True(readers && excluded, "A fixture reader's authorization changed.");
        Assert.True(requireOriginal ? original : value, "The expected fixture value was not verified.");
        if (requirePhysical)
        {
            Assert.True(physical && completeAccess, "Refusal changed the original record or its access entries.");
        }
    }

    private void ObserveStaged(Fixture fixture, Observation before, string phase, IntPtr live)
    {
        MacKeychainItem.NativeCalls calls = new();
        int search = calls.FindUnique(fixture.Reference, Service, Account, out IntPtr unique);
        int resolve = calls.ResolvePassword(before.Persistent, fixture.Reference, Service, Account, out IntPtr resolved);
        IntPtr policy = IntPtr.Zero, livePolicy = IntPtr.Zero;
        try
        {
            ReportStatus(phase, "staged unique lookup", search);
            ReportStatus(phase, "staged persistent lookup", resolve);
            ReportStatus(phase, "staged live policy", calls.CopyAccessSnapshot(live, out livePolicy));
            Compare(phase, "staged live access equal", before.Same(before.Policy, livePolicy));
            byte[]? liveStamp = calls.DataStamp(live);
            Compare(phase, "staged live stamp available", liveStamp is not null);
            if (resolve == 0 && resolved != IntPtr.Zero)
            {
                ReportStatus(phase, "staged resolved policy", calls.CopyAccessSnapshot(resolved, out policy));
                Compare(phase, "staged resolved access equal", before.Same(livePolicy, policy));
                byte[]? resolvedStamp = calls.DataStamp(resolved);
                Compare(phase, "staged protected values equal", liveStamp is not null && resolvedStamp is not null
                    && liveStamp.AsSpan().SequenceEqual(resolvedStamp));
            }
            string[] components = ["owner", "ordinary entries", "partition", "integrity rule", "rights", "prompts", "integrity digest"];
            for (int i = 0; i < components.Length; i++)
            {
                Compare(phase, "staged " + components[i] + " equal", livePolicy != IntPtr.Zero && before.Policy != IntPtr.Zero
                    && before.Same(CFArrayGetValueAtIndex(before.Policy, i), CFArrayGetValueAtIndex(livePolicy, i)));
            }
        }
        finally
        {
            calls.Release(policy);
            calls.Release(livePolicy);
            calls.Release(unique);
            calls.Release(resolved);
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
