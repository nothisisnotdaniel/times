// Read-only comparisons of the owner and ACL returned by Keychain Services.
#define __STDC_WANT_LIB_EXT1__ 1
#define _DARWIN_C_SOURCE
#include <Security/Security.h>
#include <Security/cssmapple.h>
#include <CommonCrypto/CommonDigest.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

enum {
    MaxEntries = 128, MaxRights = 64, MaxNodes = 2048, MaxDepth = 32,
    MaxPolicyBytes = 128 * 1024, MaxPartitionBytes = 8192, MaxPartitions = 64
};

typedef struct { size_t nodes, bytes; } PolicyBudget;

static bool consume(size_t *remaining, size_t amount)
{
    if (amount > *remaining) return false;
    *remaining -= amount;
    return true;
}

// Preflight the entire typed descriptor before copying fields, parsing a
// partition plist, sorting rights or retaining a subject's code requirement.
static bool measureSubject(const CSSM_LIST *list, unsigned depth, PolicyBudget *budget)
{
    if (!list->Head || depth > MaxDepth) return false;
    for (const CSSM_LIST_ELEMENT *e = list->Head; e; e = e->NextElement) {
        if (!consume(&budget->nodes, 1) || !consume(&budget->bytes, sizeof(*e))) return false;
        switch (e->ElementType) {
        case CSSM_LIST_ELEMENT_WORDID:
            break;
        case CSSM_LIST_ELEMENT_DATUM:
            if ((e->Element.Word.Length && !e->Element.Word.Data) ||
                !consume(&budget->bytes, e->Element.Word.Length)) return false;
            break;
        case CSSM_LIST_ELEMENT_SUBLIST:
            if (!measureSubject(&e->Element.Sublist, depth + 1, budget)) return false;
            break;
        default:
            return false;
        }
    }
    return true;
}

bool TimesPolicyFits(const CSSM_ACL_OWNER_PROTOTYPE *owner, uint32_t count, const CSSM_ACL_ENTRY_INFO *entries)
{
    PolicyBudget budget = { MaxNodes, MaxPolicyBytes };
    if (!owner || !entries || !count || count > MaxEntries ||
        !measureSubject(&owner->TypedSubject, 0, &budget)) return false;
    for (uint32_t i = 0; i < count; ++i) {
        const CSSM_ACL_ENTRY_PROTOTYPE *p = &entries[i].EntryPublicInfo;
        if (p->Authorization.NumberOfAuthTags > MaxRights ||
            !consume(&budget.bytes, sizeof(*p) + p->Authorization.NumberOfAuthTags * sizeof(*p->Authorization.AuthTags)) ||
            !consume(&budget.bytes, p->TimeRange.StartDate.Length) || !consume(&budget.bytes, p->TimeRange.EndDate.Length) ||
            !measureSubject(&p->TypedSubject, 0, &budget)) return false;
    }
    return true;
}

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
typedef struct { unsigned grants, applications; CFDataRef requirement; } ReaderSubject;

// SecAccessGetOwnerAndACL exposes the typed code-signature subject. Recognize
// the OS X form with exactly one requirement. The comment is never a pathname
// to open. Unknown forms and additional subjects cannot establish a sole app.
static void collectCodeSubject(const CSSM_LIST_ELEMENT *head, ReaderSubject *reader)
{
    if (!reader || head->ElementType != CSSM_LIST_ELEMENT_WORDID ||
        head->WordID != CSSM_ACL_SUBJECT_TYPE_CODE_SIGNATURE) return;
    if (++reader->applications != 1) return;
    const CSSM_LIST_ELEMENT *type = head->NextElement;
    const CSSM_LIST_ELEMENT *hash = type ? type->NextElement : NULL;
    const CSSM_LIST_ELEMENT *comment = hash ? hash->NextElement : NULL;
    const CSSM_LIST_ELEMENT *requirement = comment ? comment->NextElement : NULL;
    if (!requirement || requirement->NextElement || type->ElementType != CSSM_LIST_ELEMENT_WORDID ||
        type->WordID != CSSM_ACL_CODE_SIGNATURE_OSX || hash->ElementType != CSSM_LIST_ELEMENT_DATUM ||
        hash->Element.Word.Length != CC_SHA1_DIGEST_LENGTH || comment->ElementType != CSSM_LIST_ELEMENT_DATUM ||
        requirement->ElementType != CSSM_LIST_ELEMENT_DATUM) return;
    const CSSM_DATA *bytes = &requirement->Element.Word;
    if (bytes->Length && bytes->Data) reader->requirement = CFDataCreate(NULL, bytes->Data, bytes->Length);
}

