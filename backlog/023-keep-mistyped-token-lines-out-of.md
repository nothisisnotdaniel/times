# 023 Keep mistyped token lines out of history

As a Times user, I want mistyped lines like `//devops connect x <token>` kept out of the in-memory history, so that a token never shows up when I press ↑.

## What

Keep mistyped lines such as '//devops connect x <token>' (rejected as unknown commands) out of the in-memory history too

## Why

Such lines are rejected as unknown commands but still land in history, along with the token they contain. Job 101 left this as a follow-up.

## Notes

Proposed by job 101-azure-devops-sync (`factory/jobs/101-azure-devops-sync/`).
