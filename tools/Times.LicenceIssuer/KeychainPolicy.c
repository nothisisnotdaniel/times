// Read-only comparisons of the owner and ACL returned by Keychain Services.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <Security/cssmapple.h>
#include <CommonCrypto/CommonDigest.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

static void appendNumber(CFMutableDataRef bytes, uint32_t value)
{
    CFDataAppendBytes(bytes, (const UInt8 *)&value, sizeof(value));
}

static bool appendField(CFMutableDataRef bytes, const void *value, size_t length)
{
    if (length > UINT32_MAX || (length && !value)) return false;
    appendNumber(bytes, (uint32_t)length);
    if (length) CFDataAppendBytes(bytes, value, (CFIndex)length);
    return true;
}

// No pointer values, struct padding or provider-local ACL handles enter equality.
// Preserve the structure of compound subjects and the binding of each prompt to its rights.
static bool appendSubject(CFMutableDataRef bytes, const CSSM_LIST *list, unsigned depth,
                          CFTypeRef metadata, CFMutableBagRef prompts)
{
    if (depth > 32 || !list->Head) return false;
    CFIndex start = CFDataGetLength(bytes);
    appendNumber(bytes, list->ListType);
    for (const CSSM_LIST_ELEMENT *e = list->Head; e; e = e->NextElement) {
        appendNumber(bytes, e->ElementType);
        switch (e->ElementType) {
        case CSSM_LIST_ELEMENT_WORDID:
            appendNumber(bytes, e->WordID);
            break;
        case CSSM_LIST_ELEMENT_DATUM:
            if (!appendField(bytes, e->Element.Word.Data, e->Element.Word.Length)) return false;
            break;
        case CSSM_LIST_ELEMENT_SUBLIST:
            if (!appendSubject(bytes, &e->Element.Sublist, depth + 1, metadata, prompts)) return false;
            break;
        default:
            return false;
        }
    }
    appendNumber(bytes, UINT32_MAX); // end of this list
    if (list->Head->ElementType == CSSM_LIST_ELEMENT_WORDID &&
        list->Head->WordID == CSSM_ACL_SUBJECT_TYPE_KEYCHAIN_PROMPT) {
        CFDataRef subject = CFDataCreate(NULL, CFDataGetBytePtr(bytes) + start, CFDataGetLength(bytes) - start);
        if (!subject) return false;
        const void *values[] = {metadata, subject};
        CFArrayRef prompt = CFArrayCreate(NULL, values, 2, &kCFTypeArrayCallBacks);
        CFRelease(subject);
        if (!prompt) return false;
        CFBagAddValue(prompts, prompt);
        CFRelease(prompt);
    }
    return true;
}

// SecAccessGetOwnerAndACL returns separately allocated list nodes and values.
static void freeSubject(CSSM_LIST *list)
{
    CSSM_LIST_ELEMENT *e = list->Head;
    while (e) {
        CSSM_LIST_ELEMENT *next = e->NextElement;
        if (e->ElementType == CSSM_LIST_ELEMENT_SUBLIST) freeSubject(&e->Element.Sublist);
        if (e->ElementType == CSSM_LIST_ELEMENT_DATUM) free(e->Element.Word.Data);
        free(e);
        e = next;
    }
}

static int compareRights(const void *left, const void *right)
{
    CSSM_ACL_AUTHORIZATION_TAG a = *(const CSSM_ACL_AUTHORIZATION_TAG *)left;
    CSSM_ACL_AUTHORIZATION_TAG b = *(const CSSM_ACL_AUTHORIZATION_TAG *)right;
    return (a > b) - (a < b);
}