static bool appendSubject(CFMutableDataRef bytes, const CSSM_LIST *list, unsigned depth,
                          CFTypeRef metadata, CFMutableBagRef prompts, ReaderSubject *reader)
{
    if (depth > MaxDepth || !list->Head) return false;
    CFIndex start = CFDataGetLength(bytes);
    appendNumber(bytes, list->ListType);
    collectCodeSubject(list->Head, reader);
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
            if (!appendSubject(bytes, &e->Element.Sublist, depth + 1, metadata, prompts, reader)) return false;
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

// Even a rejected, deeply nested descriptor must be released without recursing.
// Use the owned sublist headers as the return stack, preserving sibling links.
static void freeSubject(CSSM_LIST *list)
{
    CSSM_LIST_ELEMENT *e = list->Head;
    CSSM_LIST_ELEMENT *parents = NULL;
    while (e || parents) {
        if (!e) {
            e = parents;
            parents = e->Element.Sublist.Head;
        } else if (e->ElementType == CSSM_LIST_ELEMENT_SUBLIST) {
            CSSM_LIST_ELEMENT *child = e->Element.Sublist.Head;
            e->Element.Sublist.Head = parents;
            parents = e;
            e = child;
            continue;
        } else if (e->ElementType == CSSM_LIST_ELEMENT_DATUM) {
            free(e->Element.Word.Data);
        }
        CSSM_LIST_ELEMENT *sibling = e->NextElement;
        free(e);
        e = sibling;
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
    if (payload->Length > MaxPartitionBytes) return NULL;
    CFDataRef bytes = CFDataCreate(NULL, payload->Data, (CFIndex)payload->Length);
    if (!bytes) return NULL;
    CFPropertyListRef plist = CFPropertyListCreateWithData(NULL, bytes, kCFPropertyListImmutable, NULL, NULL);
    CFRelease(bytes);
    CFMutableBagRef ids = NULL;
    if (plist && CFGetTypeID(plist) == CFDictionaryGetTypeID() && CFDictionaryGetCount(plist) == 1) {
        CFArrayRef list = CFDictionaryGetValue(plist, CFSTR("Partitions"));
        if (list && CFGetTypeID(list) == CFArrayGetTypeID() && CFArrayGetCount(list) > 0 &&
            CFArrayGetCount(list) <= MaxPartitions) {
            ids = CFBagCreateMutable(NULL, 0, &kCFTypeBagCallBacks);
            for (CFIndex i = 0; ids && i < CFArrayGetCount(list); ++i) {
                CFTypeRef id = CFArrayGetValueAtIndex(list, i);
                if (CFGetTypeID(id) != CFStringGetTypeID() || !CFStringGetLength(id) || CFStringGetLength(id) > 256) {
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
static OSStatus collectEntry(CSSM_ACL_ENTRY_PROTOTYPE *p, CFMutableBagRef bags[], CFDataRef *digest, ReaderSubject *reader)
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
    bool decrypt = false;
    const CSSM_LIST_ELEMENT *head = p->TypedSubject.Head;
    for (uint32_t i = 0; i < p->Authorization.NumberOfAuthTags; ++i) {
        CSSM_ACL_AUTHORIZATION_TAG right = p->Authorization.AuthTags[i];
        if (right == CSSM_ACL_AUTHORIZATION_DECRYPT || right == CSSM_ACL_AUTHORIZATION_ANY) decrypt = true;
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
    if (reader && decrypt) reader->grants++;
    if (group == Ordinary && !appendSubject(subject, &p->TypedSubject, 0, metadata, bags[Prompts], decrypt ? reader : NULL)) goto done;
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
static OSStatus copyAccessPolicy(SecAccessRef access, CFArrayRef *result, ReaderSubject *reader)
{
    *result = NULL;
    CSSM_ACL_OWNER_PROTOTYPE *owner = NULL;
    CSSM_ACL_ENTRY_INFO *entries = NULL;
    uint32_t count = 0;
    CFArrayRef acl = NULL;
    OSStatus status = SecAccessCopyACLList(access, &acl);
    if (!status && (!acl || CFArrayGetCount(acl) > MaxEntries)) status = errSecInvalidItemRef;
    if (acl) CFRelease(acl);
    if (status) return status;
    status = SecAccessGetOwnerAndACL(access, &owner, &count, &entries);
    CFMutableDataRef ownerPolicy = NULL;
    CFMutableBagRef bags[BagCount] = {0};
    CFDataRef digest = NULL;
    if (status) goto done;
    if (!TimesPolicyFits(owner, count, entries)) { status = errSecInvalidItemRef; goto done; }
    status = errSecAllocate;
    for (int i = 0; i < BagCount; ++i) {
        bags[i] = CFBagCreateMutable(NULL, 0, &kCFTypeBagCallBacks);
        if (!bags[i]) goto done;
    }
    ownerPolicy = CFDataCreateMutable(NULL, 0);
    if (!ownerPolicy) goto done;
    appendNumber(ownerPolicy, owner->Delegate);
    if (!appendSubject(ownerPolicy, &owner->TypedSubject, 0, CFSTR("owner"), bags[Prompts], NULL)) {
        status = errSecInvalidItemRef;
        goto done;
    }
    for (uint32_t i = 0; i < count; ++i) {
        status = collectEntry(&entries[i].EntryPublicInfo, bags, &digest, reader);
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

OSStatus TimesCopyAccessPolicy(SecAccessRef access, CFArrayRef *result)
{
    return copyAccessPolicy(access, result, NULL);
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

// The scoped logical reference, complete policy and protected-value stamp must
// agree across processes. Bag order and process-local CF handles are immaterial.
typedef struct { uint8_t bytes[CC_SHA256_DIGEST_LENGTH]; } ViewDigest;

static int compareDigests(const void *left, const void *right)
{
    return memcmp(left, right, sizeof(ViewDigest));
}

static bool digestField(CFTypeRef field, ViewDigest *digest, PolicyBudget *budget, unsigned depth)
{
    if (!field || depth > MaxDepth || !consume(&budget->nodes, 1)) return false;
    CFTypeID type = CFGetTypeID(field);
    CC_SHA256_CTX hash;
    CC_SHA256_Init(&hash);
    if (type == CFArrayGetTypeID() || type == CFBagGetTypeID()) {
        bool bag = type == CFBagGetTypeID();
        CC_SHA256_Update(&hash, bag ? "B" : "A", 1);
        CFIndex count = bag ? CFBagGetCount(field) : CFArrayGetCount(field);
        if (count < 0 || (size_t)count > budget->nodes) return false;
        if (count) {
            const void **values = calloc(count, sizeof(*values));
            ViewDigest *children = calloc(count, sizeof(*children));
            bool ok = values && children;
            if (ok) {
                if (bag) CFBagGetValues(field, values);
                else CFArrayGetValues(field, CFRangeMake(0, count), values);
                for (CFIndex i = 0; ok && i < count; ++i) ok = digestField(values[i], &children[i], budget, depth + 1);
                if (ok) {
                    if (bag) qsort(children, count, sizeof(*children), compareDigests);
                    for (CFIndex i = 0; i < count; ++i) CC_SHA256_Update(&hash, &children[i], sizeof(*children));
                }
            }
            free(children);
            free(values);
            if (!ok) return false;
        }
    } else if (field == kCFNull) {
        CC_SHA256_Update(&hash, "N", 1);
    } else {
        bool string = type == CFStringGetTypeID();
        if (!string && type != CFDataGetTypeID()) return false;
        if (string && CFStringGetLength(field) > 256) return false;
        CFDataRef bytes = string ? CFStringCreateExternalRepresentation(NULL, field, kCFStringEncodingUTF8, 0)
                                 : (CFDataRef)CFRetain(field);
        if (!bytes) return false;
        CFIndex size = CFDataGetLength(bytes);
        bool ok = size >= 0 && consume(&budget->bytes, (size_t)size);
        CC_SHA256_Update(&hash, string ? "S" : "D", 1);
        if (ok && size) CC_SHA256_Update(&hash, CFDataGetBytePtr(bytes), (CC_LONG)size);
        CFRelease(bytes);
        if (!ok) return false;
    }
    return CC_SHA256_Final(digest->bytes, &hash) == 1;
}

OSStatus TimesReadViewDigest(CFDataRef persistent, CFArrayRef policy, const uint8_t *protectedValue, uint8_t *result)
{
    ViewDigest fields[3];
    PolicyBudget budget = { 4 * MaxNodes, 4 * MaxPolicyBytes };
    if (!persistent || !CFDataGetLength(persistent) || !policy || CFArrayGetCount(policy) != 7 ||
        !protectedValue || !digestField(persistent, &fields[0], &budget, 0) ||
        !digestField(policy, &fields[1], &budget, 0)) return errSecInvalidItemRef;
    memcpy(fields[2].bytes, protectedValue, sizeof(fields[2].bytes));
    CC_SHA256(fields, sizeof(fields), result);
    return errSecSuccess;
}

// The normal password read authenticates the item's integrity under the caller's
// existing rights. The issuer disables interaction for the entire transaction.
OSStatus TimesCheckPasswordData(SecKeychainItemRef item, const uint8_t *expected, size_t length)
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

static OSStatus checkApplicationSubject(const ReaderSubject *reader)
{
    SecCodeRef caller = NULL;
    SecStaticCodeRef code = NULL;
    CFDataRef expected = NULL;
    CFDictionaryRef info = NULL;
    OSStatus status = errSecAuthFailed;
    if (reader->grants != 1 || reader->applications != 1 || !reader->requirement) return status;
    // The ACL's description may be absent, stale or name a special file. The
    // executing code supplies its own identity for either type of authorized read.
    if (SecCodeCopySelf(kSecCSDefaultFlags, &caller) || !caller ||
        SecCodeCopyStaticCode(caller, kSecCSDefaultFlags, &code) || !code ||
        SecCodeCopySigningInformation(code, kSecCSRequirementInformation, &info) || !info) goto done;
    SecRequirementRef implicit = (SecRequirementRef)CFDictionaryGetValue(info, kSecCodeInfoImplicitDesignatedRequirement);
    if (!implicit || SecRequirementCopyData(implicit, kSecCSDefaultFlags, &expected)) goto done;
    // Membership in a group does not prove a sole reader. Require the entire
    // OS-derived identity and validate the running process against that requirement.
    if (expected && CFEqual(expected, reader->requirement))
        status = SecCodeCheckValidity(caller, kSecCSDefaultFlags, implicit);
done:
    if (expected) CFRelease(expected);
    if (info) CFRelease(info);
    if (code) CFRelease(code);
    if (caller) CFRelease(caller);
    return status;
}

static OSStatus checkReaderPolicy(SecAccessRef access, CFArrayRef policy, bool writable, const ReaderSubject *subject)
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
    status = checkApplicationSubject(subject);
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
    ReaderSubject subject = {0};
    OSStatus status = item ? SecKeychainItemCopyAccess(item, &access) : errSecParam;
    if (!status) status = access ? copyAccessPolicy(access, &policy, &subject) : errSecInvalidItemRef;
    if (!status) status = checkReaderPolicy(access, policy, writable != 0, &subject);
    if (subject.requirement) CFRelease(subject.requirement);
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
    return status ? status : TimesCheckPasswordData(item, expected, length);
}

OSStatus TimesCheckPasswordPolicy(CFArrayRef before, CFArrayRef after, SecKeychainItemRef item,
                                  const uint8_t *expected, size_t length)
{
    if (!before || !after || CFArrayGetCount(before) != 7 || CFArrayGetCount(after) != 7) return errSecInvalidItemRef;
    for (int i = 0; i < 6; ++i) {
        if (!CFEqual(CFArrayGetValueAtIndex(before, i), CFArrayGetValueAtIndex(after, i))) return errSecInvalidItemRef;
    }
    if (CFArrayGetValueAtIndex(after, 6) == kCFNull) return errSecInvalidItemRef;
    return TimesCheckPasswordData(item, expected, length);
}
