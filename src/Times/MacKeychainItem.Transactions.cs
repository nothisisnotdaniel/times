using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        public byte[] KeychainPath(IntPtr keychain, string failure = SaveFailed)
        {
            byte[] path = new byte[1024]; // macOS PATH_MAX, including the terminating NUL
            uint length = (uint)path.Length;
            Check(Report("identify database path", SecKeychainGetPath(keychain, ref length, path)), failure);
            return path.AsSpan(0, (int)length).ToArray();
        }

        private unsafe DatabaseHandle FileDatabase(IntPtr keychain)
        {
            Check(Report("get database", SecKeychainGetDLDBHandle(keychain, out DatabaseHandle keychainDb)), SaveFailed);
            DatabaseHandle fileDb = default;
            Check(Report("get file database", CSSM_DL_PassThrough(keychainDb, GetFileDatabase, 0, (IntPtr)(&fileDb))), SaveFailed);
            return fileDb;
        }

        public void WithoutInteraction(Action action)
        {
            // This setting is shared by Keychain Services calls in the issuer process.
            // Cover selection, the first authorization and any subsequent verification.
            lock (TransactionLock)
            {
                Check(Report("get interaction", SecKeychainGetUserInteractionAllowed(out byte allowed)), MigrationFailed);
                if (allowed != 0)
                {
                    Check(Report("disable interaction", SecKeychainSetUserInteractionAllowed(0)), MigrationFailed);
                }
                try
                {
                    action();
                }
                finally
                {
                    if (allowed != 0)
                    {
                        Check(Report("restore interaction", SecKeychainSetUserInteractionAllowed(allowed)), MigrationFailed);
                    }
                }
            }
        }

        public void WithStableRead(IntPtr keychain, Action read)
        {
            // FileDL publishes each commit by renaming its temporary file. Pinning
            // the inode detects even a change back to the original policy between
            // observations. The descriptor never supplies the password or the ACL.
            byte[] path = [.. KeychainPath(keychain, MigrationFailed), 0];
            using SafeFileHandle version = new(OpenProbeDatabase(path), ownsHandle: true);
            if (version.IsInvalid)
            {
                throw new TokenStoreException(MigrationFailed);
            }
            read();
            Check(Report("verify database version", DatabaseAtPath(version, path)), MigrationFailed);
        }

        public int VerifyCommittedView(IntPtr keychain, string service, string account, IntPtr persistent, IntPtr policy,
            byte[]? stamp)
        {
            byte[] expected = new byte[SHA256.HashSizeInBytes];
            if (stamp is not { Length: SHA256.HashSizeInBytes } || ReadViewDigest(persistent, policy, stamp, expected) != 0)
            {
                return Report("verify committed item", -25293);
            }
            // A stable pathname alone cannot detect a previously cached FileDL view.
            // The child opens only this database, after the parent has pinned its revision.
            try
            {
                string executable = Path.Combine(AppContext.BaseDirectory, "TimesKeychainView");
                if (!IsVerifierImage(executable, 0))
                {
                    return Report("verify committed item", -25293);
                }
                ProcessStartInfo info = new(executable)
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
                };
                info.Environment.Clear();
                info.ArgumentList.Add(KeychainFilename(KeychainPath(keychain, MigrationFailed)));
                info.ArgumentList.Add(service);
                info.ArgumentList.Add(account);
                using Process child = Process.Start(info) ?? throw new TokenStoreException(MigrationFailed);
                bool accepted = VerifyReply(IsVerifierImage(executable, child.Id), expected,
                    child.StandardInput.BaseStream, () => WaitForProbe(child, ProbeTimeout));
                return Report("verify committed item", accepted ? 0 : -25293);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or DecoderFallbackException or TokenStoreException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                return Report("verify committed item", -25293);
            }
        }

        internal virtual bool IsVerifierImage(string executable, int pid) => CheckVerifierImage(executable, pid) == 0;

        // The fresh process waits for input. Only a live, build-bound image may
        // receive the comparison digest and supply the metadata result.
        internal static bool VerifyReply(bool authenticated, ReadOnlySpan<byte> digest, Stream request, Func<int> waitForExit)
        {
            using (request)
            {
                if (authenticated)
                {
                    request.Write(digest);
                }
            }
            int status = waitForExit();
            return authenticated && status == ProbeFound;
        }

        public virtual IFileTransaction BeginTransaction(IntPtr keychain)
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
            private bool committed;

            public unsafe FileTransaction(DatabaseHandle fileDb, SafeFileHandle? guard, NativeCalls calls)
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

        public virtual int CopyAccessSnapshot(IntPtr item, out IntPtr snapshot) =>
            Report("copy access", CopyPasswordAccess(item, out snapshot));

        public virtual int CopyLegacyReadPolicy(IntPtr item, bool writable, out IntPtr snapshot) =>
            Report("check legacy read policy", CopyLegacyPolicy(item, writable ? 1 : 0, out snapshot));

        public int CheckMigrationReader(IntPtr item, IntPtr policy, byte[] expected) =>
            Report("check migration reader", CheckOriginalReader(item, policy, expected, (nuint)expected.Length));

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
                ProcessStartInfo info = new(apphost)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(ProbeArgument);
                info.ArgumentList.Add(KeychainFilename(path));
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

        private static string KeychainFilename(byte[] path) => new(new UTF8Encoding(false, true).GetChars(path));

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
                    || CheckPasswordDatabase(keychain) != 0
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

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesReadViewDigest")]
        private static partial int ReadViewDigest(IntPtr persistent, IntPtr policy, ReadOnlySpan<byte> stamp, Span<byte> digest);

        [LibraryImport(CoreFoundation)]
        private static partial IntPtr CFRetain(IntPtr value);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length,
            ReadOnlySpan<byte> data);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCopyPasswordAccess")]
        private static partial int CopyPasswordAccess(IntPtr item, out IntPtr snapshot);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckMigrationReader")]
        private static partial int CheckOriginalReader(IntPtr item, IntPtr policy, ReadOnlySpan<byte> expected, nuint length);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCopyLegacyReadPolicy")]
        private static partial int CopyLegacyPolicy(IntPtr item, int writable, out IntPtr snapshot);

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

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckVerifierImage", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int CheckVerifierImage(string path, int pid);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetPath(IntPtr keychain, ref uint length, [Out] byte[] path);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);
    }
}
