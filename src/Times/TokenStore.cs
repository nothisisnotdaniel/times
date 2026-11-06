using System.Runtime.InteropServices;
using System.Text;

namespace Times;

/// <summary>
/// Why the OS credential store could not be used. The message is fixed and safe to show: it never
/// holds the token or the organisation.
/// </summary>
internal sealed class TokenStoreException : Exception
{
    public TokenStoreException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Keeps the one Azure DevOps organisation and its personal access token in the OS credential store.
/// Nothing is ever written to a Times file. Tests inject a fake, so no test touches a real store.
/// </summary>
internal interface ITokenStore
{
    /// <summary>False where there is no supported store; then nothing else may be called.</summary>
    public bool IsSupported { get; }

    /// <summary>The stored organisation and token, or <c>null</c> if none. Throws <see cref="TokenStoreException"/>.</summary>
    public (string Organization, string Pat)? Read();

    /// <summary>Stores the pair, replacing any earlier one. Throws <see cref="TokenStoreException"/>.</summary>
    public void Write(string organization, string pat);

    /// <summary>Removes the stored pair. False if there was none. Throws <see cref="TokenStoreException"/>.</summary>
    public bool Delete();
}

/// <summary>Picks the token store for the running OS.</summary>
internal sealed class TokenStore
{
    private TokenStore()
    {
    }

    /// <summary>The macOS Keychain, the Windows Credential Manager, or the unsupported store anywhere else.</summary>
    public static ITokenStore ForCurrentPlatform() => For(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

    /// <summary>The store for the given OS; the selection rule, separate so tests can check it without a real store.</summary>
    internal static ITokenStore For(bool macOS, bool windows)
    {
        if (macOS)
        {
            return new MacKeychainTokenStore();
        }

        if (windows)
        {
            return new WindowsCredentialTokenStore();
        }

        return new UnsupportedTokenStore();
    }
}

/// <summary>Linux and anything else: no store. It never touches a file and refuses every call.</summary>
internal sealed class UnsupportedTokenStore : ITokenStore
{
    public const string NotAvailable = "No credential store is available on this system.";

    public bool IsSupported => false;

    public (string Organization, string Pat)? Read() => throw new TokenStoreException(NotAvailable);

    public void Write(string organization, string pat) => throw new TokenStoreException(NotAvailable);

    public bool Delete() => throw new TokenStoreException(NotAvailable);
}

/// <summary>
/// A generic-password item in the default (login) keychain through Security.framework: service
/// <see cref="Service"/>, account = organisation, password data = PAT. Buffers the framework returns
/// are freed with <c>SecKeychainItemFreeContent</c>, item references with <c>CFRelease</c>, and
/// managed byte copies of the token are zeroed after use.
/// </summary>
internal sealed partial class MacKeychainTokenStore : ITokenStore
{
    public const string Service = "Times Azure DevOps";

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    // 'acct', the account attribute tag.
    private const uint AccountAttribute = 0x61636374;

    // Delete removes duplicates too, but never loops forever.
    private const int MaxItems = 16;

    public bool IsSupported => true;

    public (string Organization, string Pat)? Read()
    {
        byte[] service = Encoding.UTF8.GetBytes(Service);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, 0, null,
            out uint passwordLength, out IntPtr passwordData, out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException("The macOS Keychain could not be read.");
        }

        try
        {
            string pat = CopyAndFree(passwordData, passwordLength);
            string organization = ReadAccount(item);
            return (organization, pat);
        }
        finally
        {
            CFRelease(item);
        }
    }

    public void Write(string organization, string pat)
    {
        DeleteAll();
        byte[] service = Encoding.UTF8.GetBytes(Service);
        byte[] account = Encoding.UTF8.GetBytes(organization);
        byte[] password = Encoding.UTF8.GetBytes(pat);
        try
        {
            int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                (uint)account.Length, account, (uint)password.Length, password, out IntPtr item);
            if (status != ErrSecSuccess)
            {
                throw new TokenStoreException("The token could not be saved in the macOS Keychain.");
            }

            CFRelease(item);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public bool Delete() => DeleteAll();

    private static bool DeleteAll()
    {
        byte[] service = Encoding.UTF8.GetBytes(Service);
        bool deleted = false;
        for (int i = 0; i < MaxItems; i++)
        {
            int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, 0, null,
                out _, IntPtr.Zero, out IntPtr item);
            if (status == ErrSecItemNotFound)
            {
                return deleted;
            }

            if (status != ErrSecSuccess)
            {
                throw new TokenStoreException("The macOS Keychain could not be read.");
            }

            try
            {
                if (SecKeychainItemDelete(item) != ErrSecSuccess)
                {
                    throw new TokenStoreException("The token could not be removed from the macOS Keychain.");
                }

                deleted = true;
            }
            finally
            {
                CFRelease(item);
            }
        }

        throw new TokenStoreException("The macOS Keychain holds too many Times items.");
    }

