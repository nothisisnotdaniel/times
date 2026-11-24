// Test-only helpers. Creation receives an explicit disposable Keychain and reads receive an item reference.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <Security/cssmapple.h>
#include <CommonCrypto/CommonDigest.h>
#include <string.h>
#include <strings.h>

// Construct only an in-memory reader for the initial fixture descriptor. The
// documented access constructor accepts typed CSSM subjects. No item exists
// here, and the descriptor used to decode the reader is never installed.
static OSStatus fixtureReader(const char *comment, SecRequirementRef requirement, SecTrustedApplicationRef *result)
{
    CFDataRef encoded = NULL;
    OSStatus status = SecRequirementCopyData(requirement, 0, &encoded);
    if (status) return status;
    uint8_t hash[CC_SHA1_DIGEST_LENGTH] = {0};
    CSSM_ACL_KEYCHAIN_PROMPT_SELECTOR selector = {CSSM_ACL_KEYCHAIN_PROMPT_CURRENT_VERSION, 0};
    CSSM_LIST_ELEMENT app[] = {
        {.NextElement = &app[1], .WordID = CSSM_ACL_SUBJECT_TYPE_CODE_SIGNATURE, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &app[2], .WordID = CSSM_ACL_CODE_SIGNATURE_OSX, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &app[3], .ElementType = CSSM_LIST_ELEMENT_DATUM, .Element.Word = {sizeof(hash), hash}},
        {.NextElement = &app[4], .ElementType = CSSM_LIST_ELEMENT_DATUM, .Element.Word = {strlen(comment) + 1, (uint8_t *)comment}},
        {.ElementType = CSSM_LIST_ELEMENT_DATUM, .Element.Word = {CFDataGetLength(encoded), (uint8_t *)CFDataGetBytePtr(encoded)}}
    };
    CSSM_LIST_ELEMENT prompt[] = {
        {.NextElement = &prompt[1], .WordID = CSSM_ACL_SUBJECT_TYPE_KEYCHAIN_PROMPT, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &prompt[2], .ElementType = CSSM_LIST_ELEMENT_DATUM, .Element.Word = {sizeof(selector), (uint8_t *)&selector}},
        {.ElementType = CSSM_LIST_ELEMENT_DATUM, .Element.Word = {1, (uint8_t *)""}}
    };
    CSSM_LIST_ELEMENT choices[] = {
        {.NextElement = &choices[1], .WordID = CSSM_ACL_SUBJECT_TYPE_THRESHOLD, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &choices[2], .WordID = 1, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &choices[3], .WordID = 2, .ElementType = CSSM_LIST_ELEMENT_WORDID},
        {.NextElement = &choices[4], .ElementType = CSSM_LIST_ELEMENT_SUBLIST, .Element.Sublist = {CSSM_LIST_TYPE_UNKNOWN, app, &app[4]}},
        {.ElementType = CSSM_LIST_ELEMENT_SUBLIST, .Element.Sublist = {CSSM_LIST_TYPE_UNKNOWN, prompt, &prompt[2]}}
    };
    CSSM_ACL_AUTHORIZATION_TAG decrypt = CSSM_ACL_AUTHORIZATION_DECRYPT;
    CSSM_ACL_ENTRY_INFO entry = {.EntryPublicInfo = {.TypedSubject = {CSSM_LIST_TYPE_UNKNOWN, choices, &choices[4]},
                                                    .Authorization = {1, &decrypt}}, .EntryHandle = 1};
    CSSM_ACL_OWNER_PROTOTYPE owner = {.TypedSubject = {CSSM_LIST_TYPE_UNKNOWN, app, &app[4]}};
    SecAccessRef descriptor = NULL;
    CFArrayRef grants = NULL, readers = NULL;
    CFStringRef label = NULL;
    SecKeychainPromptSelector flags = 0;
    status = SecAccessCreateFromOwnerAndACL(&owner, 1, &entry, &descriptor);
    if (!status) {
        grants = SecAccessCopyMatchingACLList(descriptor, kSecACLAuthorizationDecrypt);
        status = grants && CFArrayGetCount(grants) == 1 ? SecACLCopyContents(
            (SecACLRef)CFArrayGetValueAtIndex(grants, 0), &readers, &label, &flags) : errSecInvalidItemRef;
    }
    if (!status && (!readers || CFArrayGetCount(readers) != 1)) status = errSecInvalidItemRef;
    if (!status) *result = (SecTrustedApplicationRef)CFRetain(CFArrayGetValueAtIndex(readers, 0));
    if (label) CFRelease(label);
    if (readers) CFRelease(readers);
    if (grants) CFRelease(grants);
    if (descriptor) CFRelease(descriptor);
    CFRelease(encoded);
    return status;
}

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
    if (!status) status = fixtureReader(readerPath, pair, reader);
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

static OSStatus fixtureCallerWithDescription(const char *description, SecTrustedApplicationRef *reader)
{
    SecCodeRef host = NULL;
    SecStaticCodeRef code = NULL;
    SecRequirementRef requirement = NULL;
    OSStatus status = SecCodeCopySelf(0, &host);
    if (!status) status = SecCodeCopyStaticCode(host, 0, &code);
    if (!status) status = SecCodeCopyDesignatedRequirement(code, 0, &requirement);
    if (!status) status = fixtureReader(description, requirement, reader);
    if (requirement) CFRelease(requirement);
    if (code) CFRelease(code);
    if (host) CFRelease(host);
    return status;
}

enum { NamedReaders, IncludeCaller, ReaderPair, CallerWithDescription };

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
    bool customSubject = readerMode == ReaderPair || readerMode == CallerWithDescription;
    if (customSubject && !first) { status = errSecParam; goto done; }
    int count = customSubject ? 1 : 3;
    for (int i = readerMode == NamedReaders ? 1 : 0; i < count; ++i) {
        if (i != 0 && !paths[i]) continue;
        SecTrustedApplicationRef reader = NULL;
        if (readerMode == ReaderPair) status = fixtureReaderPair(first, &reader);
        else if (readerMode == CallerWithDescription) status = fixtureCallerWithDescription(first, &reader);
        else status = SecTrustedApplicationCreateFromPath(paths[i], &reader);
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

// Observe the persisted informational bytes without opening the path they describe.
int TimesFixtureHasReaderDescription(SecKeychainItemRef item, const char *expected)
{
    SecAccessRef access = NULL;
    CFArrayRef grants = NULL, readers = NULL;
    CFDataRef data = NULL;
    CFStringRef description = NULL;
    SecKeychainPromptSelector prompt = 0;
    int matches = 0;
    if (!item || !expected || SecKeychainItemCopyAccess(item, &access) || !access) goto done;
    grants = SecAccessCopyMatchingACLList(access, kSecACLAuthorizationDecrypt);
    if (!grants || CFArrayGetCount(grants) != 1 ||
        SecACLCopyContents((SecACLRef)CFArrayGetValueAtIndex(grants, 0), &readers, &description, &prompt) ||
        !readers || CFArrayGetCount(readers) != 1) goto done;
    SecTrustedApplicationRef reader = (SecTrustedApplicationRef)CFArrayGetValueAtIndex(readers, 0);
    if (SecTrustedApplicationCopyData(reader, &data) || !data) goto done;
    size_t length = strlen(expected) + 1;
    matches = CFDataGetLength(data) == (CFIndex)length && memcmp(CFDataGetBytePtr(data), expected, length) == 0;
done:
    if (data) CFRelease(data);
    if (description) CFRelease(description);
    if (readers) CFRelease(readers);
    if (grants) CFRelease(grants);
    if (access) CFRelease(access);
    return matches;
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
