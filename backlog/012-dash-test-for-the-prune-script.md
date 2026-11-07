# 012 Dash test for the prune script

As a maintainer, I want the prerelease prune script tested under dash, so that it really runs on POSIX `sh` and not only on bash.

## What

Run the prerelease prune script under dash (POSIX sh) in a test or CI job, since this machine's sh is bash.

## Why

On this machine `sh` is bash, so bash-only syntax in the script would pass every local run and then fail where `sh` is dash. Job 081 left this as a follow-up.

## Notes

Proposed by job 081-prune-old-prereleases (`factory/jobs/081-prune-old-prereleases/`).
