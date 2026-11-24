// Reserve the requested primary key with public data in the target's open
// transaction. Only the returned destination item can receive private bytes.
#define _DARWIN_C_SOURCE
#include <CoreServices/CoreServices.h>
#include <Security/Security.h>
#include <fcntl.h>
#include <limits.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

int TimesDatabaseAtPath(int fd, const char *path);
int TimesRemoveScratchLock(int directory, const char *name);

static bool sameDirectory(int fd, const char *path)
{
    struct stat pinned, named;
    return fd >= 0 && !fstat(fd, &pinned) && !lstat(path, &named) && S_ISDIR(named.st_mode)
        && pinned.st_uid == geteuid() && pinned.st_dev == named.st_dev && pinned.st_ino == named.st_ino;
}

OSStatus TimesReservePassword(SecKeychainRef target, int pinned, const char *path, SecAccessRef access,
                              SecKeychainAttributeList *attributes, const UInt8 *password, UInt32 passwordLength,
                              SecKeychainItemRef *result)
{
    *result = NULL;
    if (!target || !access || !attributes || TimesDatabaseAtPath(pinned, path))
        return errSecNoSuchKeychain;
    if (!password || passwordLength != 64) return errSecParam;
    for (UInt32 i = 0; i < passwordLength; ++i) {
        if (!(password[i] >= '0' && password[i] <= '9') && !(password[i] >= 'a' && password[i] <= 'f'))
            return errSecParam;
    }
    // Keep the metadata source in its own 0700 directory beside the selected database.
    const char *slash = strrchr(path, '/');
    char folder[PATH_MAX], sourcePath[PATH_MAX];
    if (!slash || snprintf(folder, sizeof(folder), "%.*s/.times-create.XXXXXX", (int)(slash - path), path)
            >= (int)sizeof(folder) || !mkdtemp(folder)) return errSecIO;
    const char *name = "attributes.keychain-db";
    int directory = open(folder, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    SecKeychainRef source = NULL;
    SecKeychainItemRef blank = NULL, form = NULL;
    OSStatus status = errSecIO;
    if (!sameDirectory(directory, folder) || snprintf(sourcePath, sizeof(sourcePath), "%s/%s", folder, name)
        >= (int)sizeof(sourcePath))
        goto done;
    status = SecKeychainCreate(sourcePath, passwordLength, password, false, NULL, &source);
    if (!status && !source) status = errSecInvalidKeychain;
    if (status) goto done;
    // Both copies have explicit destinations. Only public form fields and one
    // dummy byte are ever saved to the scratch database.
    const UInt8 placeholder = 0;
    status = KCNewItem(kSecGenericPasswordItemClass, 0, 1, &placeholder, &blank);
    if (!status) status = SecKeychainItemCreateCopy(blank, source, access, &form);
    if (!status && !form) status = errSecInvalidItemRef;
    if (!status) status = SecKeychainItemModifyAttributesAndData(form, attributes, 0, NULL);
    if (!status && TimesDatabaseAtPath(pinned, path)) status = errSecNoSuchKeychain;
    if (!status) status = SecKeychainItemCreateCopy(form, target, access, result);
done:
    if (form) CFRelease(form);
    if (blank) CFRelease(blank);
    // Remove only our own database, its known FileDL lock, and its empty directory.
    if (sameDirectory(directory, folder)) {
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
    if (!status && !*result) status = errSecInvalidItemRef;
    if (status && *result) { CFRelease(*result); *result = NULL; }
    return status;
}
