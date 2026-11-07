# 008 Keep balance corrections on import

As a Times user, I want an import that replaces a day to keep that day's balance corrections, or warn me first, so that I don't lose corrections without noticing.

## What

Replacing a day through import drops that day's balance corrections; keep them or warn before replacing (121 review-security 2, review-ux 2)

## Why

Balance corrections are entered by hand and are hard to recreate. Job 121's security and UX reviewers both noted that replacing a day through import silently drops them.

## Notes

Proposed by job 121-balance-corrections (`factory/jobs/121-balance-corrections/`).
