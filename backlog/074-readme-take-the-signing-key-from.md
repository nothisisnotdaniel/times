# 074 README: take the signing key from the release tag

As a Times user, I want the README to tell me to take the signing key from the release tag, so that a compromised default branch alone can't make a fake package look genuine.

## What

In README.md step 2 of the download check, say to fetch the public key (src/Times/update-key.pem) from the release's own tag rather than the default branch, and keep the existing note that the signature proves the workflow's key. Update ReadmeInstallTests if it checks that text.

## Why

Raised by the security review of job 316: taking the key from the repo proves little if the repo and the release are compromised together.

## Notes

Proposed by job 316-readme-checksum-guidance.

Proposed by job 316-readme-checksum-guidance (`factory/jobs/316-readme-checksum-guidance/`).
