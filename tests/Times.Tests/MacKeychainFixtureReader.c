// Compiled per case with the disposable database path and both reader paths.
// No operation accepts a database, account, service or executable from its caller.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <CommonCrypto/CommonDigest.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern OSStatus SecKeychainItemCreateNew(SecItemClass, OSType, UInt32, const void *, SecKeychainItemRef *);
extern OSStatus SecKeychainItemSetAttribute(SecKeychainItemRef, SecKeychainAttribute *);
extern OSStatus SecKeychainItemAddNoUI(SecKeychainRef, SecKeychainItemRef);
extern OSStatus SecKeychainItemSetAccessWithPassword(SecKeychainItemRef, SecAccessRef, UInt32, const void *);
extern OSStatus SecKeychainGetKeychainVersion(SecKeychainRef, UInt32 *);

static OSStatus registerReader(SecKeychainRef db)
{
    const uint8_t marker = 1;
    SecKeychainItemRef pending = NULL;
    OSStatus status = SecKeychainItemCreateNew(kSecGenericPasswordItemClass, 0, sizeof(marker), &marker, &pending);
    SecKeychainAttribute service = { kSecServiceItemAttr, sizeof(READER_SERVICE) - 1, (void *)READER_SERVICE };
    SecKeychainAttribute account = { kSecAccountItemAttr, 5, (void *)"maker" };
    if (!status) status = SecKeychainItemSetAttribute(pending, &service);
    if (!status) status = SecKeychainItemSetAttribute(pending, &account);
    if (!status) status = SecKeychainItemAddNoUI(db, pending);
    if (pending) CFRelease(pending);
    return status;
}

static OSStatus configureReaders(SecKeychainItemRef item, int readersOnly)
{
    char password[32] = {0}; // generated password for this case only
    SecAccessRef access = NULL;
    CFArrayRef acls = NULL, existing = NULL;
    CFMutableArrayRef readers = NULL;
    CFStringRef description = NULL;
    SecKeychainPromptSelector selector = 0;
    OSStatus status = errSecParam;
    if (fread(password, 1, sizeof(password), stdin) != sizeof(password)) goto done;
    status = SecKeychainItemCopyAccess(item, &access);
    if (status) goto done;
    acls = SecAccessCopyMatchingACLList(access, kSecACLAuthorizationDecrypt);
    if (!acls || CFArrayGetCount(acls) != 1) { status = errSecInvalidItemRef; goto done; }
    SecACLRef acl = (SecACLRef)CFArrayGetValueAtIndex(acls, 0);
    status = SecACLCopyContents(acl, &existing, &description, &selector);
    if (status) goto done;
    if (!existing || CFArrayGetCount(existing) != 1) { status = errSecInvalidItemRef; goto done; }
    readers = CFArrayCreateMutableCopy(NULL, 0, existing);
    if (!readers) { status = errSecAllocate; goto done; }
    if (readersOnly) CFArrayRemoveAllValues(readers);
    const char *paths[] = { FIRST_READER, SECOND_READER };
    for (size_t i = 0; i < 2; i++) {
        SecTrustedApplicationRef app = NULL;
        status = SecTrustedApplicationCreateFromPath(paths[i], &app);
        if (status) goto done;
        CFArrayAppendValue(readers, app);
        CFRelease(app);
    }
    status = SecACLSetContents(acl, readers, description, selector);
    if (!status) status = SecKeychainItemSetAccessWithPassword(item, access, sizeof(password), password);
done:
    memset_s(password, sizeof(password), 0, sizeof(password));
    if (readers) CFRelease(readers);
    if (existing) CFRelease(existing);
    if (description) CFRelease(description);
    if (acls) CFRelease(acls);
    if (access) CFRelease(access);
    return status;
}

// Verify the bytes without returning them. The parent already holds the generated sample.
static OSStatus verifySample(SecKeychainItemRef item)
{
    uint8_t expected[CC_SHA256_DIGEST_LENGTH], actual[CC_SHA256_DIGEST_LENGTH];
    if (fread(expected, 1, sizeof(expected), stdin) != sizeof(expected)) return errSecParam;
    UInt32 size = 0;
    void *bytes = NULL;
    OSStatus status = SecKeychainItemCopyContent(item, NULL, NULL, &size, &bytes);
    if (!status) {
        if (!bytes || size > 16384 || !CC_SHA256(bytes, size, actual) || memcmp(expected, actual, sizeof(actual))) {
            status = errSecDecode;
        }
    }
    if (bytes) {
        memset_s(bytes, size, 0, size);
        SecKeychainItemFreeContent(NULL, bytes);
    }
    return status;
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
    UInt32 length = sizeof(path), version = 0;
    if (!status) status = SecKeychainGetPath(db, &length, path);
    if (!status && (length != sizeof(FIXTURE_DB) - 1 || memcmp(path, FIXTURE_DB, length))) status = errSecParam;
    if (!status) status = SecKeychainGetKeychainVersion(db, &version);
    if (!status && version < 0x200) status = errSecParam;
    if (!status && !strcmp(argv[1], "register")) {
        status = registerReader(db);
    } else if (!status) {
        status = SecKeychainFindGenericPassword(db, sizeof(FIXTURE_SERVICE) - 1, FIXTURE_SERVICE, 5, "maker",
                                              NULL, NULL, &item);
        if (!status) {
            if (!strcmp(argv[1], "verify")) status = verifySample(item);
            else if (!strcmp(argv[1], "include-issuer")) status = configureReaders(item, 0);
            else if (!strcmp(argv[1], "readers-only")) status = configureReaders(item, 1);
            else status = errSecParam;
        }
    }
    if (item) CFRelease(item);
    if (db) CFRelease(db);
    if (status) fprintf(stderr, "Fixture operation returned %d\n", (int)status);
    return status ? 1 : 0;
}
