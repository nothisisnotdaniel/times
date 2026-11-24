// File Keychain generic passwords are encrypted with a per-item securityd group.
// Keep that group and its complete ACL. The normal password setter creates a new
// group with the calling process's partition. See Apple's securestorage.cpp,
// SSDbUniqueRecordImpl::modify and SSGroupImpl::encodeDataBlob.
#define __STDC_WANT_LIB_EXT1__ 1
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

static CSSM_RETURN findGroup(CSSM_DL_DB_HANDLE db, CSSM_DATA label, CSSM_DATA *reference)
{
    CSSM_SELECTION_PREDICATE predicate = {
        .DbOperator = CSSM_DB_EQUAL,
        .Attribute = {
            .Info = { .AttributeNameFormat = CSSM_DB_ATTRIBUTE_NAME_AS_STRING,
                      .Label.AttributeName = "Label", .AttributeFormat = CSSM_DB_ATTRIBUTE_FORMAT_BLOB },
            .NumberOfValues = 1, .Value = &label
        }
    };
    CSSM_QUERY query = {
        .RecordType = CSSM_DL_DB_RECORD_SYMMETRIC_KEY, .Conjunctive = CSSM_DB_AND,
        .NumSelectionPredicates = 1, .SelectionPredicate = &predicate
    };
    CSSM_HANDLE cursor = CSSM_INVALID_HANDLE;
    CSSM_DB_UNIQUE_RECORD_PTR record = NULL;
    CSSM_RETURN status = CSSM_DL_DataGetFirst(db, &query, &cursor, NULL, reference, &record);
    if (record) {
        CSSM_DL_FreeUniqueRecord(db, record);
    }
    if (cursor != CSSM_INVALID_HANDLE) {
        CSSM_DL_DataAbortQuery(db, cursor);
    }
    return status;
}

// securityd must authorize ENCRYPT on the original reference key. Its key bytes
// never leave the provider. Only the ciphertext and IV change in the database.
static OSStatus encryptForGroup(CSSM_CSP_HANDLE csp, const CSSM_KEY *key, CSSM_DATA input,
                                CSSM_DATA label, CSSM_DATA iv, CSSM_DATA *blob, const char **stage)
{
    *stage = "group key header";
    if (key->KeyHeader.BlobType != CSSM_KEYBLOB_REFERENCE || key->KeyHeader.KeyClass != CSSM_KEYCLASS_SESSION_KEY) {
        return errSecInvalidItemRef;
    }
    *stage = "CSP allocator";
    CSSM_API_MEMORY_FUNCS memory;
    CSSM_RETURN status = CSSM_GetAPIMemoryFunctions(csp, &memory);
    if (status) {
        return status;
    }
    CSSM_CC_HANDLE context = CSSM_INVALID_HANDLE;
    // The CSSM client supplies an empty credential object even for an operation
    // with no samples. securityd still decides ENCRYPT and partition membership.
    CSSM_ACCESS_CREDENTIALS credentials = {0};
    *stage = "create encryption context";
    status = CSSM_CSP_CreateSymmetricContext(csp, key->KeyHeader.AlgorithmId,
        CSSM_ALGMODE_CBCPadIV8, &credentials, key, &iv, CSSM_PADDING_PKCS1, NULL, &context);
    if (status) {
        return status;
    }
    CSSM_DATA first = {0}, last = {0};
    CSSM_SIZE written = 0;
    *stage = "initialize encryption";
    status = CSSM_EncryptDataInit(context);
    if (!status) {
        *stage = "encrypt update";
        status = CSSM_EncryptDataUpdate(context, &input, 1, &first, 1, &written);
    }
    if (!status) {
        *stage = "encrypt final";
        status = CSSM_EncryptDataFinal(context, &last);
    }
    if (!status) {
        *stage = "ciphertext length";
        if (written > first.Length || written > MaxCipherSize || last.Length > MaxCipherSize - written ||
            (written && !first.Data) || (last.Length && !last.Data) || !(written + last.Length)) {
            status = errSecInternalComponent;
        } else {
            // The provider owns the framing and reports how much each phase produced.
            *stage = "allocate protected record";
            blob->Length = PrefixSize + written + last.Length;
            blob->Data = malloc(blob->Length);
            if (!blob->Data) {
                status = errSecAllocate;
            } else {
                memcpy(blob->Data, label.Data, GroupLabelSize);
                memcpy(blob->Data + GroupLabelSize, iv.Data, IvSize);
                if (written) memcpy(blob->Data + PrefixSize, first.Data, written);
                if (last.Length) memcpy(blob->Data + PrefixSize + written, last.Data, last.Length);
            }
        }
    }
    if (first.Data) {
        memory.free_func(first.Data, memory.AllocRef);
    }
    if (last.Data) {
        memory.free_func(last.Data, memory.AllocRef);
    }
    CSSM_DeleteContext(context);
    return status;
}

