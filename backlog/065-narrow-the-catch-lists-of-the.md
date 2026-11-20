# 065 Narrow the catch lists of the read-only day counters

As the developer, I want the read-only counters to catch only the exceptions DayStore.Load documents, so that a real bug isn't silently counted as a skipped file.

## What

HomeOfficeCount, VacationAllowance and the other read-only counters catch InvalidOperationException along with the IO and format errors when loading day files. Check which exceptions DayStore.Load can actually throw, narrow every counter's catch list to those together, and add a test for each.

## Why

A catch wider than the documented errors can hide a programming error as an unreadable file; changing them together keeps the counters consistent.

## Notes

Proposed by job 308-home-office-mark (review-security 2).

Proposed by job 308-home-office-mark (`factory/jobs/308-home-office-mark/`).
