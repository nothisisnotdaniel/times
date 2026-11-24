using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Times;

/// <summary>
/// Keeps the one Suite Pro licence key in the OS credential store, so Times and Context share it.
/// Failures throw <see cref="TokenStoreException"/> with a fixed message that never holds the key.
/// Tests inject a fake, so no test touches a real store.
/// </summary>
internal interface ILicenceStore
{
    /// <summary>False where there is no supported store; then nothing else may be called.</summary>
    public bool IsSupported { get; }

    /// <summary>A fixed reason why <see cref="Write"/> refuses in this run, or <c>null</c> when a write may go ahead.</summary>
    public string? WriteRefusal { get; }

    /// <summary>The stored key text, or <c>null</c> if none.</summary>
    public string? Read();

    /// <summary>Stores the key text, replacing any earlier one.</summary>
    public void Write(string key);

    /// <summary>Removes the stored key. False if there was none.</summary>
    public bool Delete();
}

/// <summary>Picks the licence store for the running OS.</summary>
internal sealed class LicenceStore
{
    /// <summary>The macOS Keychain service of the generic password that holds the key.</summary>
    public const string MacService = "Suite Pro licence";

    /// <summary>The macOS Keychain account of that item.</summary>
    public const string MacAccount = "key";

    /// <summary>The Windows Credential Manager target that holds the key.</summary>
    public const string WindowsTarget = "Suite/ProLicence";

    private LicenceStore()
    {
    }

    /// <summary>The macOS Keychain, the Windows Credential Manager, or the unsupported store anywhere else.</summary>
    public static ILicenceStore ForCurrentPlatform() => For(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

    /// <summary>The store for the given OS; the selection rule, separate so tests can check it without a real store.</summary>
    internal static ILicenceStore For(bool macOS, bool windows)
    {
        if (macOS)
        {
            return new MacKeychainLicenceStore(Environment.ProcessPath);
        }

        if (windows)
        {
            return new WindowsCredentialLicenceStore();
        }

        return new UnsupportedLicenceStore();
    }
}

/// <summary>Linux and anything else: no store. It never touches a file and refuses every call.</summary>
internal sealed class UnsupportedLicenceStore : ILicenceStore
{
    public bool IsSupported => false;

    public string? WriteRefusal => UnsupportedTokenStore.NotAvailable;

    public string? Read() => throw new TokenStoreException(UnsupportedTokenStore.NotAvailable);

    public void Write(string key) => throw new TokenStoreException(UnsupportedTokenStore.NotAvailable);

    public bool Delete() => throw new TokenStoreException(UnsupportedTokenStore.NotAvailable);
}

/// <summary>
/// The macOS Keychain generic password service <see cref="LicenceStore.MacService"/>, account
/// <see cref="LicenceStore.MacAccount"/>. Its access list trusts only the running apphost, by the same rule
/// as the Azure DevOps token (<see cref="MacKeychainTokenStore.TrustedApplicationPath"/>): never a shared
/// .NET host, so through <c>dotnet</c> nothing is stored.
/// </summary>
internal sealed class MacKeychainLicenceStore : ILicenceStore
{
    /// <summary>The fixed message when Times runs through the shared host.</summary>
    public const string SharedHostRefused =
        "The licence is stored only when Times runs as its own program (tm), not through dotnet.";

    private readonly string? trustedPath;

    /// <param name="processPath">The running executable (<see cref="Environment.ProcessPath"/>).</param>
    public MacKeychainLicenceStore(string? processPath)
    {
        trustedPath = MacKeychainTokenStore.TrustedApplicationPath(processPath);
    }

    public bool IsSupported => true;

    public string? WriteRefusal => trustedPath is null ? SharedHostRefused : null;

    public string? Read()
    {
        return MacKeychainItem.Default.Read(LicenceStore.MacService, LicenceStore.MacAccount, LicenceKey.MaxLength * 4,
            static (_, bytes) => Encoding.UTF8.GetString(bytes));
    }

    public void Write(string key)
    {
        // Checked before the keychain is touched: an item trusting the shared host is never made.
        if (trustedPath is null)
        {
            throw new TokenStoreException(SharedHostRefused);
        }

        MacKeychainItem.Default.Replace(LicenceStore.MacService, LicenceStore.MacAccount, Encoding.UTF8.GetBytes(key), trustedPath);
    }

    public bool Delete() => MacKeychainItem.Default.Delete(LicenceStore.MacService, LicenceStore.MacAccount);
}

/// <summary>A generic credential in the Windows Credential Manager, target <see cref="LicenceStore.WindowsTarget"/>.</summary>
internal sealed class WindowsCredentialLicenceStore : ILicenceStore
{
    public bool IsSupported => true;

    public string? WriteRefusal => null;

