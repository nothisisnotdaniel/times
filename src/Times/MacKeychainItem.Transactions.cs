using System.ComponentModel;
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
                    RecoverPendingSessions(MigrationFailed);
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
            RecoverSession(keychain);
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
            int result = RunMetadataProbe(KeychainPath(keychain, MigrationFailed), service, account, expected);
            return Report("verify committed item", result == ProbeFound ? 0 : -25293);
        }

        private int RunMetadataProbe(byte[] path, string service, string account, byte[]? digest)
        {
            return BoundedProbe(Launch, (children, cancel) =>
            {
                using CancellationTokenRegistration stop = cancel.Register(children.Stop);
                byte[] message = digest ?? [1];
                return AuthenticatedReply(() => AcceptVerifierImage(WaitVerifier(children.Validator) == 0),
                    () => ResumeVerifier(children.Reader, children.Request, message, message.Length), cancel);
            }, ProbeTimeout);

            VerifierChildren? Launch(CancellationToken cancel)
            {
                string databasePath = KeychainFilename(path);
                using SafeFileHandle database = new(OpenProbeDatabase([.. path, 0]), ownsHandle: true);
                if (database.IsInvalid || cancel.IsCancellationRequested)
                {
                    return null;
                }
                string executable = Path.Combine(AppContext.BaseDirectory, "TimesKeychainView");
                if (StartVerifier(executable, databasePath, service, account, digest is null ? 1 : 0, database,
                    out int pid, out int request) != 0)
                {
                    return null;
                }
                // Until ownership is handed back, a timeout may stop the reader
                // even if spawning the system signature checker has stalled.
                bool transferred = false;
                try
                {
                    using CancellationTokenRegistration stop = cancel.Register(() => StopVerifier(pid));
                    if (cancel.IsCancellationRequested || StartImageCheck(pid, out int validator) != 0)
                    {
                        return null;
                    }
                    transferred = true;
                    return new VerifierChildren(pid, request, validator);
                }
                finally
                {
                    if (!transferred)
                    {
                        ReleaseVerifier(pid, request);
                    }
                }
            }
        }

        internal virtual bool AcceptVerifierImage(bool verified) => verified;

        private sealed class VerifierChildren : IDisposable
        {
            public int Reader { get; }
            public int Request { get; }
            public int Validator { get; }

            public VerifierChildren(int reader, int request, int validator)
            {
                Reader = reader;
                Request = request;
                Validator = validator;
            }

            public void Stop()
            {
                StopVerifier(Validator);
                StopVerifier(Reader);
            }

            public void Dispose()
            {
                Stop();
                ReleaseVerifier(Validator, -1);
                ReleaseVerifier(Reader, Request);
            }
        }

        internal static int AuthenticatedReply(Func<bool> verifySuspendedImage, Func<int> resumeAndRead,
            CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested || !verifySuspendedImage() || cancel.IsCancellationRequested)
            {
                return 1;
            }
            return resumeAndRead();
        }

        internal static int BoundedProbe<T>(Func<CancellationToken, T?> launch, Func<T, CancellationToken, int> check,
            TimeSpan timeout, TimeProvider? time = null) where T : class, IDisposable
        {
            using CancellationTokenSource deadline = new(timeout, time ?? TimeProvider.System);
            CancellationToken token = deadline.Token;
            Task<T?> preparing = Task.Run(() => launch(token), token);
            try
            {
                using T? children = TakeOwnership();
                if (children is null || token.IsCancellationRequested)
                {
                    return 1;
                }
                // Validation and the reply run in killable child processes. The
                // caller reaps both and closes the socket before returning.
                int result = check(children, token);
                return token.IsCancellationRequested ? 1 : result;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or DecoderFallbackException
                or TokenStoreException or InvalidOperationException or UnauthorizedAccessException
                or TypeLoadException or BadImageFormatException)
            {
                return 1;
            }

            T? TakeOwnership()
            {
                try
                {
                    return preparing.WaitAsync(token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // If launch finishes late, dispose its suspended child and
                    // validator. Only the caller can resume an authenticated child.
                    _ = preparing.ContinueWith(task =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            task.Result?.Dispose();
                        }
                        else
                        {
                            _ = task.Exception;
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return null;
                }
            }
        }

        public IFileTransaction? TryBeginReversibleMigration(IntPtr keychain)
        {
            // The file database handle belongs to Security.framework. Its rollback can fail
            // indefinitely and we cannot close the owning session to discard a staged update.
            DatabaseHandle file = FileDatabase(keychain);
            if (file.Database == 0 || file.DataLayer == 0)
            {
                throw new TokenStoreException(MigrationFailed);
            }
            Report("identified borrowed file database", 0);
            return null;
        }

        public virtual IFileTransaction BeginTransaction(IntPtr keychain)
        {
            RecoverSession(keychain);
            SafeFileHandle? guard = OpenLocalLock(KeychainPath(keychain), lookup: false);
            try
            {
                DatabaseHandle fileDb = FileDatabase(keychain);
                return StartFileTransaction(fileDb, guard, cleanup => HoldForRecovery(keychain, cleanup));
            }
            catch
            {
                guard?.Dispose();
                throw;
            }
        }

        public IDisposable GuardMatch(IntPtr keychain, string service, string account,
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
                    match = CommittedMatch(path, service, account);
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

        // A FileDL handle is borrowed from Security.framework. Keep its owner
        // alive when cleanup fails, and finish only our own outstanding transaction
        // before another issuer can read or write through this session.
        private static readonly Dictionary<IntPtr, FileTransaction> PendingCleanup = [];

        private static void HoldForRecovery(IntPtr keychain, FileTransaction transaction)
        {
            lock (PendingCleanup)
            {
                PendingCleanup.Add(CFRetain(keychain), transaction);
            }
        }

        private static void RecoverSession(IntPtr keychain, string failure = SaveFailed)
        {
            lock (PendingCleanup)
            {
                if (PendingCleanup.TryGetValue(keychain, out FileTransaction? pending))
                {
                    if (!pending.Recover())
                    {
                        throw new TokenStoreException(failure);
                    }
                    PendingCleanup.Remove(keychain);
                    CFRelease(keychain);
                }
            }
        }

        private static void RecoverPendingSessions(string failure)
        {
            lock (PendingCleanup)
            {
                foreach (IntPtr owner in PendingCleanup.Keys.ToArray())
                {
                    RecoverSession(owner, failure);
                }
            }
        }

        private unsafe FileTransaction StartFileTransaction(DatabaseHandle fileDb, SafeFileHandle? guard,
            Action<FileTransaction> hold)
        {
            nint wasAutocommit = 0;
            Check(Report("disable autocommit", CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 0,
                (IntPtr)(&wasAutocommit))), SaveFailed);
            if (wasAutocommit == 0)
            {
                throw new TokenStoreException(SaveFailed);
            }
            FileTransaction transaction = new(
                () => Report("commit database", CSSM_DL_PassThrough(fileDb, CommitDatabase, 0, 0)),
                () => Report("rollback database", CSSM_DL_PassThrough(fileDb, Rollback, 0, 0)),
                () => Report("restore autocommit", CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 1, 0)), hold, guard);
            try
            {
                Check(Report("lock database", CSSM_DL_PassThrough(fileDb, TakeFileLock, 0, 0)), SaveFailed);
                if (guard is not null)
                {
                    Check(Report("verify writer lock", CheckWriterLock(guard)), SaveFailed);
                }
                return transaction;
            }
            catch
            {
                transaction.Dispose();
                throw;
            }
        }

        internal sealed class FileTransaction : IFileTransaction
        {
            private readonly Func<int> commit, rollback, restore;
            private readonly Action<FileTransaction> hold;
            private readonly IDisposable? guard;
            private bool needsRollback = true;
            private bool committed;

            internal FileTransaction(Func<int> commit, Func<int> rollback, Func<int> restore,
                Action<FileTransaction> hold, IDisposable? guard)
            {
                this.commit = commit;
                this.rollback = rollback;
                this.restore = restore;
                this.hold = hold;
                this.guard = guard;
            }

            public void Commit()
            {
                Check(commit(), SaveFailed);
                needsRollback = false;
                committed = true;
            }

            // This is the same retained FileDL session. Until rollback succeeds,
            // never enable autocommit or release it to a subsequent operation.
            internal bool Recover() => SpinWait.SpinUntil(() => FinishCleanup() == 0, ProbeTimeout);

            private int FinishCleanup()
            {
                if (needsRollback)
                {
                    int status = rollback();
                    if (status != 0)
                    {
                        return status;
                    }
                    needsRollback = false;
                }
                return restore();
            }

            public void Dispose()
            {
                try
                {
                    int status = FinishCleanup();
                    if (status != 0)
                    {
                        status = FinishCleanup();
                    }
                    if (status != 0)
                    {
                        hold(this);
                        if (!committed)
                        {
                            Check(status, SaveFailed);
                        }
                    }
                }
                finally
                {
                    guard?.Dispose();
                }
            }
        }

        // Only a verified rollback establishes this observation. A different
        // protected value at the same names gets a fresh decision. TransactionLock
        // guards the process-local cache across overlapping issuer instances.
        private static readonly Dictionary<(string Database, string Service, string Account), byte[]> MissingStagedViews = [];

        public bool HasStagingLimitation(IntPtr owner, string service, string account, byte[] stamp)
        {
            if (MissingStagedViews.Count == 0 || !MissingStagedViews.TryGetValue(
                (KeychainFilename(KeychainPath(owner)), service, account), out byte[]? known)
                || !stamp.AsSpan().SequenceEqual(known))
            {
                return false;
            }
            Report("known file Keychain staged view unavailable", ErrSecItemNotFound);
            return true;
        }

        public bool StagedProtectionGroupMissing(IntPtr resolved, byte[] der)
        {
            int policyStatus = CopyAccessSnapshot(resolved, out IntPtr policy);
            Release(policy);
            int readStatus = Report("read staged password", CheckPasswordData(resolved, der, (nuint)der.Length));
            return policyStatus == ErrSecItemNotFound && readStatus == ErrSecItemNotFound;
        }

        public void RememberStagingLimitation(IntPtr owner, string service, string account, byte[] stamp)
        {
            MissingStagedViews[(KeychainFilename(KeychainPath(owner)), service, account)] = stamp;
            Report("file Keychain staged view unavailable after rollback", ErrSecItemNotFound);
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
        private bool CommittedMatch(byte[] path, string service, string account)
        {
            return PresenceResult(RunMetadataProbe(path, service, account, null));
        }

        internal static bool PresenceResult(int status)
        {
            return status switch
            {
                ProbeFound => true,
                ProbeMissing => false,
                _ => throw new TokenStoreException(SaveFailed),
            };
        }

        private static string KeychainFilename(byte[] path) => new(new UTF8Encoding(false, true).GetChars(path));

        internal const string ProbeArgument = "--probe-signing-key-id";
        private const int ProbeFound = 40;
        private const int ProbeMissing = 41;

        internal static int Probe(string path, string service, string account)
        {
            if (!LicenceKey.IsValidKeyId(account) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            {
                return 1;
            }

            return new NativeCalls().RunMetadataProbe(Encoding.UTF8.GetBytes(path), service, account, null);
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

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckPasswordData")]
        private static partial int CheckPasswordData(IntPtr item, ReadOnlySpan<byte> expected, nuint length);

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

        [LibraryImport(Security)]
        private static partial int SecKeychainSetUserInteractionAllowed(byte allowed);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetUserInteractionAllowed(out byte allowed);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesStartImageCheck")]
        private static partial int StartImageCheck(int subject, out int pid);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesWaitVerifier")]
        private static partial int WaitVerifier(int pid);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesStartVerifier", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int StartVerifier(string path, string databasePath, string service, string account,
            int presence, SafeFileHandle database, out int pid, out int request);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesResumeVerifier")]
        private static partial int ResumeVerifier(int pid, int request, ReadOnlySpan<byte> message, int length);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesStopVerifier")]
        private static partial void StopVerifier(int pid);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesReleaseVerifier")]
        private static partial void ReleaseVerifier(int pid, int request);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetPath(IntPtr keychain, ref uint length, [Out] byte[] path);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);
    }
}
