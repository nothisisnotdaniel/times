# 054 Signing key stored as raw bytes

As the maker, I want the issuer to keep the private signing key as raw PKCS#8 bytes in the Keychain, so that the key never sits in managed strings that cannot be wiped.

## What

Change SigningKeyStore to write and read the raw PKCS#8 bytes instead of base64 text, clearing every buffer after use; read the old base64 form once and rewrite it, so an existing key keeps working.

## Why

The security review of job 302 noted that base64 passes the private key through immutable strings; low risk on a developer machine, but cheap to tighten.

## Notes

Proposed by job 302-signed-licence-keys.

Proposed by job 302-signed-licence-keys (`factory/jobs/302-signed-licence-keys/`).
