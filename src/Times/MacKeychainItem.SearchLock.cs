using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Times;

internal sealed partial class MacKeychainItem
{
    internal partial class NativeCalls
    {
        // AppleFileDL's AtomicFile.cpp, LocalFileLocker: all local writers flock this
        // read-only lock file. A shared lock also works for System.keychain, whose parent
        // directory need not be writable. Network volumes use FileDL's transaction instead.
        private static SafeFileHandle? TryLockLocalFile(byte[] path)
        {
            int slash = path.AsSpan().LastIndexOf((byte)'/');
            if (slash < 0 || slash == path.Length - 1)
            {
                throw new TokenStoreException(SaveFailed);
            }

            byte[] directory = new byte[slash + 2];
            path.AsSpan(0, slash + 1).CopyTo(directory);
            AttributeSelection volume = new() { Count = 5, Volume = 0x80004000 }; // ATTR_VOL_INFO | MOUNTFLAGS
            if (getattrlist(directory, in volume, out VolumeAttributes mount, 8, 0) != 0 || mount.Size != 8)
            {
                throw new TokenStoreException(SaveFailed);
            }

            if ((mount.Flags & 0x1000) == 0) // MNT_LOCAL
            {
                return null;
            }

            // The first 32 bits of SHA-1 identify a file name, not a credential.
            Span<byte> digest = stackalloc byte[SHA1.HashSizeInBytes];
            SHA1.HashData(path.AsSpan(slash + 1), digest);
            byte[] lockPath = new byte[slash + 1 + 11 + 1];
            path.AsSpan(0, slash + 1).CopyTo(lockPath);
            ".fl"u8.CopyTo(lockPath.AsSpan(slash + 1));
            Utf8Formatter.TryFormat(BinaryPrimitives.ReadUInt32BigEndian(digest), lockPath.AsSpan(slash + 4),
                out _, new StandardFormat('X', 8));

            const int OpenFlags = 0x01000000 | 0x0100 | 0x0004; // O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK
            int fd = open(lockPath, OpenFlags);
            if (fd < 0)
            {
                throw new TokenStoreException(SaveFailed);
            }

            SafeFileHandle handle = new SafeFileHandle(fd, ownsHandle: true);
            bool held = false;
            try
            {
                ValidateLockFile(handle);
                int result;
                do
                {
                    result = flock(handle, 1); // LOCK_SH
                }
                while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR

                if (result != 0)
                {
                    throw new TokenStoreException(SaveFailed);
                }

                ValidateLockFile(handle);
                held = true;
                return handle;
            }
            finally
            {
                if (!held)
                {
                    handle.Dispose();
                }
            }
        }

        private static void ValidateLockFile(SafeFileHandle handle)
        {
            AttributeSelection file = new() { Count = 5, Common = 8, File = 1 }; // OBJTYPE, LINKCOUNT
            if (fgetattrlist(handle, in file, out LockAttributes attrs, 12, 0) != 0
                || attrs.Size != 12 || attrs.Type != 1 || attrs.Links != 1)
            {
                throw new TokenStoreException(SaveFailed);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeSelection
        {
            public ushort Count;
            public ushort Reserved;
            public uint Common;
            public uint Volume;
            public uint Directory;
            public uint File;
            public uint Fork;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VolumeAttributes
        {
            public uint Size;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LockAttributes
        {
            public uint Size;
            public uint Type;
            public uint Links;
        }

        [LibraryImport("libc")]
        private static partial int getattrlist(byte[] path, in AttributeSelection attributes, out VolumeAttributes result,
            nuint size, nuint options);

        [LibraryImport("libc")]
        private static partial int fgetattrlist(SafeFileHandle fd, in AttributeSelection attributes, out LockAttributes result,
            nuint size, nuint options);

        [LibraryImport("libc")]
        private static partial int open(byte[] path, int flags);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int flock(SafeFileHandle fd, int operation);
    }
}
