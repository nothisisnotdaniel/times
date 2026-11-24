using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Times.Tests;

public sealed partial class MacKeychainMigrationTests
{
    [MacKeychainFact]
    public void DocumentedUpdate_SoleReader_PreservesAccessAndLogicalItem() =>
        MeasureSoleReaderPublicUpdate(useSecItem: false);

    [MacKeychainFact]
    public void SecItemUpdate_SoleReader_PreservesAccessAndLogicalItem() =>
        MeasureSoleReaderPublicUpdate(useSecItem: true);

    private void MeasureSoleReaderPublicUpdate(bool useSecItem)
    {
        using Fixture fixture = new();
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] sample = generated.ExportPkcs8PrivateKey();
        try
        {
            fixture.AddLegacy(sample, extra: false, oneTimeRead: false, allowWriter: true);
            using Observation before = new(fixture, fixture.Legacy!, fixture.Legacy!);
            MacKeychainItem.NativeCalls calls = new();
            Assert.Equal(0, calls.FindUnique(fixture.Reference, Service, Account, out IntPtr item));
            int status;
            try
            {
                status = useSecItem ? TimesFixtureUpdate(fixture.Reference, item, sample, (uint)sample.Length)
                    : SecKeychainItemModifyAttributesAndData(item, 0, (uint)sample.Length, sample);
            }
            finally
            {
                calls.Release(item);
            }
            ReportStatus("control-allowed", "control update", status);
            Assert.Equal(0, status);
            using Observation after = new(fixture, sample, fixture.Legacy!, before);
            AssertObservation("control-allowed", before, after, oneTimeRead: false,
                requireOriginal: false, requirePhysical: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sample);
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport("TimesKeychainFixture")]
    private static extern int TimesFixtureUpdate(IntPtr database, IntPtr item, byte[] data, uint length);

    private class FixtureWriteBoundary : MacKeychainItem.NativeCalls
    {
        private readonly bool nativeWritePermitted;
        private readonly Action<string, int> reportAttempt;

        public FixtureWriteBoundary(bool nativeWritePermitted, Action<string, int> reportStatus) : base(reportStatus)
        {
            this.nativeWritePermitted = nativeWritePermitted;
            reportAttempt = reportStatus;
        }

        public override int UpdateData(IntPtr item, ReadOnlyMemory<byte> data)
        {
            if (!nativeWritePermitted)
            {
                reportAttempt("provider update", -25293);
                throw new InvalidOperationException("A protected fixture reached the native data writer.");
            }
            return base.UpdateData(item, data);
        }
    }
}
