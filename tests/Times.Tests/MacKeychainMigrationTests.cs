using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Times.LicenceIssuer;

namespace Times.Tests;

public sealed class MacKeychainMigrationTests
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = MacKeychainSigningKeyStore.Service;
    private const string Account = "maker";
    private const string FixturePassword = "temporary-test-keychain";

    // Both commands explicitly target the private temporary keychain. security dump-keychain -a
    // reads the ACL independently of the production code and does not request the password data.
    [MacKeychainTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMigration_IssuesTwiceWithOriginalAccessOrPreservesCustomPartitions(bool addReaderPartition)
    {
        using TempFolder folder = new TempFolder();
        string path = Path.Combine(folder.Path, "issuer-test.keychain");
        byte[] password = Encoding.UTF8.GetBytes(FixturePassword);
        Assert.Equal(0, SecKeychainCreate(path, (uint)password.Length, password, 0, 0, out IntPtr keychain));
        try
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] der = key.ExportPkcs8PrivateKey();
            byte[] legacy = new byte[Base64.GetMaxEncodedToUtf8Length(der.Length)];
            Assert.Equal(OperationStatus.Done, Base64.EncodeToUtf8(der, legacy, out _, out _));
            byte[] service = Encoding.UTF8.GetBytes(Service);
            byte[] account = Encoding.UTF8.GetBytes(Account);
            Assert.Equal(0, SecKeychainAddGenericPassword(keychain, (uint)service.Length, service, (uint)account.Length,
                account, (uint)legacy.Length, legacy, 0));

            string[] before = AccessEntries(path);
            string partition = Assert.Single(before, entry => entry.Contains(" partition_id", StringComparison.Ordinal));
            Assert.Contains(before, entry => entry.Contains(" decrypt", StringComparison.Ordinal));
            if (addReaderPartition)
            {
                string ids = partition.Split('\n').Single(line => line.TrimStart().StartsWith("description:", StringComparison.Ordinal))
                    .Trim()["description:".Length..].Replace(" ", "", StringComparison.Ordinal);
                RunSecurity("set-generic-password-partition-list", "-s", Service, "-a", Account, "-S",
                    ids + ",teamid:TESTREADER", "-k", FixturePassword, path);
                before = AccessEntries(path);
                Assert.Contains(before, entry => entry.Contains("teamid:TESTREADER", StringComparison.Ordinal));
            }

            string apphost = Path.Combine(folder.Path, "Times.LicenceIssuer");
            File.WriteAllBytes(apphost, []);
            TestKeychainStore store = new TestKeychainStore(apphost, keychain);
            LicencePublicKeys original = LicencePublicKeys.FromTable([(Account, key.ExportSubjectPublicKeyInfoPem())]);
            string ledger = Path.Combine(folder.Path, "ledger.tsv");
            Assert.Equal(0, SecKeychainGetUserInteractionAllowed(out byte previous));
            Assert.Equal(0, SecKeychainSetUserInteractionAllowed(0));
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    using StringWriter output = new StringWriter();
                    using StringWriter error = new StringWriter();
                    using LicenceIssuerTests.CountingRandom random = new LicenceIssuerTests.CountingRandom();
                    Issuer issuer = new Issuer(output, error, store, random, FixedTimeProvider.At20261027(), ledger);
                    int result = issuer.Run(["issue", "--kid", Account, "--name", "Ada", "--until", "2027-12-31"]);
                    if (addReaderPartition && result == 1)
                    {
                        Assert.Empty(output.ToString());
                        Assert.Equal("Signing-key migration failed. Check Keychain write access and retry." + error.NewLine,
                            error.ToString());
                        Assert.False(File.Exists(ledger));
                        Assert.Equal(legacy, MacKeychainItem.Default.Read(keychain, Service, Account, 16384,
                            static (_, data) => data.ToArray()));
                        Assert.Equal(before, AccessEntries(path));
                        return;
                    }

                    Assert.Equal(0, result);
                    Assert.Empty(error.ToString());
                    Assert.True(LicenceKey.TryVerify(output.ToString().Trim(), original, LicenceRevocations.None).IsValid);
                    Assert.Equal(der, MacKeychainItem.Default.Read(keychain, Service, Account, 16384,
                        static (_, data) => data.ToArray()));
                    Assert.Equal(before, AccessEntries(path));
                }

                Assert.Equal(2, File.ReadAllLines(ledger).Length);
                AssertLookupLocksTheSameFileAsAWriter(keychain, folder.Path);
            }
            finally
            {
                Assert.Equal(0, SecKeychainSetUserInteractionAllowed(previous));
            }
        }
        finally
        {
            int status = SecKeychainDelete(keychain);
            CFRelease(keychain);
            Assert.Equal(0, status);
        }
    }

    private static void AssertLookupLocksTheSameFileAsAWriter(IntPtr keychain, string directory)
    {
        MacKeychainItem.NativeCalls native = new MacKeychainItem.NativeCalls();
        string writerLock;
        using (native.BeginTransaction(keychain))
        {
            writerLock = Assert.Single(BusyLockFiles(directory));
        }

        using (IDisposable guard = native.LockForLookup(keychain, native.KeychainPath(keychain)))
        {
            Assert.IsType<SafeFileHandle>(guard); // a read-only guard on the local temporary volume
            Assert.Equal([writerLock], BusyLockFiles(directory));
        }

        Assert.Empty(BusyLockFiles(directory));
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
        private readonly IntPtr keychain;

        public TestKeychainStore(string apphost, IntPtr keychain)
        {
            store = new MacKeychainSigningKeyStore(apphost);
            this.keychain = keychain;
        }

        public bool IsSupported => store.IsSupported;
        public string? WriteRefusal => store.WriteRefusal;
        public byte[]? Read(string keyId) => store.Read(keychain, keyId);
        public bool Exists(string keyId) => throw new NotSupportedException();
        public bool TryCreate(string keyId, byte[] pkcs8) => throw new NotSupportedException();
    }

    private static string[] AccessEntries(string path)
    {
        string record = RunSecurity("dump-keychain", "-a", path).Split("keychain:")
            .Single(item => item.Contains(Service, StringComparison.Ordinal));
        // Entry order and the system's integrity digest can change on a password-data update.
        return Regex.Split(record, @"(?m)^    entry [0-9]+:\s*$").Skip(1)
            .Where(entry => !entry.Contains("): integrity", StringComparison.Ordinal))
            .Select(entry => entry.Trim()).Order(StringComparer.Ordinal).ToArray();
    }

    private static string RunSecurity(params string[] arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private sealed class MacKeychainTheoryAttribute : TheoryAttribute
    {
        public MacKeychainTheoryAttribute()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Skip = "Requires the macOS file Keychain.";
            }
        }
    }

    [DllImport(Security)]
    private static extern int SecKeychainCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint passwordLength,
        byte[] password, byte prompt, IntPtr initialAccess, out IntPtr keychain);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint length, byte[] data, IntPtr item);

    [DllImport(Security)]
    private static extern int SecKeychainGetUserInteractionAllowed(out byte state);

    [DllImport(Security)]
    private static extern int SecKeychainSetUserInteractionAllowed(byte state);

    [DllImport(Security)]
    private static extern int SecKeychainDelete(IntPtr keychain);

    [DllImport("libc", EntryPoint = "open")]
    private static extern int OpenReadOnly([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr reference);
}
