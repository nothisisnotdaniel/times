#include <Security/Security.h>
#include <string.h>
#include "VerifierHashes.h"

// The two linker-generated code directories identify only the native verifier
// built alongside this bridge. Its only dynamic dependencies are system libraries.
OSStatus TimesCheckVerifierImage(const char *path, int pid)
{
    SecRequirementRef requirement = NULL;
    CFStringRef expression = CFSTR("cdhash H\"" TIMES_VIEW_ARM64 "\" or cdhash H\"" TIMES_VIEW_X86_64 "\"");
    OSStatus status = SecRequirementCreateWithString(expression, kSecCSDefaultFlags, &requirement);
    if (status) return status;
    if (pid == 0) {
        SecStaticCodeRef installed = NULL;
        CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)path, strlen(path), false);
        status = url ? SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &installed) : errSecAllocate;
        if (!status) status = SecStaticCodeCheckValidity(installed, kSecCSCheckAllArchitectures, requirement);
        if (installed) CFRelease(installed);
        if (url) CFRelease(url);
    } else {
        SecCodeRef guest = NULL;
        CFNumberRef number = CFNumberCreate(NULL, kCFNumberIntType, &pid);
        const void *key = kSecGuestAttributePid, *value = number;
        CFDictionaryRef attributes = number ? CFDictionaryCreate(NULL, &key, &value, 1,
            &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks) : NULL;
        status = attributes ? SecCodeCopyGuestWithAttributes(NULL, attributes, kSecCSDefaultFlags, &guest) : errSecAllocate;
        if (!status) status = SecCodeCheckValidity(guest, kSecCSDefaultFlags, requirement);
        if (guest) CFRelease(guest);
        if (attributes) CFRelease(attributes);
        if (number) CFRelease(number);
    }
    CFRelease(requirement);
    return status;
}
