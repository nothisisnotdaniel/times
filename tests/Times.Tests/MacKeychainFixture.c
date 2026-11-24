// Test-only helpers. Creation receives an explicit disposable Keychain and reads receive an item reference.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <string.h>
#include <strings.h>

extern OSStatus SecTrustedApplicationCreateFromRequirement(const char *, SecRequirementRef, SecTrustedApplicationRef *);

// One ACL subject for two ordinary applications. Use only public requirements
// of the test host and a reader compiled for this disposable database.
static OSStatus fixtureReaderPair(const char *readerPath, SecTrustedApplicationRef *reader)
{
    SecCodeRef host = NULL;
    SecStaticCodeRef helper = NULL, hostCode = NULL;
    SecRequirementRef first = NULL, second = NULL, pair = NULL;
    CFStringRef left = NULL, right = NULL, expression = NULL;
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)readerPath, strlen(readerPath), false);
    OSStatus status = url ? SecStaticCodeCreateWithPath(url, 0, &helper) : errSecAllocate;
    if (!status) status = SecCodeCopySelf(0, &host);
    if (!status) status = SecCodeCopyStaticCode(host, 0, &hostCode);
    if (!status) status = SecCodeCopyDesignatedRequirement(hostCode, 0, &first);
    if (!status) status = SecCodeCopyDesignatedRequirement(helper, 0, &second);
    if (!status) status = SecRequirementCopyString(first, 0, &left);
    if (!status) status = SecRequirementCopyString(second, 0, &right);
    if (!status && CFEqual(left, right)) status = errSecParam;
    if (!status) {
        expression = CFStringCreateWithFormat(NULL, NULL, CFSTR("(%@) or (%@)"), left, right);
        status = expression ? SecRequirementCreateWithString(expression, 0, &pair) : errSecAllocate;
    }
    if (!status) status = SecTrustedApplicationCreateFromRequirement(readerPath, pair, reader);
    if (pair) CFRelease(pair);
    if (expression) CFRelease(expression);
    if (right) CFRelease(right);
    if (left) CFRelease(left);
    if (second) CFRelease(second);
    if (first) CFRelease(first);
    if (helper) CFRelease(helper);
    if (hostCode) CFRelease(hostCode);
    if (host) CFRelease(host);
    if (url) CFRelease(url);
    return status;
}

enum { NamedReaders, IncludeCaller, ReaderPair };

OSStatus TimesFixtureAdd(SecKeychainRef database, const char *service, const char *first, const char *second,
                         int readerMode, const UInt8 *bytes, UInt32 length)
{
    if (!database || !service || !bytes) return errSecParam;
    CFStringRef name = CFStringCreateWithCString(NULL, service, kCFStringEncodingUTF8);
    CFMutableArrayRef readers = CFArrayCreateMutable(NULL, 0, &kCFTypeArrayCallBacks);
    CFDataRef value = CFDataCreateWithBytesNoCopy(NULL, bytes, length, kCFAllocatorNull);
    SecAccessRef access = NULL;
    OSStatus status = errSecAllocate;
    if (!name || !readers || !value) goto done;
    const char *paths[] = {NULL, first, second};
    if (readerMode == ReaderPair && !first) { status = errSecParam; goto done; }
    int count = readerMode == ReaderPair ? 1 : 3;
    for (int i = readerMode == NamedReaders ? 1 : 0; i < count; ++i) {
        if (i != 0 && !paths[i]) continue;
        SecTrustedApplicationRef reader = NULL;
        status = readerMode == ReaderPair ? fixtureReaderPair(first, &reader) : SecTrustedApplicationCreateFromPath(paths[i], &reader);
        if (status) goto done;
        CFArrayAppendValue(readers, reader);
        CFRelease(reader);
    }
    status = SecAccessCreate(name, readers, &access);
    if (!status) {
        const void *keys[] = {kSecClass, kSecUseKeychain, kSecAttrService, kSecAttrAccount, kSecAttrLabel, kSecAttrAccess, kSecValueData};
        const void *values[] = {kSecClassGenericPassword, database, name, CFSTR("maker"), name, access, value};
        CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 7,
            &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        status = attributes ? SecItemAdd(attributes, NULL) : errSecAllocate;
        if (attributes) CFRelease(attributes);
    }
done:
    if (access) CFRelease(access);
    if (value) CFRelease(value);
    if (readers) CFRelease(readers);
    if (name) CFRelease(name);
    return status;
}