    public string? Read()
    {
        byte[]? bytes = WindowsCredential.Read(LicenceStore.WindowsTarget);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public void Write(string key) =>
        WindowsCredential.Write(LicenceStore.WindowsTarget, LicenceStore.MacAccount, Encoding.UTF8.GetBytes(key));

    public bool Delete() => WindowsCredential.Delete(LicenceStore.WindowsTarget);
}

/// <summary>
/// One macOS Keychain generic password, found by service and account, in the default keychain. Shared by
/// the licence store and the issuer's signing-key store. A new item gets its own access list
/// (<c>SecAccessCreate</c>) trusting only the given executable; the access object is built before an old
/// item is removed, so a failure there keeps it. Every message is fixed and never holds the secret.
/// </summary>
internal sealed partial class MacKeychainItem
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecNoDefaultKeychain = -25307;
    private const int MaxItems = 16;

    private const uint AccountAttribute = 0x61636374;
    private const uint ServiceAttribute = 0x73766365;
    private const uint LabelAttribute = 0x6c61626c;
    private const uint GenericAttribute = 0x67656e61;
    private static ReadOnlySpan<byte> IssuerMarker => "Times.LicenceIssuer/create-key/v1"u8;
    private const uint GenericPasswordClass = 0x67656e70;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private const string ReadFailed = "The macOS Keychain could not be read.";
    private const string SaveFailed = "The item could not be saved in the macOS Keychain.";
    internal const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";

    public static readonly MacKeychainItem Default = new(new NativeCalls());

    private static readonly Lock TransactionLock = new();
    private readonly INativeCalls native;

    internal MacKeychainItem(INativeCalls native)
    {
        this.native = native;
    }

    /// <summary>Reads bounded data with the item reference still held. Both are borrowed for the callback.</summary>
    public T? Read<T>(string service, string account, int maxBytes, Func<IntPtr, ReadOnlySpan<byte>, T> read)
        where T : class => Read(IntPtr.Zero, service, account, maxBytes, read);

    internal T? Read<T>(IntPtr keychain, string service, string account, int maxBytes,
        Func<IntPtr, ReadOnlySpan<byte>, T> read) where T : class
    {
        T? result = null;
        WithItem(keychain, service, account, item =>
            ReadContent(item, maxBytes, (found, bytes) => result = read(found, bytes)));
        return result;
    }

    private bool WithItem(IntPtr keychain, string service, string account, Action<IntPtr> use)
    {
        int status = native.Find(keychain, service, account, out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return false;
        }

        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException(ReadFailed);
        }

        try
        {
            use(item);
            return true;
        }
        finally
        {
            native.Release(item);
        }
    }