static bool appendMetadata(CFMutableDataRef bytes, CSSM_ACL_ENTRY_PROTOTYPE *p)
{
    size_t tagLength = strnlen(p->EntryTag, sizeof(p->EntryTag));
    if (p->Authorization.NumberOfAuthTags > 0 && !p->Authorization.AuthTags) return false;
    // This is our copy from SecAccessGetOwnerAndACL. Rights form a set within this entry.
    if (p->Authorization.NumberOfAuthTags > 1)
        qsort(p->Authorization.AuthTags, p->Authorization.NumberOfAuthTags, sizeof(*p->Authorization.AuthTags), compareRights);
    appendNumber(bytes, p->Delegate);
    return tagLength < sizeof(p->EntryTag) && appendField(bytes, p->EntryTag, tagLength) &&
        appendField(bytes, p->Authorization.AuthTags, (size_t)p->Authorization.NumberOfAuthTags * sizeof(*p->Authorization.AuthTags)) &&
        appendField(bytes, p->TimeRange.StartDate.Data, p->TimeRange.StartDate.Length) &&
        appendField(bytes, p->TimeRange.EndDate.Data, p->TimeRange.EndDate.Length);
}

static CFTypeRef copyPartitionSet(const CSSM_DATA *payload)
{
    CFDataRef bytes = CFDataCreate(NULL, payload->Data, (CFIndex)payload->Length);
    if (!bytes) return NULL;
    CFPropertyListRef plist = CFPropertyListCreateWithData(NULL, bytes, kCFPropertyListImmutable, NULL, NULL);
    CFRelease(bytes);
    CFMutableBagRef ids = NULL;
    if (plist && CFGetTypeID(plist) == CFDictionaryGetTypeID() && CFDictionaryGetCount(plist) == 1) {
        CFArrayRef list = CFDictionaryGetValue(plist, CFSTR("Partitions"));
        if (list && CFGetTypeID(list) == CFArrayGetTypeID() && CFArrayGetCount(list) > 0) {
            ids = CFBagCreateMutable(NULL, 0, &kCFTypeBagCallBacks);
            for (CFIndex i = 0; ids && i < CFArrayGetCount(list); ++i) {
                CFTypeRef id = CFArrayGetValueAtIndex(list, i);
                if (CFGetTypeID(id) != CFStringGetTypeID() || !CFStringGetLength(id)) {
                    CFRelease(ids);
                    ids = NULL;
                    break;
                }
                CFBagAddValue(ids, id);
            }
        }
    }
    if (plist) CFRelease(plist);
    return ids;
}

enum { Ordinary, Partitions, Integrity, Rights, Prompts, BagCount };
static OSStatus collectEntry(CSSM_ACL_ENTRY_PROTOTYPE *p, CFMutableBagRef bags[], CFDataRef *digest)
{
    CFMutableDataRef metadata = CFDataCreateMutable(NULL, 0);
    CFMutableDataRef subject = CFDataCreateMutable(NULL, 0);
    CFTypeRef partition = NULL;
    OSStatus status = errSecAllocate;
    if (!metadata || !subject) goto done;
    status = errSecInvalidItemRef;
    if (!appendMetadata(metadata, p)) goto done;
    CFBagAddValue(bags[Rights], metadata);
    int group = Ordinary;
    const CSSM_LIST_ELEMENT *head = p->TypedSubject.Head;
    for (uint32_t i = 0; i < p->Authorization.NumberOfAuthTags; ++i) {
        CSSM_ACL_AUTHORIZATION_TAG right = p->Authorization.AuthTags[i];
        if (right != CSSM_ACL_AUTHORIZATION_PARTITION_ID && right != CSSM_ACL_AUTHORIZATION_INTEGRITY) continue;
        if (p->Authorization.NumberOfAuthTags != 1 || !head || head->ElementType != CSSM_LIST_ELEMENT_WORDID ||
            head->WordID != CSSM_ACL_SUBJECT_TYPE_PARTITION || !head->NextElement ||
            head->NextElement->ElementType != CSSM_LIST_ELEMENT_DATUM || head->NextElement->NextElement) goto done;
        const CSSM_DATA *payload = &head->NextElement->Element.Word;
        if (!payload->Data || !payload->Length) goto done;
        appendNumber(subject, p->TypedSubject.ListType);
        appendNumber(subject, head->WordID);
        if (right == CSSM_ACL_AUTHORIZATION_PARTITION_ID) {
            group = Partitions;
            partition = copyPartitionSet(payload);
            if (!partition) goto done;
        } else {
            group = Integrity;
            if (*digest || payload->Length != CC_SHA256_DIGEST_LENGTH) goto done;
            *digest = CFDataCreate(NULL, payload->Data, payload->Length);
            if (!*digest) goto done;
        }
    }
    if (group == Ordinary && !appendSubject(subject, &p->TypedSubject, 0, metadata, bags[Prompts])) goto done;
    const void *fields[] = {metadata, subject, partition ? partition : kCFNull};
    CFArrayRef entry = CFArrayCreate(NULL, fields, 3, &kCFTypeArrayCallBacks);
    if (!entry) { status = errSecAllocate; goto done; }
    CFBagAddValue(bags[group], entry);
    CFRelease(entry);
    status = errSecSuccess;
done:
    if (partition) CFRelease(partition);
    if (subject) CFRelease(subject);
    if (metadata) CFRelease(metadata);
    return status;
}

