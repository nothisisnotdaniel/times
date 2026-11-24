#!/bin/sh
set -eu
out=$1
compile() {
    xcrun clang -std=c11 -Wall -Werror -Wno-deprecated-declarations -arch arm64 -arch x86_64 \
        -framework Security -framework CoreFoundation "$@"
}
view="$out/TimesKeychainView"
compile -Wl,-dead_strip -Wl,-adhoc_codesign KeychainView.c KeychainMigration.c KeychainPolicy.c KeychainFiles.c -o "$view"
for arch in arm64 x86_64; do
    description=$(/usr/bin/codesign --display --verbose=4 --arch "$arch" "$view" 2>&1)
    hash=$(printf '%s\n' "$description" | /usr/bin/sed -n 's/^CDHash=\([0-9a-f]\{40\}\)$/\1/p')
    [ "${#hash}" = 40 ]
    case "$arch" in
        arm64) arm=$hash ;;
        x86_64) intel=$hash ;;
    esac
done
printf '#define TIMES_VIEW_ARM64 "%s"\n#define TIMES_VIEW_X86_64 "%s"\n' "$arm" "$intel" > "$out/VerifierHashes.h"
compile -dynamiclib -I"$out" VerifierIdentity.c KeychainMigration.c KeychainPolicy.c KeychainFiles.c \
    -o "$out/libTimesIssuerKeychain.dylib"
