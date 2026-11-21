# 071 Cap the number of calendar meetings read

As the user, I want Times to refuse an absurdly long meeting list from Context, so that a broken Context never fills the screen with a huge table.

Category: security

## What

Since job 312 `meetings` reads today's appointments from `context current --json` through CalendarMeetings.Parse (Times/src/Times/CalendarMeetings.cs), which accepts any number of appointments; only the 1 MiB stdout cap bounds it. Cap the list (for example 500 items) and, above it, show a clear message instead of the table.

## Why

A runaway or broken Context output would otherwise draw thousands of rows and make the prompt unusable.

## Notes

Proposed by job 312-calendar-to-checkpoints (review-security 1).

Proposed by job 312-calendar-to-checkpoints (`factory/jobs/312-calendar-to-checkpoints/`).
