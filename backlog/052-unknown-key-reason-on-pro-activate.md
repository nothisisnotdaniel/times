# 052 Unknown key reason on pro activate

As a Pro buyer, I want `times pro activate` to say "unknown key" when my key was signed by a key this build doesn't know, so that I know to update the app instead of thinking my key is broken.

Category: ux

## What

In LicenceKey.Verify, when no public key in LicencePublicKeys verifies the signature and the payload's key id is not in the table, report "unknown key" instead of "signature does not match". Keep checking the signature before trusting any field; only the reason text changes. Add a test with a token signed by a key not in the table.

## Why

The UX and code reviews of job 302 flagged that a key from a newer signing key reads as a bad signature, which is misleading and will cause support mails once a second key exists.

## Notes

Proposed by job 302-signed-licence-keys.

Proposed by job 302-signed-licence-keys (`factory/jobs/302-signed-licence-keys/`).
