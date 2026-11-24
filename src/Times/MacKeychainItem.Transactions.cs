using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
        private readonly Action<string, int>? reportFailure;

        public NativeCalls(Action<string, int>? reportFailure = null)
        {
            this.reportFailure = reportFailure;
        }

        private int Report(string stage, int status)
        {
            if (status != 0)
            {
                reportFailure?.Invoke(stage, status);
            }

            return status;
        }

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
            Check(Report("identify database path", SecKeychainGetPath(keychain, ref length, path)), SaveFailed);
            return path.AsSpan(0, (int)length).ToArray();
        }

        private unsafe DatabaseHandle FileDatabase(IntPtr keychain)
        {
            Check(Report("get database", SecKeychainGetDLDBHandle(keychain, out DatabaseHandle keychainDb)), SaveFailed);
            DatabaseHandle fileDb = default;
            Check(Report("get file database", CSSM_DL_PassThrough(keychainDb, GetFileDatabase, 0, (IntPtr)(&fileDb))), SaveFailed);
            return fileDb;
        }

        public IFileTransaction BeginTransaction(IntPtr keychain)
        {
            SafeFileHandle? guard = OpenLocalLock(KeychainPath(keychain), lookup: false);
            try
            {
                return new FileTransaction(FileDatabase(keychain), guard, this);
            }
            catch
            {
                guard?.Dispose();
                throw;
            }
        }

        public IDisposable LockForLookup(IntPtr keychain, byte[] path)
        {
            return (IDisposable?)OpenLocalLock(path, lookup: true) ?? BeginTransaction(keychain);
        }

        // Security.framework's nested password operations leave the commit to the caller
        // when autocommit is off. Unmodified databases only release their file lock.
        private sealed class FileTransaction : IFileTransaction
        {
            private readonly DatabaseHandle fileDb;
            private readonly SafeFileHandle? guard;
            private readonly NativeCalls calls;
            private bool committed;

            public unsafe FileTransaction(DatabaseHandle fileDb, SafeFileHandle? guard, NativeCalls calls)
            {
                this.fileDb = fileDb;
                this.guard = guard;
                this.calls = calls;
                nint wasAutocommit = 0;
                Check(calls.Report("disable autocommit", CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 0,
                    (IntPtr)(&wasAutocommit))), SaveFailed);
                if (wasAutocommit == 0)
                {
                    throw new TokenStoreException(SaveFailed);
                }

                try
                {
                    Check(calls.Report("lock database", CSSM_DL_PassThrough(fileDb, TakeFileLock, 0, 0)), SaveFailed);
                    if (guard is not null)
                    {
                        Check(calls.Report("verify writer lock", CheckWriterLock(guard)), SaveFailed);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Commit()
            {
                Check(calls.Report("commit database", CSSM_DL_PassThrough(fileDb, CommitDatabase, 0, 0)), SaveFailed);
                committed = true;
            }

            public void Dispose()
            {
                if (!committed)
                {
                    CSSM_DL_PassThrough(fileDb, Rollback, 0, 0);
                }

                CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 1, 0);
                guard?.Dispose();
            }
        }

        public int UpdateData(IntPtr owner, IntPtr item, ReadOnlyMemory<byte> data)
        {
            int status = UpdateProtectedPassword(owner, item, data.Span, (uint)data.Length, out IntPtr stage);
            return status == 0 ? 0 : Report(Marshal.PtrToStringAnsi(stage) ?? "update password", status);
        }

        public byte[]? DataStamp(IntPtr item)
        {
            byte[] digest = new byte[32];
            try
            {
                int status = PasswordStamp(item, digest, out IntPtr stage);
                if (status == 0)
                {
                    return digest;
                }

                Report(Marshal.PtrToStringAnsi(stage) ?? "read record stamp", status);
                return null;
            }
            catch (Exception ex) when (ex is TypeLoadException or BadImageFormatException)
            {
                Report("load stamp bridge", ex.HResult);
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
                return WaitForProbe(child, ProbeTimeout) switch
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

        internal static int WaitForProbe(Process child, TimeSpan timeout)
        {
            // The probe reports presence by exit code. Output is never needed.
            if (child.WaitForExit(timeout))
            {
                return child.ExitCode;
            }

            try
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(TimeSpan.FromSeconds(1));
            }
            catch (InvalidOperationException) when (child.HasExited)
            {
                // It exited between the deadline and the kill.
            }

            throw new TokenStoreException(SaveFailed);
        }

        internal static int Probe(string path, string service, string account)
        {
            if (!LicenceKey.IsValidKeyId(account) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            {
                return 1;
            }

            byte[] expectedPath = Encoding.UTF8.GetBytes(path);
            byte[] terminatedPath = [.. expectedPath, 0];
            using SafeFileHandle database = new(OpenProbeDatabase(terminatedPath), ownsHandle: true);
            if (database.IsInvalid || SecKeychainSetUserInteractionAllowed(0) != 0
                || SecKeychainOpen(path, out IntPtr keychain) != 0 || keychain == IntPtr.Zero)
            {
                return 1;
            }

            try
            {
                const uint ReadPermission = 2; // kSecReadPermStatus
                if (SecKeychainGetStatus(keychain, out uint status) != 0 || (status & ReadPermission) == 0
                    || SecKeychainGetKeychainVersion(keychain, out uint version) != 0 || version == 0
                    || !new NativeCalls().KeychainPath(keychain).AsSpan().SequenceEqual(expectedPath))
                {
                    return 1;
                }

                bool found = Default.Exists(keychain, service, account);
                return DatabaseAtPath(database, terminatedPath) == 0 ? (found ? ProbeFound : ProbeMissing) : 1;
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
        private static partial int UpdateProtectedPassword(IntPtr owner, IntPtr item, ReadOnlySpan<byte> data, uint length,
            out IntPtr stage);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesPasswordStamp")]
        private static partial int PasswordStamp(IntPtr item, [Out] byte[] digest, out IntPtr stage);

        [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int SecKeychainOpen(string path, out IntPtr keychain);

        [LibraryImport(Security)]
        private static partial int SecKeychainSetUserInteractionAllowed(byte allowed);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetStatus(IntPtr keychain, out uint status);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetKeychainVersion(IntPtr keychain, out uint version);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetPath(IntPtr keychain, ref uint length, [Out] byte[] path);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);
    }
}
