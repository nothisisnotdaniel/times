// Helpers for a read-only stamp of a file Keychain item and its access policy.
#define __STDC_WANT_LIB_EXT1__ 1
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <Security/cssm.h>
#include <Security/cssmapple.h>
#include <CommonCrypto/CommonDigest.h>
#include <stdlib.h>
#include <string.h>

enum { GroupLabelSize = 20, IvSize = 8, PrefixSize = GroupLabelSize + IvSize, MaxCipherSize = 16384 + IvSize };

// Compare the protected value across the read and the subsequent writer transaction.
// A fresh IV means that a re-encryption also changes the stamp.
OSStatus TimesPasswordStamp(SecKeychainItemRef item, uint8_t digest[CC_SHA256_DIGEST_LENGTH], const char **stage)
{
    CSSM_DL_DB_HANDLE db;
    CSSM_API_MEMORY_FUNCS memory;
    const CSSM_DB_UNIQUE_RECORD *record;
    *stage = "stamp database";
    if (!item) return errSecParam;
    OSStatus status = SecKeychainItemGetDLDBHandle(item, &db);
    if (status) return status;
    *stage = "stamp record";
    if ((status = SecKeychainItemGetUniqueRecordID(item, &record))) return status;
    *stage = "stamp allocator";
    if ((status = CSSM_GetAPIMemoryFunctions(db.DLHandle, &memory))) return status;
    CSSM_DATA blob = {0};
    CSSM_DB_RECORD_ATTRIBUTE_DATA attrs = {0};
    *stage = "read stamp data";
    status = CSSM_DL_DataGetFromUniqueRecordId(db, record, &attrs, &blob);
    if (!status) {
        *stage = "validate stamp envelope";
        if (attrs.DataRecordType != CSSM_DL_DB_RECORD_GENERIC_PASSWORD || !blob.Data || blob.Length < PrefixSize + IvSize ||
            blob.Length > PrefixSize + MaxCipherSize || memcmp(blob.Data, "ssgp", 4) != 0) {
            status = errSecInvalidItemRef;
        } else {
            CC_SHA256(blob.Data, (CC_LONG)blob.Length, digest);
        }
    }
    if (blob.Data) {
        memset_s(blob.Data, blob.Length, 0, blob.Length);
        memory.free_func(blob.Data, memory.AllocRef);
    }
    return status;
}

// Compare the provider's ACL entries by value. A trusted application's external
// representation includes its code requirement as well as its path.
extern OSStatus SecTrustedApplicationCopyExternalRepresentation(SecTrustedApplicationRef, CFDataRef *);

static OSStatus copyAccessEntry(SecACLRef acl, CFArrayRef *result)
{
    CFArrayRef apps = NULL;
    CFStringRef description = NULL;
    CSSM_ACL_KEYCHAIN_PROMPT_SELECTOR prompt = {0};
    OSStatus status = SecACLCopySimpleContents(acl, &apps, &description, &prompt);
    CFMutableArrayRef readers = NULL;
    CSSM_ACL_AUTHORIZATION_TAG *tags = NULL;
    CFDataRef rights = NULL, selector = NULL;
    if (status) goto done;

    // Keep numeric authorizations, including vendor-specific rights. The string
    // API maps unfamiliar authorizations to "any".
    uint32_t count = 0;
    CSSM_ACL_AUTHORIZATION_TAG unused = 0;
    status = SecACLGetAuthorizations(acl, &unused, &count);
    if (count && status == errSecParam) {
        tags = calloc(count, sizeof(*tags));
        if (!tags) { status = errSecAllocate; goto done; }
        status = SecACLGetAuthorizations(acl, tags, &count);
    }
    if (status) goto done;
    rights = CFDataCreate(NULL, (const UInt8 *)(tags ? tags : &unused), (CFIndex)count * sizeof(*tags));
    selector = CFDataCreate(NULL, (const UInt8 *)&prompt, sizeof(prompt));
    if (!rights || !selector) { status = errSecAllocate; goto done; }

    if (apps) {
        readers = CFArrayCreateMutable(NULL, 0, &kCFTypeArrayCallBacks);
        if (!readers) { status = errSecAllocate; goto done; }
        for (CFIndex i = 0; i < CFArrayGetCount(apps); i++) {
            CFDataRef application = NULL;
            status = SecTrustedApplicationCopyExternalRepresentation(
                (SecTrustedApplicationRef)CFArrayGetValueAtIndex(apps, i), &application);
            if (!status && !application) status = errSecInvalidItemRef;
            if (status) {
                if (application) CFRelease(application);
                goto done;
            }
            CFArrayAppendValue(readers, application);
            CFRelease(application);
        }
    }
    const void *fields[] = { rights, selector, description ? (CFTypeRef)description : kCFNull,
                            readers ? (CFTypeRef)readers : kCFNull };
    *result = CFArrayCreate(NULL, fields, 4, &kCFTypeArrayCallBacks);
    if (!*result) status = errSecAllocate;
done:
    free(tags);
    if (rights) CFRelease(rights);
    if (selector) CFRelease(selector);
    if (readers) CFRelease(readers);
    if (description) CFRelease(description);
    if (apps) CFRelease(apps);
    return status;
}

