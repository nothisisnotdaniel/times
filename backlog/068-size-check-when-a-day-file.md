# 068 Size check when a day file is written

As the user, I want Times to refuse to write a day file it could not read back, so that a very large day never ends up as an unreadable file.

Category: bug

## What

DayStore.Write does not check the size of what it writes, while reading caps a day file at DayStore.MaxFileBytes (64 KiB). An import that merges a large imported day with a large existing day's corrections, a correction or a plain import could in theory write more than that, and the day would then load as unreadable. Check the serialised size in DayStore.Write and refuse the write with a clear message when it is over the cap, leaving the old file untouched.

## Why

A day file Times writes but cannot read back loses that day's bookings until the user repairs it by hand.

## Notes

Proposed by job 310-keep-corrections-import (review-security 1).

Proposed by job 310-keep-corrections-import (`factory/jobs/310-keep-corrections-import/`).
