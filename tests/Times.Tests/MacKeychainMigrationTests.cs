using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    public void LegacyMigration_PreservesNativeAccessOrRollsBackIfMacOSDeniesRestoration(bool addReaderPartition)
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
            MacKeychainSigningKeyStore store = new MacKeychainSigningKeyStore(apphost);
            Assert.Equal(0, SecKeychainGetUserInteractionAllowed(out byte previous));
            Assert.Equal(0, SecKeychainSetUserInteractionAllowed(0));
            try
            {
                byte[] expected = der;
                try
                {
                    Assert.Equal(der, MacKeychainItem.Default.Read(keychain, Service, Account, 16384,
                        (item, data) => store.DecodeLegacy(item, data.ToArray())));
                }
                catch (TokenStoreException ex) when (addReaderPartition)
                {
                    // macOS may require the keychain password to restore an extra reader.
                    Assert.Equal("Signing-key migration failed. Check Keychain write access and retry.", ex.Message);
                    expected = legacy;
                }

                Assert.Equal(expected, MacKeychainItem.Default.Read(keychain, Service, Account, 16384,
                    static (_, data) => data.ToArray()));
                Assert.Equal(before, AccessEntries(path));
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

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr reference);
}