// Layout: owner, ordinary entries, partitions, integrity rule, rights, prompts, integrity digest.
OSStatus TimesCopyAccessPolicy(SecAccessRef access, CFArrayRef *result)
{
    *result = NULL;
    CSSM_ACL_OWNER_PROTOTYPE *owner = NULL;
    CSSM_ACL_ENTRY_INFO *entries = NULL;
    uint32_t count = 0;
    OSStatus status = SecAccessGetOwnerAndACL(access, &owner, &count, &entries);
    CFMutableDataRef ownerPolicy = NULL;
    CFMutableBagRef bags[BagCount] = {0};
    CFDataRef digest = NULL;
    if (status) goto done;
    if (!owner || !entries || !count) { status = errSecInvalidItemRef; goto done; }
    status = errSecAllocate;
    for (int i = 0; i < BagCount; ++i) {
        bags[i] = CFBagCreateMutable(NULL, 0, &kCFTypeBagCallBacks);
        if (!bags[i]) goto done;
    }
    ownerPolicy = CFDataCreateMutable(NULL, 0);
    if (!ownerPolicy) goto done;
    appendNumber(ownerPolicy, owner->Delegate);
    if (!appendSubject(ownerPolicy, &owner->TypedSubject, 0, CFSTR("owner"), bags[Prompts])) {
        status = errSecInvalidItemRef;
        goto done;
    }
    for (uint32_t i = 0; i < count; ++i) {
        status = collectEntry(&entries[i].EntryPublicInfo, bags, &digest);
        if (status) goto done;
    }
    const void *fields[] = {ownerPolicy, bags[Ordinary], bags[Partitions], bags[Integrity], bags[Rights],
                            bags[Prompts], digest ? (CFTypeRef)digest : kCFNull};
    *result = CFArrayCreate(NULL, fields, sizeof(fields) / sizeof(fields[0]), &kCFTypeArrayCallBacks);
    status = *result ? errSecSuccess : errSecAllocate;
done:
    if (ownerPolicy) CFRelease(ownerPolicy);
    for (int i = 0; i < BagCount; ++i) if (bags[i]) CFRelease(bags[i]);
    if (digest) CFRelease(digest);
    if (owner) { freeSubject(&owner->TypedSubject); free(owner); }
    for (uint32_t i = 0; entries && i < count; ++i) {
        CSSM_ACL_ENTRY_PROTOTYPE *p = &entries[i].EntryPublicInfo;
        freeSubject(&p->TypedSubject);
        free(p->Authorization.AuthTags);
        free(p->TimeRange.StartDate.Data);
        free(p->TimeRange.EndDate.Data);
    }
    free(entries);
    return status;
}