    // Copies the framework's buffer into a string, zeroes the managed copy and frees the buffer.
    private static string CopyAndFree(IntPtr data, uint length)
    {
        byte[] bytes = new byte[length];
        try
        {
            if (length > 0)
            {
                Marshal.Copy(data, bytes, 0, (int)length);
            }

            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes);
            SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
    }

    // The item's account attribute (the organisation), read with SecKeychainItemCopyContent and freed after.
    private static unsafe string ReadAccount(IntPtr item)
    {
        Attribute attribute = new Attribute { Tag = AccountAttribute };
        AttributeList list = new AttributeList { Count = 1, Attributes = (IntPtr)(&attribute) };
        int status = SecKeychainItemCopyContent(item, IntPtr.Zero, ref list, IntPtr.Zero, IntPtr.Zero);
        if (status != ErrSecSuccess)
        {
            throw new TokenStoreException("The macOS Keychain could not be read.");
        }

        try
        {
            Attribute filled = *(Attribute*)list.Attributes;
            return filled.Data == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(filled.Data, (int)filled.Length);
        }
        finally
        {
            SecKeychainItemFreeContent(ref list, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Attribute
    {
        public uint Tag;
        public uint Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeList
    {
        public uint Count;
        public IntPtr Attributes;
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[]? accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    // The same call without the password: passwordData NULL, so the token is never copied out.
    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength,
        byte[] serviceName, uint accountNameLength, byte[]? accountName, out uint passwordLength,
        IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemCopyContent(IntPtr itemRef, IntPtr itemClass, ref AttributeList attrList,
        IntPtr length, IntPtr outData);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(ref AttributeList attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}

/// <summary>
/// A generic credential in the Windows Credential Manager (advapi32): target <see cref="Target"/>,
/// user name = organisation, blob = PAT (UTF-8), persisted for the local machine. Buffers from
/// <c>CredReadW</c> are freed with <c>CredFree</c>; unmanaged copies of the token are zeroed before they are freed.
/// </summary>
internal sealed partial class WindowsCredentialTokenStore : ITokenStore
{
    public const string Target = "Times/AzureDevOps";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsSupported => true;

    public unsafe (string Organization, string Pat)? Read()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out IntPtr pointer))
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
            try
            {
                if (blob.Length > 0)
                {
                    Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                }

                string pat = Encoding.UTF8.GetString(blob);
                string organization = credential.UserName == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
                return (organization, pat);
            }
            finally
            {
                Array.Clear(blob);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string organization, string pat)
    {
        byte[] blob = Encoding.UTF8.GetBytes(pat);
        IntPtr target = Marshal.StringToHGlobalUni(Target);
        IntPtr userName = Marshal.StringToHGlobalUni(organization);
        IntPtr data = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            Credential credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };

            // CredWrite replaces an existing credential with the same target and type.
            if (!CredWrite(ref credential, 0))
            {
                throw new TokenStoreException("The token could not be saved in the Windows Credential Manager.");
            }
        }
        finally
        {
            Array.Clear(blob);
            Marshal.Copy(new byte[blob.Length], 0, data, blob.Length);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(userName);
            Marshal.FreeHGlobal(target);
        }
    }

    public bool Delete()
    {
        if (CredDelete(Target, CredTypeGeneric, 0))
        {
            return true;
        }

        if (Marshal.GetLastPInvokeError() == ErrorNotFound)
        {
            return false;
        }

        throw new TokenStoreException("The token could not be removed from the Windows Credential Manager.");
    }

    // CREDENTIALW. FILETIME is two DWORDs.
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