OSStatus TimesUpdateProtectedPassword(SecKeychainRef owner, SecKeychainItemRef item, uint8_t *der, uint32_t length,
                                      const char **stage)
{
    CSSM_DL_DB_HANDLE db;
    CSSM_CSP_HANDLE csp;
    CSSM_API_MEMORY_FUNCS memory;
    const CSSM_DB_UNIQUE_RECORD *record;
    SecKeychainStatus permissions;
    *stage = "migration arguments";
    if (!owner || !item || !der || !length || length > 16384) {
        return errSecParam;
    }
    *stage = "database write permission";
    OSStatus status = SecKeychainGetStatus(owner, &permissions);
    if (status || !(permissions & kSecWritePermStatus)) {
        return status ? status : errSecAuthFailed;
    }
    *stage = "item database";
    if ((status = SecKeychainItemGetDLDBHandle(item, &db))) return status;
    *stage = "database CSP";
    if ((status = SecKeychainGetCSPHandle(owner, &csp))) return status;
    *stage = "item record";
    if ((status = SecKeychainItemGetUniqueRecordID(item, &record))) return status;
    *stage = "database allocator";
    if ((status = CSSM_GetAPIMemoryFunctions(db.DLHandle, &memory))) return status;
    CSSM_DB_RECORD_ATTRIBUTE_DATA attrs = {0};
    CSSM_DATA original = {0}, reference = {0};
    CSSM_DATA replacement = {0};
    *stage = "read protected record";
    status = CSSM_DL_DataGetFromUniqueRecordId(db, record, &attrs, &original);
    if (status) {
        goto cleanup;
    }
    *stage = "validate record envelope";
    if (attrs.DataRecordType != CSSM_DL_DB_RECORD_GENERIC_PASSWORD || !original.Data ||
        original.Length < PrefixSize + IvSize || original.Length > PrefixSize + MaxCipherSize ||
        memcmp(original.Data, "ssgp", 4) != 0) {
        status = errSecInvalidItemRef;
        goto cleanup;
    }
    CSSM_DATA label = { GroupLabelSize, original.Data };
    *stage = "find record group";
    status = findGroup(db, label, &reference);
    if (status) {
        goto cleanup;
    }
    *stage = "validate group reference";
    if (reference.Length != sizeof(CSSM_KEY) || !reference.Data) {
        status = errSecInvalidItemRef;
        goto cleanup;
    }
    uint8_t ivBytes[IvSize];
    CSSM_DATA iv = { sizeof(ivBytes), ivBytes };
    *stage = "generate IV";
    status = SecRandomCopyBytes(kSecRandomDefault, iv.Length, iv.Data);
    if (!status) {
        CSSM_DATA input = { length, der };
        status = encryptForGroup(csp, (const CSSM_KEY *)reference.Data, input, label, iv, &replacement, stage);
    }
    if (!status) {
        *stage = "update record data";
        status = CSSM_DL_DataModify(db, attrs.DataRecordType, (CSSM_DB_UNIQUE_RECORD_PTR)record,
                                    NULL, &replacement, CSSM_DB_MODIFY_ATTRIBUTE_NONE);
    }
cleanup:
    if (reference.Data) {
        if (reference.Length == sizeof(CSSM_KEY)) {
            CSSM_FreeKey(csp, NULL, (CSSM_KEY *)reference.Data, CSSM_FALSE);
        }
        memory.free_func(reference.Data, memory.AllocRef);
    }
    if (original.Data) {
        memset_s(original.Data, original.Length, 0, original.Length);
        memory.free_func(original.Data, memory.AllocRef);
    }
    free(replacement.Data);
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
