using System.Runtime.InteropServices;
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
    private const uint GenericPasswordClass = 0x67656e70;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    private const string ReadFailed = "The macOS Keychain could not be read.";
    private const string SaveFailed = "The item could not be saved in the macOS Keychain.";
    private const string MigrationFailed = "Signing-key migration failed. Check Keychain write access and retry.";

    public static readonly MacKeychainItem Default = new(new NativeCalls());

    private readonly INativeCalls native;

    internal MacKeychainItem(INativeCalls native)
    {
        this.native = native;
    }

    /// <summary>Reads bounded data with the item reference still held. Both are borrowed for the callback.</summary>
    public T? Read<T>(string service, string account, int maxBytes, Func<IntPtr, ReadOnlySpan<byte>, T> read)
        where T : class => Read(IntPtr.Zero, service, account, maxBytes, read);

    // New signing keys belong to the default keychain. An older key can still live elsewhere on the search list.
    public T? ReadFromDefaultFirst<T>(string service, string account, int maxBytes, Func<IntPtr, ReadOnlySpan<byte>, T> read)
        where T : class
    {
        int status = native.CopyDefaultKeychain(out IntPtr target);
        if (status != ErrSecNoDefaultKeychain)
        {
            Check(status, ReadFailed);
            try
            {
                T? result = Read(target, service, account, maxBytes, read);
                if (result is not null)
                {
                    return result;
                }
            }
            finally
            {
                native.Release(target);
            }
        }

        return Read(service, account, maxBytes, read);
    }

    internal unsafe T? Read<T>(IntPtr keychain, string service, string account, int maxBytes,
        Func<IntPtr, ReadOnlySpan<byte>, T> read) where T : class
    {
        uint length = 0;
        IntPtr data = IntPtr.Zero;
        int status = native.Find(keychain, service, account, (IntPtr)(&length), (IntPtr)(&data), out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException(ReadFailed);
        }

        try
        {
            if (length > maxBytes)
            {
                throw new TokenStoreException("The macOS Keychain item is too large.");
            }

            return read(item, new ReadOnlySpan<byte>(data.ToPointer(), (int)length));
        }
        finally
        {
            NativeMemory.Clear(data.ToPointer(), length);
            native.FreeContent(data);
            native.Release(item);
        }
    }

    /// <summary>True if an item with this service and account exists (its data is never copied out).</summary>
    public bool Exists(string service, string account) => Exists(IntPtr.Zero, service, account);

    private bool Exists(IntPtr keychain, string service, string account)
    {
        int status = native.Find(keychain, service, account, IntPtr.Zero, IntPtr.Zero, out IntPtr item);
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
        try
        {
            IntPtr access = native.CreateAccess(trustedPath, service);
            try
            {
                // The native add arbitrates duplicates in target. Before making it visible,
                // check the other searchable keychains for legacy IDs, even if target is searched first.
                return native.WriteTransaction(target, () => TryAdd(target, service, account, data, access)
                    && !native.OtherKeychains(target).Any(other => Exists(other, service, account)));
            }
            finally
            {
                native.Release(access);
            }
        }
        finally
        {
            native.Release(target);
        }
    }

    /// <summary>Updates only the data of the item held by <see cref="Read{T}"/>.</summary>
    public void UpdateData(IntPtr item, ReadOnlyMemory<byte> data)
    {
        try
        {
            Check(native.CopyItemKeychain(item, out IntPtr owner), MigrationFailed);
            try
            {
                native.WriteTransaction(owner, () =>
                {
                    IntPtr partition = native.CopyPartition(item);
                    try
                    {
                        Check(native.ModifyAttributesAndData(item, IntPtr.Zero, data), MigrationFailed);
                        native.PreservePartition(item, partition);
                        return true;
                    }
                    finally
                    {
                        native.Release(partition);
                    }
                });
            }
            finally
            {
                native.Release(owner);
            }
        }
        catch (TokenStoreException)
        {
            throw new TokenStoreException(MigrationFailed);
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
            int status = native.Find(IntPtr.Zero, service, account, IntPtr.Zero, IntPtr.Zero, out IntPtr item);
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

    private static IntPtr CreateAccess(string path, string description)
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

    private unsafe bool TryAdd(IntPtr keychain, string service, string account, byte[] secret, IntPtr access)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        fixed (byte* servicePointer = serviceBytes)
        fixed (byte* accountPointer = accountBytes)
        {
            Attribute* attributes = stackalloc Attribute[3];
            attributes[0] = new Attribute { Tag = ServiceAttribute, Length = (uint)serviceBytes.Length, Data = (IntPtr)servicePointer };
            attributes[1] = new Attribute { Tag = AccountAttribute, Length = (uint)accountBytes.Length, Data = (IntPtr)accountPointer };
            attributes[2] = new Attribute { Tag = LabelAttribute, Length = (uint)serviceBytes.Length, Data = (IntPtr)servicePointer };
            AttributeList list = new AttributeList { Count = 3, Attributes = (IntPtr)attributes };
            int status = native.CreateFromContent(GenericPasswordClass, ref list, secret, keychain, access, out IntPtr item);
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
        public int Find(IntPtr keychain, string service, string account, IntPtr length, IntPtr data, out IntPtr item);
        public int CopyDefaultKeychain(out IntPtr keychain);
        public int CopyItemKeychain(IntPtr item, out IntPtr keychain);
        public IEnumerable<IntPtr> OtherKeychains(IntPtr target);
        public bool WriteTransaction(IntPtr keychain, Func<bool> change);
        public IntPtr CopyPartition(IntPtr item);
        public void PreservePartition(IntPtr item, IntPtr original);
        public IntPtr CreateAccess(string path, string description);
        public int CreateFromContent(uint itemClass, ref AttributeList attributes, byte[] data, IntPtr keychain,
            IntPtr access, out IntPtr item);
        public int ModifyAttributesAndData(IntPtr item, IntPtr attributes, ReadOnlyMemory<byte> data);
        public int Delete(IntPtr item);
        public void FreeContent(IntPtr data);
        public void Release(IntPtr reference);
    }

    private sealed partial class NativeCalls : INativeCalls
    {
        public int Find(IntPtr keychain, string service, string account, IntPtr length, IntPtr data, out IntPtr item)
        {
            byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
            byte[] accountBytes = Encoding.UTF8.GetBytes(account);
            return SecKeychainFindGenericPassword(keychain, (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes, length, data, out item);
        }

        public int CopyDefaultKeychain(out IntPtr keychain) => SecKeychainCopyDefault(out keychain);

        public int CopyItemKeychain(IntPtr item, out IntPtr keychain) => SecKeychainItemCopyKeychain(item, out keychain);

        public IEnumerable<IntPtr> OtherKeychains(IntPtr target)
        {
            Check(SecKeychainCopySearchList(out IntPtr list), ReadFailed);
            try
            {
                for (nint i = 0; i < CFArrayGetCount(list); i++)
                {
                    IntPtr candidate = CFArrayGetValueAtIndex(list, i);
                    if (CFEqual(candidate, target) == 0)
                    {
                        yield return candidate;
                    }
                }
            }
            finally
            {
                CFRelease(list);
            }
        }

        public IntPtr CreateAccess(string path, string description) => MacKeychainItem.CreateAccess(path, description);

        public int CreateFromContent(uint itemClass, ref AttributeList attributes, byte[] data, IntPtr keychain,
            IntPtr access, out IntPtr item) =>
            SecKeychainItemCreateFromContent(itemClass, ref attributes, (uint)data.Length, data, keychain, access, out item);

        public int ModifyAttributesAndData(IntPtr item, IntPtr attributes, ReadOnlyMemory<byte> data) =>
            SecKeychainItemModifyAttributesAndData(item, attributes, (uint)data.Length, data.Span);

        public int Delete(IntPtr item) => SecKeychainItemDelete(item);

        public void FreeContent(IntPtr data) => SecKeychainItemFreeContent(IntPtr.Zero, data);

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

    [LibraryImport(Security)]
    private static partial int SecKeychainCopySearchList(out IntPtr list);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFEqual(IntPtr first, IntPtr second);

    [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SecTrustedApplicationCreateFromPath(string path, out IntPtr application);

    [LibraryImport(Security)]
    private static partial int SecAccessCreate(IntPtr descriptor, IntPtr trustedList, out IntPtr access);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemCreateFromContent(uint itemClass, ref AttributeList attrList, uint length,
        byte[] data, IntPtr keychain, IntPtr initialAccess, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length,
        ReadOnlySpan<byte> data);

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
