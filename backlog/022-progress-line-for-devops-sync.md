# 022 Progress line for DevOps sync

As a Times user, I want a "Sending …" line per work item during Azure DevOps sync, so that a slow network doesn't look like a hang.

## What

Show a 'Sending ...' progress line per work item during sync send, so a slow network does not look like a hang

## Why

Sync can take a while per item, and a silent screen makes it unclear whether anything is happening. Job 101 left this as a follow-up.

## Notes

Proposed by job 101-azure-devops-sync (`factory/jobs/101-azure-devops-sync/`).
