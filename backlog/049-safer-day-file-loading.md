# 049 Safer day-file loading

As a Times user, I want day files loaded safely on every platform, counted whatever the case of their extension, and scanned without slowing every redraw, so that my balance is right and the app stays fast.

Category: bug

## What

- Safer file loading on Windows (was #13): On Windows the day file is still opened by path; make it as safe as on the other platforms. 086 left this out of scope.
- Cap the folder scan (was #15): Cap or cache the day-file folder scan behind the flexitime balance, so a very large data folder can't slow every redraw.
- Case-insensitive day files (was #16): Also count day files with an upper-case `.JSON` extension on file systems that ignore case.

## Why

- #13: Job 086 made day-file loading safer on the other platforms and left Windows out of scope, where the file is still opened by path. The same protection should apply everywhere.
- #15: The balance scans the whole day-file folder, and that cost grows with every day tracked. Job 091 left capping or caching the scan as a follow-up.
- #16: A file copied or renamed with an upper-case extension is silently skipped today, which makes the balance wrong without any hint. Job 091 left this as a follow-up.

## Notes

- Merges #13, #15, #16 on 2026-11-16, to save the fixed cost of one job per item.
- Cheap mode is enough.
- #13: Proposed by job 086-safer-file-loading (`factory/jobs/086-safer-file-loading/`).
- #15: Proposed by job 091-daily-target-flexitime (`factory/jobs/091-daily-target-flexitime/`).
- #16: Proposed by job 091-daily-target-flexitime (`factory/jobs/091-daily-target-flexitime/`).