    private unsafe void ReadContent(IntPtr item, int maxBytes, Action<IntPtr, ReadOnlySpan<byte>> read,
        Action? authorizationDenied = null)
    {
        int status = native.CopyContent(item, out uint length, out IntPtr data);
        try
        {
            if (status is -25293 or -25308 or -25243 or -60005 or -2147416032 or -128)
            {
                authorizationDenied?.Invoke();
            }
            Check(status, ReadFailed);
            if (length != 0 && data == IntPtr.Zero)
            {
                throw new TokenStoreException(ReadFailed);
            }

            if (length > maxBytes)
            {
                throw new TokenStoreException("The macOS Keychain item is too large.");
            }

            read(item, new ReadOnlySpan<byte>(data.ToPointer(), (int)length));
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                NativeMemory.Clear(data.ToPointer(), length);
                native.FreeContent(data);
            }
        }
    }

    /// <summary>True if an item with this service and account exists (its data is never copied out).</summary>
    public bool Exists(string service, string account) => Exists(IntPtr.Zero, service, account);

    internal bool Exists(IntPtr keychain, string service, string account)
    {
        int status = native.Find(keychain, service, account, out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return false;
        }

        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException(ReadFailed);
        }

        native.Release(item);
        return true;
    }

    /// <summary>Adds with the given access list. A duplicate leaves the existing item intact. The caller owns the data.</summary>
    public bool TryCreate(string service, string account, byte[] data, string trustedPath)
    {
        Check(native.CopyDefaultKeychain(out IntPtr target), SaveFailed);
        if (target == IntPtr.Zero)
        {
            throw new TokenStoreException(SaveFailed);
        }
        try
        {
            return native.WithSearchList(target, others => AddWithSearchList(target, others, service, account,
                data, trustedPath, trustedPath));
        }
        finally
        {
            native.Release(target);
        }
    }

    internal bool AddWithSearchList(IntPtr target, IReadOnlyList<IntPtr> others, string service, string account,
        byte[] data, string creatorPath, string apphost)
    {
        Stack<IDisposable> held = new(others.Count);
        try
        {
            lock (TransactionLock)
            {
                using IFileTransaction writer = native.BeginTransaction(target);
                if (Exists(target, service, account))
                {
                    return false;
                }

                long start = Stopwatch.GetTimestamp();
                foreach (IntPtr database in others)
                {
                    TimeSpan remaining = NativeCalls.ProbeTimeout - Stopwatch.GetElapsedTime(start);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TokenStoreException(SaveFailed);
                    }
                    held.Push(native.GuardMatch(database, service, account, apphost, remaining, out bool found));
                    if (found)
                    {
                        return false;
                    }
                }

                if (!TryAddToKeychain(target, service, account, data, creatorPath, issuerCreated: true))
                {
                    return false;
                }
                writer.Commit();
                return true;
            }
        }
        finally
        {
            // FileDL cleanup on an unresponsive secondary may block. At this point
            // both the target file transaction and the process lock have ended.
            foreach (IDisposable guard in held)
            {
                guard.Dispose();
            }
        }
    }

    /// <summary>Returns the decoded data and migrates a writable item if its encoding changed.</summary>
    public byte[]? ReadAndMigrate(IntPtr search, string service, string account, int maxBytes, bool writable,
        Func<ReadOnlySpan<byte>, byte[]> decode)
    {
        byte[]? decoded = null;
        byte[]? stored = null;
        bool legacy = false;
        byte[]? stamp = null;
        IntPtr owner = IntPtr.Zero;
        IntPtr recordId = IntPtr.Zero;
        IntPtr originalPolicy = IntPtr.Zero;
        IntPtr persistent = IntPtr.Zero;
        try
        {
            native.WithoutInteraction(SelectAndMigrate);
            return decoded;
        }
        catch (Exception ex)
        {
            CryptographicOperations.ZeroMemory(decoded);
            if (ex is TypeLoadException or BadImageFormatException)
            {
                throw new TokenStoreException(MigrationFailed);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
            native.Release(originalPolicy);
            native.Release(persistent);
            native.Release(recordId);
            native.Release(owner);
        }

        void SelectAndMigrate()
        {
            bool ReadSelected(IntPtr scope) => WithItem(scope, service, account, ReadSelectedItem);

            void ReadSelectedItem(IntPtr item)
            {
                Check(native.CopyItemKeychain(item, out owner), MigrationFailed);
                native.WithStableRead(owner, () =>
                {
                    Check(native.CopyRecordId(item, out recordId), MigrationFailed);
                    Check(native.CopyAccessSnapshot(item, out originalPolicy), MigrationFailed);
                    stamp = native.DataStamp(item);
                    ReadContent(item, maxBytes, AuthorizeRead, () => VerifyReadPolicy(item, legacy: true));
                });
            }

            void VerifyReadPolicy(IntPtr item, bool legacy)
            {
                int status = legacy ? native.CopyLegacyReadPolicy(item, writable, out IntPtr policy)
                    : native.CopyAccessSnapshot(item, out policy);
                try
                {
                    Check(status, MigrationFailed);
                    if (originalPolicy == IntPtr.Zero || policy == IntPtr.Zero || !native.Equal(originalPolicy, policy))
                    {
                        throw new TokenStoreException(MigrationFailed);
                    }
                }
                finally
                {
                    native.Release(policy);
                }
            }

            void AuthorizeRead(IntPtr item, ReadOnlySpan<byte> bytes)
            {
                // A raw PKCS#8 starts with SEQUENCE. Every other input goes
                // through the legacy policy, including malformed ASCII.
                legacy = bytes is not [0x30, ..];
                Check(native.FindUnique(owner, service, account, out IntPtr selected), MigrationFailed);
                try
                {
                    VerifyRecordId(selected, recordId);
                    VerifyReadPolicy(selected, legacy);
                    byte[]? selectedStamp = native.DataStamp(selected);
                    if (stamp is null || selectedStamp is null || !stamp.AsSpan().SequenceEqual(selectedStamp))
                    {
                        throw new TokenStoreException(MigrationFailed);
                    }
                    Check(native.CopyPersistentReference(item, out persistent), MigrationFailed);
                    Check(native.VerifyCommittedView(owner, service, account, persistent, originalPolicy, stamp), MigrationFailed);
                }
                finally
                {
                    native.Release(selected);
                }

                stored = bytes.ToArray();
            }

            if (search != IntPtr.Zero)
            {
                ReadSelected(search);
            }
            else
            {
                // Only a successful create-key stamps its record. Prefer that association even
                // if an unmarked duplicate later appears earlier in the search list. Unmarked
                // records (including earlier migrations) retain the historical search order.
                int status = native.CopyDefaultKeychain(out IntPtr target);
                if (status != ErrSecNoDefaultKeychain)
                {
                    Check(status, ReadFailed);
                }

                try
                {
                    bool marked = native.WithSearchList(target, others => WithPublishedKey(
                        target == IntPtr.Zero ? others : others.Append(target), service, account, ReadSelectedItem));
                    if (!marked && !ReadSelected(search) && target != IntPtr.Zero)
                    {
                        ReadSelected(target);
                    }
                }
                finally
                {
                    native.Release(target);
                }
            }

            if (stored is null)
            {
                return;
            }
            // The stable-read guard has now checked the database revision at
            // both ends of the authorized copy. Only now may the decoder import a key.
            decoded = decode(stored);
            if (legacy && writable)
            {
                // Release the initial item before looking it up again. Security.framework
                // caches live item references by primary key, including their old record handles.
                Migrate(owner, recordId, persistent, originalPolicy, service, account, stored, decoded, stamp);
            }
        }
    }

    private bool WithPublishedKey(IEnumerable<IntPtr> databases, string service, string account, Action<IntPtr> read)
    {
        IntPtr selected = IntPtr.Zero;
        try
        {
            foreach (IntPtr database in databases)
            {
                int status = native.Find(database, service, account, out IntPtr match, createdOnly: true);
                if (status == ErrSecItemNotFound)
                {
                    continue;
                }

                Check(status, ReadFailed);
                if (selected != IntPtr.Zero)
                {
                    native.Release(match);
                    throw new TokenStoreException("Multiple published signing keys have this ID. Check the Keychain search list.");
                }

                selected = match;
            }

            if (selected == IntPtr.Zero)
            {
                return false;
            }

            read(selected);
            return true;
        }
        finally
        {
            native.Release(selected);
        }
    }

    private void Migrate(IntPtr owner, IntPtr recordId, IntPtr persistent, IntPtr originalPolicy, string service,
        string account, byte[] expected, byte[] data, byte[]? stamp)
    {
        try
        {
            lock (TransactionLock)
            {
                using IFileTransaction transaction = native.BeginTransaction(owner);
                Check(native.FindUnique(owner, service, account, out IntPtr current), MigrationFailed);
                IntPtr before = IntPtr.Zero;
                try
                {
                    VerifyRecordId(current, recordId);
                    VerifyPersistentReference(current, persistent);
                    Check(native.CopyAccessSnapshot(current, out before), MigrationFailed);
                    if (persistent == IntPtr.Zero || originalPolicy == IntPtr.Zero || before == IntPtr.Zero
                        || !native.Equal(originalPolicy, before))
                    {
                        throw new TokenStoreException(MigrationFailed);
                    }

                    // A provider can change the value without changing the physical ID.
                    byte[]? lockedStamp = native.DataStamp(current);
                    if (stamp is null || !stamp.AsSpan().SequenceEqual(lockedStamp))
                    {
                        bool migrated = false;
                        ReadContent(current, Math.Max(expected.Length, data.Length), (_, bytes) =>
                        {
                            migrated = CryptographicOperations.FixedTimeEquals(bytes, data);
                            if (!migrated && !CryptographicOperations.FixedTimeEquals(bytes, expected))
                            {
                                throw new TokenStoreException(MigrationFailed);
                            }
                        });
                        if (migrated)
                        {
                            Check(native.CheckMigrationReader(current, before, data), MigrationFailed);
                            return;
                        }
                    }

                    Check(native.CheckMigrationReader(current, before, expected), MigrationFailed);
                    UpdatePreservingAccess(owner, service, account, current, persistent, before, lockedStamp, data);
                    transaction.Commit();
                }
                finally
                {
                    native.Release(before);
                    native.Release(current);
                }
            }
        }
        catch (Exception ex) when (ex is TokenStoreException or TypeLoadException or BadImageFormatException)
        {
            throw new TokenStoreException(MigrationFailed);
        }
    }

    private void VerifyRecordId(IntPtr item, IntPtr expectedId)
    {
        if (item == IntPtr.Zero || expectedId == IntPtr.Zero)
        {
            throw new TokenStoreException(MigrationFailed);
        }
        int status = native.CopyRecordId(item, out IntPtr actualId);
        try
        {
            Check(status, MigrationFailed);
            if (actualId == IntPtr.Zero || !native.Equal(actualId, expectedId))
            {
                throw new TokenStoreException(MigrationFailed);
            }
        }
        finally
        {
            native.Release(actualId);
        }
    }

    private void VerifyPersistentReference(IntPtr item, IntPtr original)
    {
        int status = native.CopyPersistentReference(item, out IntPtr observed);
        try
        {
            Check(status, MigrationFailed);
            if (original == IntPtr.Zero || observed == IntPtr.Zero || !native.Equal(original, observed))
            {
                throw new TokenStoreException(MigrationFailed);
            }
        }
        finally
        {
            native.Release(observed);
        }
    }

    private void UpdatePreservingAccess(IntPtr owner, string service, string account, IntPtr item, IntPtr persistent,
        IntPtr before, byte[]? originalStamp, byte[] data)
    {
        IntPtr resolved = IntPtr.Zero, unique = IntPtr.Zero, writtenId = IntPtr.Zero, writtenPolicy = IntPtr.Zero;
        try
        {
            if (originalStamp is null)
            {
                throw new TokenStoreException(MigrationFailed);
            }
            Check(native.UpdateData(item, data), MigrationFailed);
            // FileDL may resolve the primary key from its cache while a cursor still
            // sees the pre-update snapshot. Both observations must prove the staged value.
            int resolveStatus = native.ResolvePassword(persistent, owner, service, account, out resolved);
            int searchStatus = native.FindUnique(owner, service, account, out unique);
            Check(resolveStatus, MigrationFailed);
            Check(searchStatus, MigrationFailed);
            Check(native.CopyRecordId(item, out writtenId), MigrationFailed);
            Check(native.CopyAccessSnapshot(item, out writtenPolicy), MigrationFailed);
            byte[]? written = native.DataStamp(item, data.Length);
            if (written is null || written.AsSpan().SequenceEqual(originalStamp))
            {
                throw new TokenStoreException(MigrationFailed);
            }
            VerifyStaged(resolved, written);
            VerifyStaged(unique, written);
        }
        finally
        {
            native.Release(writtenPolicy);
            native.Release(writtenId);
            native.Release(unique);
            native.Release(resolved);
        }

        void VerifyStaged(IntPtr selected, byte[] written)
        {
            VerifyRecordId(selected, writtenId);
            VerifyPersistentReference(selected, persistent);
            if (!written.AsSpan().SequenceEqual(native.DataStamp(selected, data.Length)))
            {
                throw new TokenStoreException(MigrationFailed);
            }
            int status = native.CopyAccessSnapshot(selected, out IntPtr policy);
            try
            {
                Check(status, MigrationFailed);
                if (writtenPolicy == IntPtr.Zero || policy == IntPtr.Zero || !native.Equal(writtenPolicy, policy))
                {
                    throw new TokenStoreException(MigrationFailed);
                }
                Check(native.CheckAccess(before, policy, selected, data), MigrationFailed);
            }
            finally
            {
                native.Release(policy);
            }
        }
    }

    /// <summary>Stores <paramref name="secret"/> (zeroed afterwards) as the only item, trusting only <paramref name="trustedPath"/>.</summary>
    public void Replace(string service, string account, byte[] secret, string trustedPath)
    {
        IntPtr access = IntPtr.Zero;
        try
        {
            access = native.CreateAccess(trustedPath, service);
            Delete(service, account);
            if (!TryAdd(IntPtr.Zero, service, account, secret, access))
            {
                throw new TokenStoreException(SaveFailed);
            }
        }
        finally
        {
            Array.Clear(secret);
            if (access != IntPtr.Zero)
            {
                native.Release(access);
            }
        }
    }

    /// <summary>Removes every item with this service and account. False if there was none.</summary>
    public bool Delete(string service, string account)
    {
        bool deleted = false;
        for (int i = 0; i < MaxItems; i++)
        {
            int status = native.Find(IntPtr.Zero, service, account, out IntPtr item);
            if (status == ErrSecItemNotFound)
            {
                return deleted;
            }

            if (status != ErrSecSuccess)
            {
                throw new TokenStoreException(ReadFailed);
            }

            try
            {
                if (native.Delete(item) != ErrSecSuccess)
                {
                    throw new TokenStoreException("The item could not be removed from the macOS Keychain.");
                }

                deleted = true;
            }
            finally
            {
                native.Release(item);
            }
        }

        throw new TokenStoreException("The macOS Keychain holds too many matching items.");
    }

    private static IntPtr CreateAccess(string? path, string description)
    {
        IntPtr application = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (SecTrustedApplicationCreateFromPath(path, out application) != ErrSecSuccess || application == IntPtr.Zero)
            {
                throw new TokenStoreException(SaveFailed);
            }

            list = CreateArray(application);
            descriptor = CFStringCreateWithCString(IntPtr.Zero, description, CFStringEncodingUtf8);
            if (list == IntPtr.Zero || descriptor == IntPtr.Zero)
            {
                throw new TokenStoreException(SaveFailed);
            }

            if (SecAccessCreate(descriptor, list, out IntPtr access) != ErrSecSuccess || access == IntPtr.Zero)
            {
                throw new TokenStoreException(SaveFailed);
            }

            return access;
        }
        finally
        {
            ReleaseIfSet(descriptor);
            ReleaseIfSet(list);
            ReleaseIfSet(application);
        }
    }

    private static unsafe IntPtr CreateArray(IntPtr value)
    {
        IntPtr library = NativeLibrary.Load(CoreFoundation);
        IntPtr callBacks = NativeLibrary.GetExport(library, "kCFTypeArrayCallBacks");
        IntPtr* values = stackalloc IntPtr[1];
        values[0] = value;
        return CFArrayCreate(IntPtr.Zero, (IntPtr)values, 1, callBacks);
    }

    internal bool TryAddToKeychain(IntPtr keychain, string service, string account, byte[] secret, string creatorPath,
        bool issuerCreated = false)
    {
        if (keychain == IntPtr.Zero || string.IsNullOrEmpty(creatorPath))
        {
            throw new TokenStoreException(SaveFailed);
        }

        return TryAdd(keychain, service, account, secret, IntPtr.Zero, creatorPath, issuerCreated);
    }

    private unsafe bool TryAdd(IntPtr keychain, string service, string account, byte[] secret, IntPtr access,
        string? creatorPath = null, bool issuerCreated = false)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        fixed (byte* servicePointer = serviceBytes)
        fixed (byte* accountPointer = accountBytes)
        fixed (byte* marker = IssuerMarker)
        {
            Attribute* attributes = stackalloc Attribute[4];
            attributes[0] = new Attribute { Tag = ServiceAttribute, Length = (uint)serviceBytes.Length, Data = (IntPtr)servicePointer };
            attributes[1] = new Attribute { Tag = AccountAttribute, Length = (uint)accountBytes.Length, Data = (IntPtr)accountPointer };
            attributes[2] = new Attribute { Tag = LabelAttribute, Length = (uint)serviceBytes.Length, Data = (IntPtr)servicePointer };
            attributes[3] = new Attribute { Tag = GenericAttribute, Length = (uint)IssuerMarker.Length, Data = (IntPtr)marker };
            AttributeList list = new AttributeList { Count = issuerCreated ? 4u : 3u, Attributes = (IntPtr)attributes };
            int status = creatorPath is not null
                ? native.AddToKeychain(keychain, ref list, secret, creatorPath, service, out IntPtr item)
                : native.CreateFromContent(GenericPasswordClass, ref list, secret, keychain, access, out item);
            if (status == ErrSecDuplicateItem)
            {
                return false;
            }

            if (status != ErrSecSuccess)
            {
                throw new TokenStoreException(SaveFailed);
            }

            native.Release(item);
            return true;
        }
    }

    private static void ReleaseIfSet(IntPtr reference)
    {
        if (reference != IntPtr.Zero)
        {
            CFRelease(reference);
        }
    }

    private static void Check(int status, string message)
    {
        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Attribute
    {
        public uint Tag;
        public uint Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AttributeList
    {
        public uint Count;
        public IntPtr Attributes;
    }

    internal interface INativeCalls
    {
        public int Find(IntPtr keychain, string service, string account, out IntPtr item, bool createdOnly = false);
        public int FindUnique(IntPtr keychain, string service, string account, out IntPtr item);
        public IDisposable GuardMatch(IntPtr keychain, string service, string account, string apphost,
            TimeSpan timeout, out bool found);
        public int CopyContent(IntPtr item, out uint length, out IntPtr data);
        public byte[]? DataStamp(IntPtr item, int expectedLength = -1);
        public int CopyDefaultKeychain(out IntPtr keychain);
        public int CopyItemKeychain(IntPtr item, out IntPtr keychain);
        public bool WithSearchList(IntPtr target, Func<IReadOnlyList<IntPtr>, bool> action);
        public void WithoutInteraction(Action action);
        public void WithStableRead(IntPtr keychain, Action read);
        public int VerifyCommittedView(IntPtr keychain, string service, string account, IntPtr persistent, IntPtr policy,
            byte[]? stamp);
        public IFileTransaction BeginTransaction(IntPtr keychain);
        public int CopyRecordId(IntPtr item, out IntPtr recordId);
        public int CopyPersistentReference(IntPtr item, out IntPtr persistent);
        public int ResolvePassword(IntPtr persistent, IntPtr owner, string service, string account, out IntPtr item);
        public int CopyAccessSnapshot(IntPtr item, out IntPtr snapshot);
        public int CopyLegacyReadPolicy(IntPtr item, bool writable, out IntPtr snapshot);
        public int CheckMigrationReader(IntPtr item, IntPtr policy, byte[] expected);
        public int CheckAccess(IntPtr before, IntPtr after, IntPtr item, byte[] expected);
        public bool Equal(IntPtr first, IntPtr second);
        public IntPtr CreateAccess(string path, string description);
        public int AddToKeychain(IntPtr keychain, ref AttributeList attributes, byte[] data, string creatorPath,
            string description, out IntPtr item);
        public int CreateFromContent(uint itemClass, ref AttributeList attributes, byte[] data, IntPtr keychain,
            IntPtr access, out IntPtr item);
        public int UpdateData(IntPtr item, ReadOnlyMemory<byte> data);
        public int Delete(IntPtr item);
        public void FreeContent(IntPtr data);
        public void Release(IntPtr reference);
    }

    internal partial class NativeCalls : INativeCalls
    {
        public int Find(IntPtr keychain, string service, string account, out IntPtr item, bool createdOnly = false)
        {
            byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
            byte[] accountBytes = Encoding.UTF8.GetBytes(account);
            return createdOnly ? SearchPassword(keychain, serviceBytes, accountBytes, true, out item)
                : Report("find password record", SecKeychainFindGenericPassword(keychain, (uint)serviceBytes.Length,
                    serviceBytes, (uint)accountBytes.Length, accountBytes, 0, 0, out item));
        }

        public int FindUnique(IntPtr keychain, string service, string account, out IntPtr item) =>
            Report("unique lookup", SearchPassword(keychain, Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(account),
                false, out item));

        private static unsafe int SearchPassword(IntPtr keychain, byte[] serviceBytes, byte[] accountBytes,
            bool marked, out IntPtr item)
        {
            item = IntPtr.Zero;
            if (keychain == IntPtr.Zero)
            {
                return -50;
            }
            fixed (byte* servicePointer = serviceBytes)
            fixed (byte* accountPointer = accountBytes)
            fixed (byte* marker = IssuerMarker)
            {
                Attribute* attrs = stackalloc Attribute[3];
                attrs[0] = new Attribute { Tag = ServiceAttribute, Length = (uint)serviceBytes.Length, Data = (IntPtr)servicePointer };
                attrs[1] = new Attribute { Tag = AccountAttribute, Length = (uint)accountBytes.Length, Data = (IntPtr)accountPointer };
                attrs[2] = new Attribute { Tag = GenericAttribute, Length = (uint)IssuerMarker.Length, Data = (IntPtr)marker };
                AttributeList list = new() { Count = marked ? 3u : 2u, Attributes = (IntPtr)attrs };
                int status = SecKeychainSearchCreateFromAttributes(keychain, GenericPasswordClass, ref list, out IntPtr search);
                try
                {
                    if (status == 0)
                    {
                        status = SecKeychainSearchCopyNext(search, out item);
                    }
                    if (status == 0)
                    {
                        int next = SecKeychainSearchCopyNext(search, out IntPtr duplicate);
                        ReleaseIfSet(duplicate);
                        if (next != ErrSecItemNotFound)
                        {
                            ReleaseIfSet(item);
                            item = IntPtr.Zero;
                            return next == 0 ? ErrSecDuplicateItem : next;
                        }
                    }
                    return status;
                }
                finally
                {
                    ReleaseIfSet(search);
                }
            }
        }

        public int CopyDefaultKeychain(out IntPtr keychain) => SecKeychainCopyDefault(out keychain);

        public virtual int CopyContent(IntPtr item, out uint length, out IntPtr data) =>
            Report("read password", SecKeychainItemCopyContent(item, 0, 0, out length, out data));

        public int CopyItemKeychain(IntPtr item, out IntPtr keychain) =>
            Report("identify owner", SecKeychainItemCopyKeychain(item, out keychain));

        public int CopyRecordId(IntPtr item, out IntPtr recordId) =>
            Report("identify record", CopyPhysicalRecordId(item, out recordId));

        public bool Equal(IntPtr first, IntPtr second) => CFEqual(first, second) != 0;

        public bool WithSearchList(IntPtr target, Func<IReadOnlyList<IntPtr>, bool> action)
        {
            Check(SecKeychainCopySearchList(out IntPtr list), ReadFailed);
            try
            {
                List<IntPtr> others = [];
                for (nint i = 0; i < CFArrayGetCount(list); i++)
                {
                    IntPtr candidate = CFArrayGetValueAtIndex(list, i);
                    if ((target == IntPtr.Zero || CFEqual(candidate, target) == 0)
                        && !others.Any(other => CFEqual(candidate, other) != 0))
                    {
                        others.Add(candidate);
                    }
                }

                return action(others);
            }
            finally
            {
                CFRelease(list);
            }
        }

        public IntPtr CreateAccess(string path, string description) => MacKeychainItem.CreateAccess(path, description);

        public int AddToKeychain(IntPtr keychain, ref AttributeList attributes, byte[] data, string creatorPath,
            string description, out IntPtr item)
        {
            item = IntPtr.Zero;
            if (keychain == IntPtr.Zero || Environment.ProcessPath is not string executable
                || CanonicalPath(creatorPath) != CanonicalPath(executable))
            {
                return -50; // errSecParam
            }

            byte[] path = [.. KeychainPath(keychain), 0];
            using Microsoft.Win32.SafeHandles.SafeFileHandle database = new(OpenProbeDatabase(path), ownsHandle: true);
            if (database.IsInvalid || CheckPasswordDatabase(keychain) != 0)
            {
                return -25294; // errSecNoSuchKeychain
            }

            // NULL denotes the running application in SecTrustedApplicationCreateFromPath.
            // The issuer holds the target writer and adds the complete item in one call.
            IntPtr access = MacKeychainItem.CreateAccess(null, description);
            try
            {
                if (DatabaseAtPath(database, path) != 0)
                {
                    return -25294;
                }
                int status = CreateFromContent(GenericPasswordClass, ref attributes, data, keychain, access, out item);
                if (status != 0)
                {
                    return status;
                }
                status = CopyItemKeychain(item, out IntPtr owner);
                bool inTarget = status == 0 && owner != IntPtr.Zero && Equal(owner, keychain);
                Release(owner);
                if (inTarget)
                {
                    return 0;
                }
                Release(item);
                item = IntPtr.Zero;
                return -25294;
            }
            finally
            {
                ReleaseIfSet(access);
            }
        }

        private static string CanonicalPath(string path) =>
            File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);

        public int CreateFromContent(uint itemClass, ref AttributeList attributes, byte[] data, IntPtr keychain,
            IntPtr access, out IntPtr item) =>
            Report("provider add", SecKeychainItemCreateFromContent(itemClass, ref attributes, (uint)data.Length, data,
                keychain, access, out item));

        public int Delete(IntPtr item) => Report("provider delete", SecKeychainItemDelete(item));

        public virtual void FreeContent(IntPtr data) => SecKeychainItemFreeContent(IntPtr.Zero, data);

        public void Release(IntPtr reference) => ReleaseIfSet(reference);
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[] accountName, IntPtr passwordLength,
        IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainCopyDefault(out IntPtr keychain);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemCopyKeychain(IntPtr item, out IntPtr keychain);

    [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCopyRecordId")]
    private static partial int CopyPhysicalRecordId(IntPtr item, out IntPtr identifier);

    [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckPasswordDatabase")]
    private static partial int CheckPasswordDatabase(IntPtr keychain);

    [LibraryImport(Security)]
    private static partial int SecKeychainCopySearchList(out IntPtr list);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFEqual(IntPtr first, IntPtr second);

    [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SecTrustedApplicationCreateFromPath(string? path, out IntPtr application);

    [LibraryImport(Security)]
    private static partial int SecAccessCreate(IntPtr descriptor, IntPtr trustedList, out IntPtr access);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemCreateFromContent(uint itemClass, ref AttributeList attrList, uint length,
        byte[] data, IntPtr keychain, IntPtr initialAccess, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainSearchCreateFromAttributes(IntPtr search, uint itemClass, ref AttributeList attributes,
        out IntPtr cursor);

    [LibraryImport(Security)]
    private static partial int SecKeychainSearchCopyNext(IntPtr cursor, out IntPtr item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemCopyContent(IntPtr item, IntPtr itemClass, IntPtr attributes, out uint length, out IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayCreate(IntPtr allocator, IntPtr values, nint count, IntPtr callBacks);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}

/// <summary>
/// One generic credential in the Windows Credential Manager (advapi32), persisted for the local machine.
/// Buffers from <c>CredReadW</c> are freed with <c>CredFree</c>; unmanaged copies are zeroed before they are freed.
/// </summary>
internal static partial class WindowsCredential
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>The credential's blob, or <c>null</c> if there is none.</summary>
    public static unsafe byte[]? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out IntPtr pointer))
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound)
            {
                return null;
            }

            throw new TokenStoreException("The Windows Credential Manager could not be read.");
        }

        try
        {
            Credential credential = *(Credential*)pointer;
            byte[] blob = new byte[credential.CredentialBlobSize];
            if (blob.Length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            }

            return blob;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>Stores <paramref name="blob"/> (zeroed afterwards), replacing an existing credential.</summary>
    public static void Write(string target, string userName, byte[] blob)
    {
        IntPtr targetPointer = Marshal.StringToHGlobalUni(target);
        IntPtr userPointer = Marshal.StringToHGlobalUni(userName);
        IntPtr data = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            Credential credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
                UserName = userPointer,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new TokenStoreException("The item could not be saved in the Windows Credential Manager.");
            }
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, data, blob.Length);
            Array.Clear(blob);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(userPointer);
            Marshal.FreeHGlobal(targetPointer);
        }
    }

    /// <summary>Removes the credential. False if there was none.</summary>
    public static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        if (Marshal.GetLastPInvokeError() == ErrorNotFound)
        {
            return false;
        }

        throw new TokenStoreException("The item could not be removed from the Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
