using System.Buffers;
using System.Runtime.InteropServices;

namespace Times;

internal sealed partial class MacKeychainItem
{
    internal interface IFileTransaction : IDisposable
    {
        public void Commit();
    }

    internal sealed partial class NativeCalls
    {
        private const uint GetFileDatabase = 6; // CSSM_APPLECSPDL_DB_GET_HANDLE
        private const uint ToggleAutocommit = 0;
        private const uint CommitDatabase = 1;
        private const uint Rollback = 2;
        private const uint TakeFileLock = 3;
        private const uint PartitionId = 0x10002; // CSSM_ACL_AUTHORIZATION_PARTITION_ID
        private const uint PartitionedDatabase = 0x200;

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
            DatabaseHandle db = FileDatabase(keychain);
            return (IDisposable?)TryLockLocalFile(path) ?? new FileTransaction(db);
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

        public void CheckAccessForUpdate(IntPtr owner, IntPtr item)
        {
            // The locked read has just decrypted this item. On a partitioned file Keychain
            // securityd requires the reader's partition to be in its list. A data update copies
            // the ordinary ACLs and creates a group with just the writer's partition. Therefore
            // a singleton already matches. A custom list cannot be reproduced by this update.
            // Check the committed group: cursors cannot find a newly staged group by label.
            Check(SecKeychainGetKeychainVersion(owner, out uint version), MigrationFailed);
            Check(SecKeychainItemCopyAccess(item, out IntPtr access), MigrationFailed);
            IntPtr list = IntPtr.Zero;
            try
            {
                Check(SecAccessCopySelectedACLList(access, PartitionId, out list), MigrationFailed);
                bool hasPartition = false;
                nint count = list == IntPtr.Zero ? 0 : CFArrayGetCount(list);
                for (nint i = 0; i < count; i++)
                {
                    IntPtr acl = CFArrayGetValueAtIndex(list, i);
                    // The selected list can also include wildcard authorizations.
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
                        if (hasPartition || tagCount != 1 || !HasSingleReader(acl))
                        {
                            throw new TokenStoreException(MigrationFailed);
                        }

                        hasPartition = true;
                    }
                }

                if (hasPartition != (version >= PartitionedDatabase))
                {
                    throw new TokenStoreException(MigrationFailed);
                }
            }
            finally
            {
                ReleaseIfSet(list);
                CFRelease(access);
            }
        }

        private static bool HasSingleReader(IntPtr acl)
        {
            int status = SecACLCopyContents(acl, out IntPtr apps, out IntPtr descriptor, out ushort flags);
            try
            {
                if (status != ErrSecSuccess || apps != IntPtr.Zero || descriptor == IntPtr.Zero || flags != 0)
                {
                    return false;
                }

                // SecACLCopyContents exposes the partition property list as hexadecimal text.
                nint length = CFStringGetLength(descriptor);
                if (length is <= 0 or > 65536 || length % 2 != 0)
                {
                    return false;
                }

                byte[] hex = new byte[(int)length + 1];
                byte[] plist = new byte[length / 2];
                if (CFStringGetCString(descriptor, hex, hex.Length, CFStringEncodingUtf8) == 0 ||
                    Convert.FromHexString(hex.AsSpan(0, (int)length), plist, out _, out _) != OperationStatus.Done)
                {
                    return false;
                }

                return IsSinglePartitionList(plist);
            }
            finally
            {
                ReleaseIfSet(apps);
                ReleaseIfSet(descriptor);
            }
        }

        private static bool IsSinglePartitionList(byte[] plist)
        {
            IntPtr data = CFDataCreate(0, plist, plist.Length);
            IntPtr dictionary = IntPtr.Zero;
            IntPtr name = IntPtr.Zero;
            try
            {
                if (data == IntPtr.Zero)
                {
                    return false;
                }

                dictionary = CFPropertyListCreateWithData(0, data, 0, 0, 0);
                if (dictionary == IntPtr.Zero || CFGetTypeID(dictionary) != CFDictionaryGetTypeID()
                    || CFDictionaryGetCount(dictionary) != 1)
                {
                    return false;
                }

                name = CFStringCreateWithCString(0, "Partitions", CFStringEncodingUtf8);
                IntPtr entries = name == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(dictionary, name);
                if (entries == IntPtr.Zero || CFGetTypeID(entries) != CFArrayGetTypeID() || CFArrayGetCount(entries) != 1)
                {
                    return false;
                }

                IntPtr reader = CFArrayGetValueAtIndex(entries, 0);
                return CFGetTypeID(reader) == CFStringGetTypeID() && CFStringGetLength(reader) > 0;
            }
            finally
            {
                ReleaseIfSet(name);
                ReleaseIfSet(dictionary);
                ReleaseIfSet(data);
            }
        }

        [LibraryImport(Security)]
        private static partial int SecKeychainGetPath(IntPtr keychain, ref uint length, [Out] byte[] path);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetDLDBHandle(IntPtr keychain, out DatabaseHandle handle);

        [LibraryImport(Security)]
        private static partial int CSSM_DL_PassThrough(DatabaseHandle database, uint operation, IntPtr input, IntPtr output);

        [LibraryImport(Security)]
        private static partial int SecKeychainGetKeychainVersion(IntPtr keychain, out uint version);

        [LibraryImport(Security)]
        private static partial int SecKeychainItemCopyAccess(IntPtr item, out IntPtr access);

        [LibraryImport(Security)]
        private static partial int SecAccessCopySelectedACLList(IntPtr access, uint right, out IntPtr list);

        [LibraryImport(Security)]
        private static partial int SecACLGetAuthorizations(IntPtr acl, [Out] uint[] tags, ref uint count);

        [LibraryImport(Security)]
        private static partial int SecACLCopyContents(IntPtr acl, out IntPtr applications, out IntPtr description, out ushort flags);

        [LibraryImport(CoreFoundation)]
        private static partial nint CFStringGetLength(IntPtr value);

        [LibraryImport(CoreFoundation)]
        private static partial byte CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint size, uint encoding);

        [LibraryImport(CoreFoundation)]
        private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        [LibraryImport(CoreFoundation)]
        private static partial IntPtr CFPropertyListCreateWithData(IntPtr allocator, IntPtr data, nuint options,
            IntPtr format, IntPtr error);

        [LibraryImport(CoreFoundation)]
        private static partial nuint CFGetTypeID(IntPtr value);

        [LibraryImport(CoreFoundation)]
        private static partial nuint CFDictionaryGetTypeID();

        [LibraryImport(CoreFoundation)]
        private static partial nuint CFArrayGetTypeID();

        [LibraryImport(CoreFoundation)]
        private static partial nuint CFStringGetTypeID();

        [LibraryImport(CoreFoundation)]
        private static partial nint CFDictionaryGetCount(IntPtr dictionary);

        [LibraryImport(CoreFoundation)]
        private static partial IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr name);
    }
}
