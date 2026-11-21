# 077 Sync send exits 1 when a work item fails

As a Times user who scripts the one-shot sync, I want sync send to exit with code 1 when any work item fails, so that a script can tell a partial failure from success.

Category: bug

## What

Make a failed work item during the one-shot Azure DevOps sync send set CommandResult.Failed, so the process exits 1; update the test that pins exit 0 for a failing item.

## Why

Today a failed item still exits 0 (job 320 kept it unchanged and its spec wording said 1 on failures). Code and ux reviews of job 320 noted the gap; scripts cannot detect a partial failure.

## Notes

Proposed by job 320-devops-sync-progress-line (`factory/jobs/320-devops-sync-progress-line/`).