OSStatus TimesFixtureRead(SecKeychainItemRef item, const UInt8 *expected, UInt32 expectedSize,
                          const UInt8 *original, UInt32 originalSize, int *matches, int *retained)
{
    *matches = *retained = 0;
    if (!item) return errSecInvalidItemRef;
    void *bytes = NULL;
    UInt32 size = 0;
    OSStatus status = SecKeychainItemCopyContent(item, NULL, NULL, &size, &bytes);
    if (!status && bytes) {
        *matches = size == expectedSize && !timingsafe_bcmp(bytes, expected, size);
        *retained = size == originalSize && !timingsafe_bcmp(bytes, original, size);
    }
    if (bytes) {
        memset_s(bytes, size, 0, size);
        SecKeychainItemFreeContent(NULL, bytes);
    }
    return status;
}

// Independent control for the dictionary-based public API. Both the database
// and the single item were selected before this call on this case's fixture.
OSStatus TimesFixtureUpdate(SecKeychainRef database, SecKeychainItemRef item, const UInt8 *bytes, UInt32 length)
{
    if (!database || !item || !bytes) return errSecParam;
    const void *db = database, *target = item;
    CFArrayRef scope = CFArrayCreate(NULL, &db, 1, &kCFTypeArrayCallBacks);
    CFArrayRef items = CFArrayCreate(NULL, &target, 1, &kCFTypeArrayCallBacks);
    CFDataRef value = CFDataCreateWithBytesNoCopy(NULL, bytes, length, kCFAllocatorNull);
    OSStatus status = errSecAllocate;
    if (scope && items && value) {
        const void *keys[] = {kSecClass, kSecMatchSearchList, kSecMatchItemList};
        const void *values[] = {kSecClassGenericPassword, scope, items};
        const void *field = kSecValueData, *data = value;
        CFDictionaryRef query = CFDictionaryCreate(NULL, keys, values, 3,
            &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        CFDictionaryRef change = CFDictionaryCreate(NULL, &field, &data, 1,
            &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        if (query && change) status = SecItemUpdate(query, change);
        if (query) CFRelease(query);
        if (change) CFRelease(change);
    }
    if (value) CFRelease(value);
    if (items) CFRelease(items);
    if (scope) CFRelease(scope);
    return status;
}

// Independent public access objects. These policies are never installed on an item.
OSStatus TimesFixtureAccessVariant(int field, SecAccessRef *access)
{
    CSSM_LIST_ELEMENT any = {.WordID = CSSM_ACL_SUBJECT_TYPE_ANY, .ElementType = CSSM_LIST_ELEMENT_WORDID};
    CSSM_LIST subject = {CSSM_LIST_TYPE_UNKNOWN, &any, &any};
    CSSM_ACL_OWNER_PROTOTYPE owner = {.TypedSubject = subject, .Delegate = field == 1};
    CSSM_ACL_AUTHORIZATION_TAG right = field == 4 ? CSSM_ACL_AUTHORIZATION_ENCRYPT : CSSM_ACL_AUTHORIZATION_DECRYPT;
    CSSM_ACL_ENTRY_INFO entry = {.EntryPublicInfo = {.TypedSubject = subject, .Delegate = field == 3,
                                   .Authorization = {1, &right}}, .EntryHandle = 17};
    strcpy(entry.EntryPublicInfo.EntryTag, field == 2 ? "other" : "sample");
    return SecAccessCreateFromOwnerAndACL(&owner, 1, &entry, access);
}
