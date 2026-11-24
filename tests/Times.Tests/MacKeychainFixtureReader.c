// Compiled per reader with the disposable database path and marker service.
// No operation accepts a database, account, service or executable from its caller.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <CommonCrypto/CommonDigest.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

// A marker created through the same API as the parent lives only in this database.
static OSStatus registerReader(SecKeychainRef db)
{
    const uint8_t marker = 1;
    CFDataRef data = CFDataCreate(NULL, &marker, sizeof(marker));
    if (!data) return errSecAllocate;
    const void *keys[] = { kSecClass, kSecUseKeychain, kSecAttrService, kSecAttrAccount, kSecValueData };
    const void *values[] = { kSecClassGenericPassword, db, CFSTR(READER_SERVICE), CFSTR("maker"), data };
    CFDictionaryRef query = CFDictionaryCreate(NULL, keys, values, 5, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    OSStatus status = query ? SecItemAdd(query, NULL) : errSecAllocate;
    if (query) CFRelease(query);
    CFRelease(data);
    return status;
}

// Measurements report the operating system's decision separately from byte equality.
static OSStatus verifySample(SecKeychainItemRef item, int measure)
{
    uint8_t expected[CC_SHA256_DIGEST_LENGTH], original[CC_SHA256_DIGEST_LENGTH], actual[CC_SHA256_DIGEST_LENGTH];
    if (fread(expected, 1, sizeof(expected), stdin) != sizeof(expected)) return errSecParam;
    if (measure && fread(original, 1, sizeof(original), stdin) != sizeof(original)) return errSecParam;
    UInt32 size = 0;
    void *bytes = NULL;
    OSStatus status = SecKeychainItemCopyContent(item, NULL, NULL, &size, &bytes);
    int matches = 0, retained = 0;
    if (!status && bytes && size <= 16384 && CC_SHA256(bytes, size, actual)) {
        matches = !timingsafe_bcmp(expected, actual, sizeof(actual));
        retained = measure && !timingsafe_bcmp(original, actual, sizeof(actual));
    }
    if (bytes) {
        memset_s(bytes, size, 0, size);
        SecKeychainItemFreeContent(NULL, bytes);
    }
    if (measure) {
        printf("%d %d %d\n", (int)status, matches, retained);
        return 0;
    }
    return status ? status : matches ? 0 : errSecDecode;
}

int main(int argc, char **argv)
{
    const char *root = getenv("TIMES_TEST_KEYCHAIN_ROOT");
    if (argc != 2 || !root || !*root || strncmp(FIXTURE_DB, root, strlen(root))
        || FIXTURE_DB[strlen(root)] != '/') return 1;
    SecKeychainRef db = NULL;
    SecKeychainItemRef item = NULL;
    OSStatus status = SecKeychainSetUserInteractionAllowed(false);
    if (!status) status = SecKeychainOpen(FIXTURE_DB, &db);
    if (!status && !db) status = errSecNoSuchKeychain;
    char path[PATH_MAX];
    UInt32 length = sizeof(path);
    if (!status) status = SecKeychainGetPath(db, &length, path);
    if (!status && (length != sizeof(FIXTURE_DB) - 1 || memcmp(path, FIXTURE_DB, length))) status = errSecParam;
    if (!status && !strcmp(argv[1], "register")) {
        status = registerReader(db);
    } else if (!status) {
        status = SecKeychainFindGenericPassword(db, sizeof(FIXTURE_SERVICE) - 1, FIXTURE_SERVICE, 5, "maker",
                                              NULL, NULL, &item);
        if (!status) {
            if (!strcmp(argv[1], "verify") || !strcmp(argv[1], "measure")) status = verifySample(item, !strcmp(argv[1], "measure"));
            else status = errSecParam;
        } else if (!strcmp(argv[1], "measure")) {
            printf("%d 0 0\n", (int)status);
            status = 0;
        }
    }
    if (item) CFRelease(item);
    if (db) CFRelease(db);
    if (status) fprintf(stderr, "Fixture operation returned %d\n", (int)status);
    return status ? 1 : 0;
}