OSStatus TimesCopyPasswordAccess(SecKeychainItemRef item, CFArrayRef *result)
{
    *result = NULL;
    SecAccessRef access = NULL;
    OSStatus status = SecKeychainItemCopyAccess(item, &access);
    if (!status) status = access ? TimesCopyAccessPolicy(access, result) : errSecInvalidItemRef;
    if (access) CFRelease(access);
    return status;
}

// The normal password read authenticates the item's integrity under the caller's
// existing rights. The issuer disables interaction for the entire transaction.
static OSStatus checkPassword(SecKeychainItemRef item, const uint8_t *expected, size_t length)
{
    UInt32 size = 0;
    void *bytes = NULL;
    OSStatus status = SecKeychainItemCopyContent(item, NULL, NULL, &size, &bytes);
    if (!status && (!bytes || size != length || timingsafe_bcmp(bytes, expected, length))) status = errSecInvalidItemRef;
    if (bytes) {
        memset_s(bytes, size, 0, size);
        SecKeychainItemFreeContent(NULL, bytes);
    }
    return status;
}

// The legacy trusted-application API exposes the description separately from
// the stored code requirement. Older frameworks without this accessor cannot
// establish the subject of a partitioned grant.
extern OSStatus SecTrustedApplicationCopyRequirement(SecTrustedApplicationRef, SecRequirementRef *)
    __attribute__((weak_import));

static OSStatus checkApplicationSubject(SecTrustedApplicationRef reader, bool requireCaller)
{
    SecRequirementRef stored = NULL;
    SecCodeRef caller = NULL;
    SecStaticCodeRef code = NULL;
    CFDataRef path = NULL, actual = NULL, expected = NULL;
    CFURLRef url = NULL;
    CFDictionaryRef info = NULL;
    OSStatus status = errSecAuthFailed;
    if (!SecTrustedApplicationCopyRequirement || SecTrustedApplicationCopyRequirement(reader, &stored) || !stored)
        goto done;
    if (requireCaller) {
        if (SecCodeCopySelf(kSecCSDefaultFlags, &caller) ||
            SecCodeCopyStaticCode(caller, kSecCSDefaultFlags, &code)) goto done;
    } else {
        // An already authorized read may belong to a different process. Use the
        // recorded location only to obtain an independent identity for comparison.
        if (SecTrustedApplicationCopyData(reader, &path) || !path) goto done;
        CFIndex length = CFDataGetLength(path);
        const UInt8 *bytes = CFDataGetBytePtr(path);
        if (length < 2 || length > PATH_MAX || !bytes || bytes[0] != '/' ||
            bytes[length - 1] != 0 || memchr(bytes, 0, (size_t)length - 1)) goto done;
        url = CFURLCreateFromFileSystemRepresentation(NULL, bytes, length - 1, false);
        if (!url || SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &code)) goto done;
    }
    // Compare the entire requirement to the OS-derived identity. An executable
    // can supply a custom designated requirement, including a group requirement.
    if (!code || SecCodeCopySigningInformation(code, kSecCSRequirementInformation, &info) || !info) goto done;
    SecRequirementRef implicit = (SecRequirementRef)CFDictionaryGetValue(info, kSecCodeInfoImplicitDesignatedRequirement);
    if (!implicit || SecRequirementCopyData(implicit, kSecCSDefaultFlags, &expected) ||
        SecRequirementCopyData(stored, kSecCSDefaultFlags, &actual)) goto done;
    if (expected && actual && CFEqual(expected, actual)) status = errSecSuccess;
done:
    if (actual) CFRelease(actual);
    if (expected) CFRelease(expected);
    if (info) CFRelease(info);
    if (url) CFRelease(url);
    if (path) CFRelease(path);
    if (code) CFRelease(code);
    if (caller) CFRelease(caller);
    if (stored) CFRelease(stored);
    return status;
}

