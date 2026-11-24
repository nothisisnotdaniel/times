using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Times;

internal sealed partial class MacKeychainItem
{
    internal partial class NativeCalls
    {
        // A local lookup shares the FileDL lock. A writer pins the validated inode
        // while FileDL takes its own exclusive lock. Remote volumes use FileDL directly.
        private SafeFileHandle? OpenLocalLock(byte[] path, bool lookup)
        {
            int status = OpenLock([.. path, 0], lookup ? 1 : 0, out int fd);
            Check(Report(lookup ? "open lookup lock" : "open writer lock", status), SaveFailed);
            return fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
        }

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesOpenKeychainLock")]
        private static partial int OpenLock(byte[] path, int lookup, out int fd);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesCheckWriterLock")]
        private static partial int CheckWriterLock(SafeFileHandle fd);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesOpenProbeDatabase")]
        private static partial int OpenProbeDatabase(byte[] path);

        [LibraryImport("TimesIssuerKeychain", EntryPoint = "TimesDatabaseAtPath")]
        private static partial int DatabaseAtPath(SafeFileHandle fd, byte[] path);
    }
}
