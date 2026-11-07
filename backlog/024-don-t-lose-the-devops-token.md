# 024 Don't lose the DevOps token on save

As a Times user, I want my Azure DevOps token kept when saving a new one fails, so that a failed update doesn't leave me with no token at all.

## What

When saving the new Keychain item fails after the old one was deleted, the old token is lost; keep it (for example by updating the item in place)

## Why

Today the old Keychain item is deleted before the new one is saved, so a failed save loses both. Updating in place (or saving first) avoids it. Job 101 left this as a follow-up.

## Notes

Proposed by job 101-azure-devops-sync (`factory/jobs/101-azure-devops-sync/`).
