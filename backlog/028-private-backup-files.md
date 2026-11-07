# 028 Private backup files

As a Times user, I want a decision on whether backup zips are created owner-only (0600), so that my backups are as private as I expect.

## What

Review-security 3 — 0600 on the zip would change the file mode, which the spec does not cover, and would make `backup` differ from `export`. Worth its own job if backups should be private.

## Why

A backup holds all my time data. Job 108's security review noted that 0600 would change the file mode and make `backup` differ from `export`, so it should be decided on purpose.

## Notes

Proposed by job 108-backup-command (`factory/jobs/108-backup-command/`).
