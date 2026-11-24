// Standalone, metadata-only verifier. The parent authenticates this running
// image before sending a digest. Nothing from the Keychain goes to stdout.
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <CommonCrypto/CommonDigest.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>

int TimesDatabaseAtPath(int fd, const char *path);
OSStatus TimesCheckPasswordDatabase(SecKeychainRef database);
OSStatus TimesCopyPasswordReference(SecKeychainItemRef item, CFDataRef *result);
OSStatus TimesCopyPasswordAccess(SecKeychainItemRef item, CFArrayRef *result);
OSStatus TimesPasswordStamp(SecKeychainItemRef item, int length, uint8_t *digest, const char **stage);
OSStatus TimesReadViewDigest(CFDataRef persistent, CFArrayRef policy, const uint8_t *stamp, uint8_t *digest);

enum { Refused = 1, Found = 40, Missing = 41 };

static int lookup(SecKeychainRef database, const char *service, const char *account, const uint8_t *expected)
{
    SecKeychainAttribute attributes[] = {
        {kSecServiceItemAttr, (UInt32)strlen(service), (void *)service},
        {kSecAccountItemAttr, (UInt32)strlen(account), (void *)account}
    };
    SecKeychainAttributeList list = {2, attributes};
    SecKeychainSearchRef search = NULL;
    SecKeychainItemRef item = NULL, duplicate = NULL;
    CFDataRef persistent = NULL;
    CFArrayRef policy = NULL;
    uint8_t stamp[CC_SHA256_DIGEST_LENGTH], digest[CC_SHA256_DIGEST_LENGTH];
    const char *stage = NULL;
    int result = Refused;
    OSStatus status = SecKeychainSearchCreateFromAttributes(database, kSecGenericPasswordItemClass, &list, &search);
    if (!status && search) {
        status = SecKeychainSearchCopyNext(search, &item);
        if (status == errSecItemNotFound && !expected) result = Missing;
        if (!status && item && SecKeychainSearchCopyNext(search, &duplicate) == errSecItemNotFound) {
            if (!expected || (!TimesCopyPasswordReference(item, &persistent) && !TimesCopyPasswordAccess(item, &policy) &&
                !TimesPasswordStamp(item, -1, stamp, &stage) && !TimesReadViewDigest(persistent, policy, stamp, digest) &&
                !timingsafe_bcmp(digest, expected, sizeof(digest)))) result = Found;
        }
    }
    if (policy) CFRelease(policy);
    if (persistent) CFRelease(persistent);
    if (duplicate) CFRelease(duplicate);
    if (item) CFRelease(item);
    if (search) CFRelease(search);
    return result;
}

int main(int argc, char **argv)
{
    if (argc != 5 || argv[1][0] != '/' || strlen(argv[1]) >= PATH_MAX ||
        strcmp(argv[2], "Suite Pro signing key") != 0 || !argv[3][0] || strlen(argv[3]) > 16 ||
        argv[3][strspn(argv[3], "abcdefghijklmnopqrstuvwxyz0123456789-")]) return 1;
    bool presence = !strcmp(argv[4], "presence");
    if (!presence && strcmp(argv[4], "view")) return Refused;
    uint8_t expected[CC_SHA256_DIGEST_LENGTH];
    if (presence ? fgetc(stdin) != 1 : fread(expected, 1, sizeof(expected), stdin) != sizeof(expected)) return Refused;
    if (fgetc(stdin) != EOF || TimesDatabaseAtPath(3, argv[1])) return Refused;
    SecKeychainRef database = NULL;
    SecKeychainStatus permissions = 0;
    char path[PATH_MAX];
    UInt32 size = sizeof(path);
    bool ok = !SecKeychainSetUserInteractionAllowed(false) && !SecKeychainOpen(argv[1], &database) && database &&
        !SecKeychainGetPath(database, &size, path) && size == strlen(argv[1]) && !memcmp(argv[1], path, size) &&
        !SecKeychainGetStatus(database, &permissions) && (permissions & kSecReadPermStatus) &&
        !TimesCheckPasswordDatabase(database);
    int result = ok ? lookup(database, argv[2], argv[3], presence ? NULL : expected) : Refused;
    if (database) CFRelease(database);
    return TimesDatabaseAtPath(3, argv[1]) ? Refused : result;
}
