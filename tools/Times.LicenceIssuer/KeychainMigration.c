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

// Both callers keep the owning database explicit. Copy the opaque identifier
// while the public CSSM bridge's borrowed record is still valid.
OSStatus TimesCopyRecordId(SecKeychainItemRef item, CFDataRef *result)
{
    *result = NULL;
    const CSSM_DB_UNIQUE_RECORD *record = NULL;
    OSStatus status = item ? SecKeychainItemGetUniqueRecordID(item, &record) : errSecParam;
    if (status) return status;
    if (!record || !record->RecordIdentifier.Data || !record->RecordIdentifier.Length ||
        record->RecordIdentifier.Length > 1024) return errSecInvalidItemRef;
    *result = CFDataCreate(NULL, record->RecordIdentifier.Data, (CFIndex)record->RecordIdentifier.Length);
    return *result ? errSecSuccess : errSecAllocate;
}

OSStatus TimesCheckPasswordDatabase(SecKeychainRef database)
{
    SecKeychainAttributeInfo *info = NULL;
    OSStatus status = database ? SecKeychainAttributeInfoForItemID(database, CSSM_DL_DB_RECORD_GENERIC_PASSWORD, &info)
                               : errSecParam;
    bool service = false, account = false;
    if (!status && info && info->tag && info->count <= 256) {
        for (UInt32 i = 0; i < info->count; ++i) {
            service |= info->tag[i] == kSecServiceItemAttr;
            account |= info->tag[i] == kSecAccountItemAttr;
        }
    }
    if (info) SecKeychainFreeAttributeInfo(info);
    return status ? status : service && account ? errSecSuccess : errSecInvalidKeychain;
}

// Compare the protected value across the read and the subsequent writer transaction.
// A fresh IV means that a re-encryption also changes the stamp.
OSStatus TimesPasswordStamp(SecKeychainItemRef item, int expectedLength, uint8_t digest[CC_SHA256_DIGEST_LENGTH],
                            const char **stage)
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
            blob.Length > PrefixSize + MaxCipherSize || memcmp(blob.Data, "ssgp", 4) != 0 ||
            (expectedLength >= 0 && blob.Length != PrefixSize + ((size_t)expectedLength / IvSize + 1) * IvSize)) {
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

// Restrict both forms of the reference query to the selected file Keychain.
static OSStatus copyReference(SecKeychainRef owner, CFStringRef input, CFTypeRef value,
                              CFStringRef output, CFTypeRef *result)
{
    *result = NULL;
    const void *database = owner;
    CFArrayRef scope = CFArrayCreate(NULL, &database, 1, &kCFTypeArrayCallBacks);
    if (!scope) return errSecAllocate;
    const void *keys[] = {kSecClass, kSecMatchSearchList, input, output, kSecMatchLimit};
    const void *values[] = {kSecClassGenericPassword, scope, value, kCFBooleanTrue, kSecMatchLimitOne};
    CFDictionaryRef query = CFDictionaryCreate(NULL, keys, values, 5,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    OSStatus status = query ? SecItemCopyMatching(query, result) : errSecAllocate;
    if (query) CFRelease(query);
    CFRelease(scope);
    return status;
}

OSStatus TimesCopyPasswordReference(SecKeychainItemRef item, CFDataRef *result)
{
    *result = NULL;
    SecKeychainRef database = NULL;
    CFTypeRef reference = NULL;
    OSStatus status = item ? SecKeychainItemCopyKeychain(item, &database) : errSecParam;
    if (!status && !database) status = errSecInvalidItemRef;
    if (!status) status = copyReference(database, kSecValueRef, item, kSecReturnPersistentRef, &reference);
    if (!status && (!reference || CFGetTypeID(reference) != CFDataGetTypeID())) status = errSecInvalidItemRef;
    if (status && reference) CFRelease(reference);
    if (!status) *result = (CFDataRef)reference;
    if (database) CFRelease(database);
    return status;
}

// The reference comes from the selected file Keychain. Recheck its owner, class
// and primary attributes before using the resolved item for a postcondition.
OSStatus TimesResolvePassword(CFDataRef persistent, SecKeychainRef owner, const char *service,
                              const char *account, SecKeychainItemRef *result)
{
    *result = NULL;
    if (!persistent || !owner || !service || !account) return errSecParam;
    SecKeychainItemRef item = NULL;
    SecKeychainRef database = NULL;
    CFTypeRef resolved = NULL;
    OSStatus status = copyReference(owner, kSecValuePersistentRef, persistent, kSecReturnRef, &resolved);
    if (!status && (!resolved || CFGetTypeID(resolved) != SecKeychainItemGetTypeID())) status = errSecInvalidItemRef;
    if (!status) item = (SecKeychainItemRef)resolved;
    if (!status) status = SecKeychainItemCopyKeychain(item, &database);
    if (!status && (!database || !CFEqual(owner, database))) status = errSecInvalidItemRef;
    if (!status) {
        SecItemClass kind = 0;
        SecKeychainAttribute attrs[] = { {kSecServiceItemAttr, 0, NULL}, {kSecAccountItemAttr, 0, NULL} };
        SecKeychainAttributeList list = {2, attrs};
        status = SecKeychainItemCopyContent(item, &kind, &list, NULL, NULL);
        if (!status && (kind != kSecGenericPasswordItemClass || !attrs[0].data || !attrs[1].data ||
            attrs[0].length != strlen(service) || memcmp(attrs[0].data, service, attrs[0].length) ||
            attrs[1].length != strlen(account) || memcmp(attrs[1].data, account, attrs[1].length))) {
            status = errSecInvalidItemRef;
        }
        SecKeychainItemFreeContent(&list, NULL);
    }
    if (database) CFRelease(database);
    if (status && resolved) CFRelease(resolved);
    if (!status) *result = item;
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