static OSStatus checkReaderPolicy(SecAccessRef access, CFArrayRef policy, bool writable)
{
    if (!access || !policy || CFArrayGetCount(policy) != 7) return errSecInvalidItemRef;
    CFBagRef partitions = CFArrayGetValueAtIndex(policy, 2);
    CFBagRef integrity = CFArrayGetValueAtIndex(policy, 3);
    // The commissioned refusal applies to partitioned items. A read-only
    // issuer may consume an authorized read of an older unpartitioned item.
    if (!writable && CFBagGetCount(partitions) == 0) return errSecSuccess;
    if (CFBagGetCount(partitions) != 1 || CFBagGetCount(integrity) != 1) return errSecAuthFailed;
    const void *entry = NULL;
    CFBagGetValues(partitions, &entry);
    CFBagRef membership = CFArrayGetValueAtIndex((CFArrayRef)entry, 2);
    if (CFBagGetCount(membership) != 1) return errSecAuthFailed;

    CFArrayRef grants = NULL, readers = NULL;
    CFStringRef description = NULL;
    SecKeychainPromptSelector prompt = 0;
    OSStatus status = errSecAuthFailed;
    grants = SecAccessCopyMatchingACLList(access, kSecACLAuthorizationDecrypt);
    if (!grants || CFArrayGetCount(grants) != 1) goto done;
    status = SecACLCopyContents((SecACLRef)CFArrayGetValueAtIndex(grants, 0), &readers, &description, &prompt);
    if (status) goto done;
    status = errSecAuthFailed;
    if (!readers || CFArrayGetCount(readers) != 1) goto done;
    SecTrustedApplicationRef reader = (SecTrustedApplicationRef)CFArrayGetValueAtIndex(readers, 0);
    if (CFGetTypeID(reader) != SecTrustedApplicationGetTypeID()) goto done;
    status = checkApplicationSubject(reader, writable);
done:
    if (description) CFRelease(description);
    if (readers) CFRelease(readers);
    if (grants) CFRelease(grants);
    return status;
}

// Inspect the same SecAccess object for both membership and the complete original policy.
OSStatus TimesCopyLegacyReadPolicy(SecKeychainItemRef item, int writable, CFArrayRef *result)
{
    *result = NULL;
    SecAccessRef access = NULL;
    CFArrayRef policy = NULL;
    OSStatus status = item ? SecKeychainItemCopyAccess(item, &access) : errSecParam;
    if (!status) status = access ? TimesCopyAccessPolicy(access, &policy) : errSecInvalidItemRef;
    if (!status) status = checkReaderPolicy(access, policy, writable != 0);
    if (!status) *result = policy;
    else if (policy) CFRelease(policy);
    if (access) CFRelease(access);
    return status;
}

OSStatus TimesCheckMigrationReader(SecKeychainItemRef item, CFArrayRef policy, const uint8_t *expected, size_t length)
{
    CFArrayRef current = NULL;
    OSStatus status = TimesCopyLegacyReadPolicy(item, 1, &current);
    if (!status && (!policy || !current || !CFEqual(current, policy))) status = errSecInvalidItemRef;
    if (current) CFRelease(current);
    // Authenticate the existing value under the writer's own code identity and partition.
    return status ? status : checkPassword(item, expected, length);
}

OSStatus TimesCheckPasswordPolicy(CFArrayRef before, CFArrayRef after, SecKeychainItemRef item,
                                  const uint8_t *expected, size_t length)
{
    if (!before || !after || CFArrayGetCount(before) != 7 || CFArrayGetCount(after) != 7) return errSecInvalidItemRef;
    for (int i = 0; i < 6; ++i) {
        if (!CFEqual(CFArrayGetValueAtIndex(before, i), CFArrayGetValueAtIndex(after, i))) return errSecInvalidItemRef;
    }
    if (CFArrayGetValueAtIndex(after, 6) == kCFNull) return errSecInvalidItemRef;
    return checkPassword(item, expected, length);
}
