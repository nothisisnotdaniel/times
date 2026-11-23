using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Times;

internal sealed partial class MacKeychainItem
{
    internal sealed partial class NativeCalls
    {
        // AppleFileDL's AtomicFile.cpp, LocalFileLocker: all local writers flock this
        // read-only lock file. A shared lock also works for System.keychain, whose parent
        // directory need not be writable. Network volumes use FileDL's transaction instead.
        private static SafeFileHandle? TryLockLocalFile(byte[] path)
        {
            int slash = path.AsSpan().LastIndexOf((byte)'/');
            if (slash < 0 || slash == path.Length - 1)
            {
                return null;
            }

            byte[] directory = new byte[slash + 2];
            path.AsSpan(0, slash + 1).CopyTo(directory);
            AttributeSelection volume = new() { Count = 5, Volume = 0x80004000 }; // ATTR_VOL_INFO | MOUNTFLAGS
            if (getattrlist(directory, in volume, out VolumeAttributes mount, 8, 0) != 0 ||
                mount.Size != 8 || (mount.Flags & 0x1000) == 0) // MNT_LOCAL
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
                return null;
            }

            SafeFileHandle handle = new SafeFileHandle(fd, ownsHandle: true);
            bool held = false;
            try
            {
                int result;
                do
                {
                    result = flock(handle, 1); // LOCK_SH
                }
                while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR

                AttributeSelection file = new() { Count = 5, Common = 8, File = 1 }; // OBJTYPE, LINKCOUNT
                held = result == 0 && fgetattrlist(handle, in file, out LockAttributes attributes, 12, 0) == 0
                    && attributes.Size == 12 && attributes.Type == 1 && attributes.Links > 0; // VREG, still linked
                return held ? handle : null;
            }
            finally
            {
                if (!held)
                {
                    handle.Dispose();
                }
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
