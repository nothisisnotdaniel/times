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
        internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
        private readonly Action<string, int>? reportStatus;

        public NativeCalls(Action<string, int>? reportStatus = null)
        {
            this.reportStatus = reportStatus;
        }

        private int Report(string stage, int status)
        {
            reportStatus?.Invoke(stage, status);
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

        public virtual IFileTransaction BeginTransaction(IntPtr keychain, bool nonInteractive = false)
        {
            bool restoreInteraction = false;
            SafeFileHandle? guard = null;
            try
            {
                if (nonInteractive)
                {
                    Check(Report("get interaction", SecKeychainGetUserInteractionAllowed(out byte allowed)), SaveFailed);
                    if (allowed != 0)
                    {
                        Check(Report("disable interaction", SecKeychainSetUserInteractionAllowed(0)), SaveFailed);
                        restoreInteraction = true;
                    }
                }
                guard = OpenLocalLock(KeychainPath(keychain), lookup: false);
                return new FileTransaction(FileDatabase(keychain), guard, this, restoreInteraction);
            }
            catch
            {
                guard?.Dispose();
                if (restoreInteraction)
                {
                    SecKeychainSetUserInteractionAllowed(1);
                }
                throw;
            }
        }

        public IDisposable GuardMatch(IntPtr keychain, string service, string account, string apphost,
            TimeSpan timeout, out bool found)
        {
            // Path discovery, mount inspection, provider locks and the fresh committed
            // lookup all run under one caller deadline. A late worker owns its reference.
            IntPtr retained = CFRetain(keychain);
            bool match = false;
            LookupTransaction guarded = LookupTransaction.Acquire(() =>
            {
                byte[] path = KeychainPath(retained);
                IDisposable guard = LockForLookup(retained, path);
                try
                {
                    match = CommittedMatch(path, service, account, apphost);
                    return guard;
                }
                catch
                {
                    guard.Dispose();
                    throw;
                }
            }, () => CFRelease(retained), timeout);
            found = match;
            return guarded;
        }

        internal virtual IDisposable LockForLookup(IntPtr keychain, byte[] path)
        {
            return (IDisposable?)OpenLocalLock(path, lookup: true) ?? BeginTransaction(keychain);
        }

        internal sealed class LookupTransaction : IDisposable
        {
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Task worker;

            private LookupTransaction(Func<IDisposable> acquire, Action releaseReference, TaskCompletionSource<bool> ready)
            {
                worker = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        using IDisposable transaction = acquire();
                        ready.TrySetResult(true);
                        release.Task.GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is TokenStoreException or TypeLoadException or BadImageFormatException)
                    {
                        ready.TrySetResult(false);
                    }
                    finally
                    {
                        ready.TrySetResult(false);
                        releaseReference();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            internal static LookupTransaction Acquire(Func<IDisposable> acquire, Action releaseReference, TimeSpan timeout)
            {
                TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                LookupTransaction guard;
                try
                {
                    guard = new LookupTransaction(acquire, releaseReference, ready);
                }
                catch
                {
                    releaseReference();
                    throw;
                }
                try
                {
                    if (ready.Task.Wait(timeout) && ready.Task.Result)
                    {
                        return guard;
                    }
                    throw new TokenStoreException(SaveFailed);
                }
                catch
                {
                    guard.release.TrySetResult();
                    throw;
                }
            }

            public void Dispose()
            {
                release.TrySetResult();
                // Late cleanup keeps the retained secondary alive on its own worker.
                // It must not prevent release of the remaining lookup guards.
                worker.Wait(ProbeTimeout);
            }
        }

        // Security.framework's nested password operations leave the commit to the caller
        // when autocommit is off. Unmodified databases only release their file lock.
        private sealed class FileTransaction : IFileTransaction
        {
            private readonly DatabaseHandle fileDb;
            private readonly SafeFileHandle? guard;
            private readonly NativeCalls calls;
            private readonly bool restoreInteraction;
            private bool committed;

            public unsafe FileTransaction(DatabaseHandle fileDb, SafeFileHandle? guard, NativeCalls calls, bool restoreInteraction)
            {
                this.fileDb = fileDb;
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
                this.guard = guard;
                this.restoreInteraction = restoreInteraction;
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
                if (restoreInteraction)
                {
                    SecKeychainSetUserInteractionAllowed(1);
                }
                guard?.Dispose();
            }
        }

        public int CopyAccessSnapshot(IntPtr item, out IntPtr snapshot) =>
            Report("copy access", CopyPasswordAccess(item, out snapshot));

        public int CheckAccess(IntPtr before, IntPtr after, IntPtr item, byte[] expected) =>
            Report("check access", CheckPasswordPolicy(before, after, item, expected, (nuint)expected.Length));

        public int CopyPersistentReference(IntPtr item, out IntPtr persistent) =>
            Report("persistent reference", CopyPasswordReference(item, out persistent));

        public int ResolvePassword(IntPtr persistent, IntPtr owner, string service, string account, out IntPtr item) =>
            Report("resolve persistent reference", ResolvePasswordReference(persistent, owner, service, account, out item));

        public virtual int UpdateData(IntPtr item, ReadOnlyMemory<byte> data) =>
            Report("provider update", SecKeychainItemModifyAttributesAndData(item, 0, (uint)data.Length, data.Span));

        public byte[]? DataStamp(IntPtr item, int expectedLength = -1)
        {
            byte[] digest = new byte[32];
            try
            {
                int status = PasswordStamp(item, expectedLength, digest, out IntPtr stage);
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
        private static bool CommittedMatch(byte[] path, string service, string account, string apphost)
        {
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

        [LibraryImport(CoreFoundation)]
        private static partial IntPtr CFRetain(IntPtr value);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length,
            ReadOnlySpan<byte> data);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCopyPasswordAccess")]
        private static partial int CopyPasswordAccess(IntPtr item, out IntPtr snapshot);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckPasswordPolicy")]
        private static partial int CheckPasswordPolicy(IntPtr before, IntPtr after, IntPtr item,
            ReadOnlySpan<byte> expected, nuint length);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCopyPasswordReference")]
        private static partial int CopyPasswordReference(IntPtr item, out IntPtr persistent);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesResolvePassword", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int ResolvePasswordReference(IntPtr persistent, IntPtr owner, string service,
            string account, out IntPtr item);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesPasswordStamp")]
        private static partial int PasswordStamp(IntPtr item, int expectedLength, [Out] byte[] digest, out IntPtr stage);

        [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int SecKeychainOpen(string path, out IntPtr keychain);

        [LibraryImport(Security)]
        private static partial int SecKeychainSetUserInteractionAllowed(byte allowed);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetUserInteractionAllowed(out byte allowed);

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
