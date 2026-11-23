using System.Runtime.InteropServices;

namespace Times;

internal sealed partial class MacKeychainItem
{
    private sealed partial class NativeCalls
    {
        // File Keychain transactions use the underlying AppleFileDL handle. Security.framework's
        // own nested password update leaves the commit to its caller when autocommit is already off.
        private const uint GetFileDatabase = 6; // CSSM_APPLECSPDL_DB_GET_HANDLE
        private const uint ToggleAutocommit = 0;
        private const uint Commit = 1;
        private const uint Rollback = 2;
        private const uint TakeFileLock = 3;
        private const uint PartitionId = 0x10002; // CSSM_ACL_AUTHORIZATION_PARTITION_ID
        private static readonly Lock TransactionLock = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct DatabaseHandle
        {
            public nint DataLayer;
            public nint Database;
        }

        public unsafe bool WriteTransaction(IntPtr keychain, Func<bool> change)
        {
            lock (TransactionLock)
            {
                Check(SecKeychainGetDLDBHandle(keychain, out DatabaseHandle keychainDb), SaveFailed);
                DatabaseHandle fileDb = default;
                Check(CSSM_DL_PassThrough(keychainDb, GetFileDatabase, 0, (IntPtr)(&fileDb)), SaveFailed);
                nint wasAutocommit = 0;
                Check(CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 0, (IntPtr)(&wasAutocommit)), SaveFailed);
                if (wasAutocommit == 0)
                {
                    throw new TokenStoreException(SaveFailed);
                }

                bool committed = false;
                try
                {
                    Check(CSSM_DL_PassThrough(fileDb, TakeFileLock, 0, 0), SaveFailed);
                    if (!change())
                    {
                        return false;
                    }

                    Check(CSSM_DL_PassThrough(fileDb, Commit, 0, 0), SaveFailed);
                    committed = true;
                    return true;
                }
                finally
                {
                    if (!committed)
                    {
                        Check(CSSM_DL_PassThrough(fileDb, Rollback, 0, 0), SaveFailed);
                    }

                    // The commit already determined the durable result. Restoring the connection
                    // mode cannot turn a committed write into a failed migration or a duplicate.
                    CSSM_DL_PassThrough(fileDb, ToggleAutocommit, 1, 0);
                }
            }
        }

        public IntPtr CopyPartition(IntPtr item) => WithPartitionAcl(item, (_, acl) => CopyDescriptor(acl));

        public void PreservePartition(IntPtr item, IntPtr original)
        {
            // A password-data update carries forward the trusted applications but rebuilds the
            // storage group. Reconcile its code-signing partition ACL before the transaction commits.
            bool changed = WithPartitionAcl(item, (access, acl) =>
            {
                IntPtr current = CopyDescriptor(acl);
                try
                {
                    if (EqualDescriptors(original, current))
                    {
                        return false;
                    }

                    if (acl == IntPtr.Zero)
                    {
                        throw new TokenStoreException(MigrationFailed);
                    }

                    Check(original == IntPtr.Zero ? SecACLRemove(acl) : SecACLSetContents(acl, 0, original, 0), MigrationFailed);
                    Check(SecKeychainItemSetAccess(item, access), MigrationFailed);
                    return true;
                }
                finally
                {
                    ReleaseIfSet(current);
                }
            });

            if (changed)
            {
                IntPtr restored = CopyPartition(item);
                try
                {
                    if (!EqualDescriptors(original, restored))
                    {
                        throw new TokenStoreException(MigrationFailed);
                    }
                }
                finally
                {
                    ReleaseIfSet(restored);
                }
            }
        }

        private static bool EqualDescriptors(IntPtr first, IntPtr second) =>
            first == second || (first != IntPtr.Zero && second != IntPtr.Zero && CFEqual(first, second) != 0);

        // A partition ACL's descriptor contains the serialized partition IDs. It has no application
        // list or prompt flags. Refuse an unfamiliar shape before committing any change to the item.
        private static IntPtr CopyDescriptor(IntPtr acl)
        {
            if (acl == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            int status = SecACLCopyContents(acl, out IntPtr apps, out IntPtr description, out uint flags);
            if (status != ErrSecSuccess || apps != IntPtr.Zero || description == IntPtr.Zero || flags != 0)
            {
                ReleaseIfSet(apps);
                ReleaseIfSet(description);
                throw new TokenStoreException(MigrationFailed);
            }

            return description;
        }

        private static T WithPartitionAcl<T>(IntPtr item, Func<IntPtr, IntPtr, T> action)
        {
            Check(SecKeychainItemCopyAccess(item, out IntPtr access), MigrationFailed);
            IntPtr list = IntPtr.Zero;
            try
            {
                Check(SecAccessCopySelectedACLList(access, PartitionId, out list), MigrationFailed);
                IntPtr partition = IntPtr.Zero;
                nint count = list == IntPtr.Zero ? 0 : CFArrayGetCount(list);
                for (nint i = 0; i < count; i++)
                {
                    IntPtr acl = CFArrayGetValueAtIndex(list, i);
                    // The selected list also includes wildcard ACLs. Only the explicit right
                    // describes code partitions. Never apply the descriptor to a wildcard ACL.
                    uint tagCount = 0;
                    int status = SecACLGetAuthorizations(acl, [], ref tagCount);
                    if (status != -50 || tagCount == 0) // errSecParam reports the required capacity
                    {
                        Check(status, MigrationFailed);
                    }

                    uint[] tags = new uint[tagCount];
                    Check(SecACLGetAuthorizations(acl, tags, ref tagCount), MigrationFailed);
                    if (tags.Contains(PartitionId))
                    {
                        if (partition != IntPtr.Zero || tagCount != 1)
                        {
                            throw new TokenStoreException(MigrationFailed);
                        }

                        partition = acl;
                    }
                }

                return action(access, partition);
            }
            finally
            {
                ReleaseIfSet(list);
                CFRelease(access);
            }
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemCopyAccess(IntPtr item, out IntPtr access);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemSetAccess(IntPtr item, IntPtr access);

        [LibraryImport(Security)]
        private static partial int SecAccessCopySelectedACLList(IntPtr access, uint right, out IntPtr list);

        [LibraryImport(Security)]
        private static partial int SecACLGetAuthorizations(IntPtr acl, [Out] uint[] tags, ref uint count);

        [LibraryImport(Security)]
        private static partial int SecACLCopyContents(IntPtr acl, out IntPtr applications, out IntPtr description, out uint flags);

        [LibraryImport(Security)]
        private static partial int SecACLSetContents(IntPtr acl, IntPtr applications, IntPtr description, uint flags);

        [LibraryImport(Security)]
        private static partial int SecACLRemove(IntPtr acl);
    }
}
