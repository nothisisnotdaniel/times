using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Times;

internal sealed partial class MacKeychainItem
{
    internal interface IFileTransaction : IDisposable
    {
        public void Commit();
    }

    internal partial class NativeCalls
    {
        private const uint GetFileDatabase = 6; // CSSM_APPLECSPDL_DB_GET_HANDLE
        private const uint ToggleAutocommit = 0;
        private const uint CommitDatabase = 1;
        private const uint Rollback = 2;
        private const uint TakeFileLock = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct DatabaseHandle
        {
            public nint DataLayer;
            public nint Database;
        }

        public byte[] KeychainPath(IntPtr keychain)
        {
            byte[] path = new byte[1024]; // macOS PATH_MAX, including the terminating NUL
            uint length = (uint)path.Length;
            Check(SecKeychainGetPath(keychain, ref length, path), SaveFailed);
            return path.AsSpan(0, (int)length).ToArray();
        }

        private static unsafe DatabaseHandle FileDatabase(IntPtr keychain)
        {
            Check(SecKeychainGetDLDBHandle(keychain, out DatabaseHandle keychainDb), SaveFailed);
            DatabaseHandle fileDb = default;
            Check(CSSM_DL_PassThrough(keychainDb, GetFileDatabase, 0, (IntPtr)(&fileDb)), SaveFailed);
            return fileDb;
        }

        public IFileTransaction BeginTransaction(IntPtr keychain) => new FileTransaction(FileDatabase(keychain));

        public IDisposable LockForLookup(IntPtr keychain, byte[] path)
        {
            return (IDisposable?)TryLockLocalFile(path) ?? BeginTransaction(keychain);
        }

        // Security.framework's nested password operations leave the commit to the caller
        // when autocommit is off. Unmodified databases only release their file lock.
        private sealed class FileTransaction : IFileTransaction
        {
            private readonly DatabaseHandle fileDb;
            private bool committed;

            public unsafe FileTransaction(DatabaseHandle fileDb)
            {
                this.fileDb = fileDb;
                nint wasAutocommit = 0;
                Check(CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 0, (IntPtr)(&wasAutocommit)), SaveFailed);
                if (wasAutocommit == 0)
                {
                    throw new TokenStoreException(SaveFailed);
                }

                try
                {
                    Check(CSSM_DL_PassThrough(fileDb, TakeFileLock, 0, 0), SaveFailed);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Commit()
            {
                Check(CSSM_DL_PassThrough(fileDb, CommitDatabase, 0, 0), SaveFailed);
                committed = true;
            }

            public void Dispose()
            {
                if (!committed)
                {
                    CSSM_DL_PassThrough(fileDb, Rollback, 0, 0);
                }

                CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 1, 0);
            }
        }

        public int UpdateData(IntPtr owner, IntPtr item, ReadOnlyMemory<byte> data) =>
            UpdateProtectedPassword(owner, item, data.Span, (uint)data.Length);

        public byte[]? DataStamp(IntPtr item)
        {
            byte[] digest = new byte[32];
            try
            {
                return PasswordStamp(item, digest) == 0 ? digest : null;
            }
            catch (Exception ex) when (ex is TypeLoadException or BadImageFormatException)
            {
                return null;
            }
        }

        // The writer transaction refreshes its FileDL snapshot. A shared reader lock does
        // not, so resolve secondary matches in a process with no cached database view.
        public bool CommittedMatch(IntPtr keychain, string service, string account, string apphost)
        {
            byte[] path = KeychainPath(keychain);
            try
            {
                string filename = new(new UTF8Encoding(false, true).GetChars(path));
                ProcessStartInfo info = new(apphost)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(ProbeArgument);
                info.ArgumentList.Add(filename);
                info.ArgumentList.Add(service);
                info.ArgumentList.Add(account);
                using Process child = Process.Start(info) ?? throw new TokenStoreException(SaveFailed);
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                child.WaitForExit();
                return child.ExitCode switch
                {
                    ProbeFound => true,
                    ProbeMissing => false,
                    _ => throw new TokenStoreException(SaveFailed),
                };
            }
            catch (Exception ex) when (ex is Win32Exception or DecoderFallbackException or IOException)
            {
                throw new TokenStoreException(SaveFailed);
            }
        }

        internal const string ProbeArgument = "--probe-signing-key-id";
        private const int ProbeFound = 40;
        private const int ProbeMissing = 41;

        internal static int Probe(string path, string service, string account)
        {
            if (!Path.IsPathFullyQualified(path) || SecKeychainSetUserInteractionAllowed(0) != 0
                || SecKeychainOpen(path, out IntPtr keychain) != 0 || keychain == IntPtr.Zero)
            {
                return 1;
            }

            try
            {
                const uint ReadPermission = 2; // kSecReadPermStatus
                if (SecKeychainGetStatus(keychain, out uint status) != 0 || (status & ReadPermission) == 0)
                {
                    return 1;
                }

                return Default.Exists(keychain, service, account) ? ProbeFound : ProbeMissing;
            }
            catch (TokenStoreException)
            {
                return 1;
            }
            finally
            {
                CFRelease(keychain);
            }
        }

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesUpdateProtectedPassword")]
        private static partial int UpdateProtectedPassword(IntPtr owner, IntPtr item, ReadOnlySpan<byte> data, uint length);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesPasswordStamp")]
        private static partial int PasswordStamp(IntPtr item, [Out] byte[] digest);

        [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int SecKeychainOpen(string path, out IntPtr keychain);

        [LibraryImport(Security)]
        private static partial int SecKeychainSetUserInteractionAllowed(byte allowed);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetStatus(IntPtr keychain, out uint status);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetPath(IntPtr keychain, ref uint length, [Out] byte[] path);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);
    }
}
