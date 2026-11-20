# 057 Report hint order for bad dates

As a user typing `report --json` with bad dates in the session, I want to see the hint that fits best, so that I fix the right thing first.

## What

Decide whether the session's `--json is one-shot only` hint or the bad-date error should come first for `report --from x --json`, and keep or change the order with a test that pins it.

## Why

The UX review of job 303 noted that the one-shot hint is checked before the dates; it called the order fine and arguably right, so this is only to settle it on purpose.

## Notes

Proposed by job 303-work-item-hours-json.

Proposed by job 303-work-item-hours-json (`factory/jobs/303-work-item-hours-json/`).
