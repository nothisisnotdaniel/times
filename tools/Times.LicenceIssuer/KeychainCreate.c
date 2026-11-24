// Build the public attributes on a disposable, non-secret copy source. File
// Keychain's create-from-content API may redirect an unavailable destination.
// CreateCopy takes the non-null destination directly at its only insertion.
#define _DARWIN_C_SOURCE
#define __STDC_WANT_LIB_EXT1__ 1
#include <CoreServices/CoreServices.h>
#include <Security/Security.h>
#include <fcntl.h>
#include <limits.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

int TimesOpenProbeDatabase(const char *path);
int TimesDatabaseAtPath(int fd, const char *path);
int TimesRemoveScratchLock(int directory, const char *name);

static bool sameDirectory(int fd, const char *path)
{
    struct stat pinned, named;
    return fd >= 0 && !fstat(fd, &pinned) && !lstat(path, &named) && S_ISDIR(named.st_mode)
        && pinned.st_uid == geteuid() && pinned.st_dev == named.st_dev && pinned.st_ino == named.st_ino;
}

OSStatus TimesCopyNewPassword(SecKeychainRef target, int pinned, const char *path, SecAccessRef access,
                              SecKeychainAttributeList *attributes, const UInt8 *data, UInt32 length,
                              SecKeychainItemRef *result)
{
    *result = NULL;
    if (!target || !access || !attributes || !data || !length || TimesDatabaseAtPath(pinned, path))
        return errSecNoSuchKeychain;
    // Keep the metadata source in its own 0700 directory beside the selected database.
    const char *slash = strrchr(path, '/');
    char folder[PATH_MAX], sourcePath[PATH_MAX];
    if (!slash || snprintf(folder, sizeof(folder), "%.*s/.times-create.XXXXXX", (int)(slash - path), path)
            >= (int)sizeof(folder) || !mkdtemp(folder)) return errSecIO;
    const char *name = "attributes.keychain-db";
    int directory = open(folder, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    int revision = -1;
    SecKeychainRef source = NULL;
    SecKeychainItemRef blank = NULL, form = NULL;
    UInt8 password[32];
    arc4random_buf(password, sizeof(password));
    OSStatus status = errSecIO;
    if (!sameDirectory(directory, folder) || snprintf(sourcePath, sizeof(sourcePath), "%s/%s", folder, name)
        >= (int)sizeof(sourcePath))
        goto done;
    status = SecKeychainCreate(sourcePath, sizeof(password), password, false, NULL, &source);
    if (!status && !source) status = errSecInvalidKeychain;
    if (status) goto done;
    // Both copies have explicit destinations. Only public form fields and one
    // dummy byte are ever saved to the scratch database.
    const UInt8 placeholder = 0;
    status = KCNewItem(kSecGenericPasswordItemClass, 0, 1, &placeholder, &blank);
    if (!status) status = SecKeychainItemCreateCopy(blank, source, access, &form);
    if (!status && !form) status = errSecInvalidItemRef;
    if (!status) status = SecKeychainItemModifyAttributesAndData(form, attributes, 0, NULL);
    if (!status) {
        // FileDL publishes through a rename. Pin the public template and make both
        // it and its private directory read-only before supplying any key bytes.
        revision = TimesOpenProbeDatabase(sourcePath);
        if (revision < 0 || !sameDirectory(directory, folder) || fchmod(directory, 0500)
            || fchmod(revision, 0400) || TimesDatabaseAtPath(revision, sourcePath)) status = errSecIO;
    }
    if (!status && TimesDatabaseAtPath(pinned, path)) status = errSecNoSuchKeychain;
    // KCSetData edits the in-memory item. KCUpdateItem would persist it to its
    // source. Instead copy the edited item straight to the selected target.
    if (!status) status = KCSetData(form, length, data);
    if (!status) status = SecKeychainItemCreateCopy(form, target, access, result);
    if (!status && (!*result || TimesDatabaseAtPath(revision, sourcePath))) status = errSecIO;
done:
    memset_s(password, sizeof(password), 0, sizeof(password));
    if (form) CFRelease(form);
    if (blank) CFRelease(blank);
    if (revision >= 0) {
        if (fchmod(revision, 0600) && !status) status = errSecIO;
        close(revision);
    }
    // Remove only our own database, its known FileDL lock, and its empty directory.
    if (sameDirectory(directory, folder)) {
        if (fchmod(directory, 0700) && !status) status = errSecIO;
        if (source) {
            OSStatus cleanup = SecKeychainDelete(source);
            if (!status) status = cleanup;
        }
        if (TimesRemoveScratchLock(directory, name) && !status) status = errSecIO;
        if ((!sameDirectory(directory, folder) || rmdir(folder)) && !status) status = errSecIO;
    } else if (!status) {
        status = errSecIO;
    }
    if (source) CFRelease(source);
    if (directory >= 0) close(directory);
    if (status && *result) { CFRelease(*result); *result = NULL; }
    return status;
}
