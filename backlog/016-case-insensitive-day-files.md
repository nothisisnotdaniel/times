# 016 Case-insensitive day files

As a Times user, I want day files ending in `.JSON` to count too, so that my balance is right on file systems that ignore case.

## What

Also count day files with an upper-case `.JSON` extension on file systems that ignore case.

## Why

A file copied or renamed with an upper-case extension is silently skipped today, which makes the balance wrong without any hint. Job 091 left this as a follow-up.

## Notes

Proposed by job 091-daily-target-flexitime (`factory/jobs/091-daily-target-flexitime/`).