// SecAccessCopyACLList includes the owner entry, partition and integrity rules.
// Keep duplicate entries, ignoring only enumeration order. A policy the provider
// cannot describe causes the surrounding file transaction to roll back.
OSStatus TimesCopyPasswordAccess(SecKeychainItemRef item, CFBagRef *result)
{
    *result = NULL;
    SecAccessRef access = NULL;
    CFArrayRef entries = NULL;
    CFMutableBagRef policy = NULL;
    OSStatus status = SecKeychainItemCopyAccess(item, &access);
    if (!status && !access) status = errSecInvalidItemRef;
    if (!status) status = SecAccessCopyACLList(access, &entries);
    if (!status && (!entries || !CFArrayGetCount(entries))) status = errSecInvalidItemRef;
    if (status) goto done;
    policy = CFBagCreateMutable(NULL, 0, &kCFTypeBagCallBacks);
    if (!policy) { status = errSecAllocate; goto done; }
    for (CFIndex i = 0; i < CFArrayGetCount(entries); i++) {
        CFArrayRef entry = NULL;
        status = copyAccessEntry((SecACLRef)CFArrayGetValueAtIndex(entries, i), &entry);
        if (status) goto done;
        CFBagAddValue(policy, entry);
        CFRelease(entry);
    }
    *result = policy;
    policy = NULL;
done:
    if (policy) CFRelease(policy);
    if (entries) CFRelease(entries);
    if (access) CFRelease(access);
    return status;
}

// RFC 5915 makes the public point optional. Import a floating SEC1 key (no
// destination Keychain). Apple's X9.63 representation uses a zeroizing CFData
// allocator; only the public part is copied back to the managed caller.
OSStatus TimesCopyPublicPoint(const uint8_t scalar[32], uint8_t point[65])
{
    uint8_t sec1[51] = { 0x30, 0x31, 0x02, 0x01, 0x01, 0x04, 0x20 };
    const uint8_t curve[] = { 0xa0, 0x0a, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07 };
    memcpy(sec1 + 7, scalar, 32);
    memcpy(sec1 + 39, curve, sizeof(curve));
    CFDataRef encoded = CFDataCreateWithBytesNoCopy(NULL, sec1, sizeof(sec1), kCFAllocatorNull);
    const void *extractable = kSecAttrIsExtractable;
    CFArrayRef attrs = CFArrayCreate(NULL, &extractable, 1, &kCFTypeArrayCallBacks);
    SecItemImportExportKeyParameters parameters = { .version = SEC_KEY_IMPORT_EXPORT_PARAMS_VERSION, .keyAttributes = attrs };
    SecExternalFormat format = kSecFormatOpenSSL;
    SecExternalItemType type = kSecItemTypePrivateKey;
    CFArrayRef items = NULL;
    OSStatus status = encoded && attrs ? SecItemImport(encoded, NULL, &format, &type, 0, &parameters, NULL, &items)
                                      : errSecAllocate;
    if (!status) {
        status = errSecDecode;
        if (items && CFArrayGetCount(items) == 1) {
            SecKeyRef key = (SecKeyRef)CFArrayGetValueAtIndex(items, 0);
            CFDataRef representation = SecKeyCopyExternalRepresentation(key, NULL);
            if (representation) {
                if (CFDataGetLength(representation) == 97 && CFDataGetBytePtr(representation)[0] == 4) {
                    memcpy(point, CFDataGetBytePtr(representation), 65);
                    status = errSecSuccess;
                }
                CFRelease(representation);
            }
        }
    }
    if (items) {
        CFRelease(items);
    }
    if (attrs) {
        CFRelease(attrs);
    }
    if (encoded) {
        CFRelease(encoded);
    }
    memset_s(sec1, sizeof(sec1), 0, sizeof(sec1));
    return status;
}
