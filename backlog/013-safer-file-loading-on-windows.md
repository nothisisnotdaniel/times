# 013 Safer file loading on Windows

As a Times user on Windows, I want the day file loaded as safely as on macOS and Linux, so that Windows doesn't keep a weaker path.

## What

On Windows the day file is still opened by path; make it as safe as on the other platforms. 086 left this out of scope.

## Why

Job 086 made day-file loading safer on the other platforms and left Windows out of scope, where the file is still opened by path. The same protection should apply everywhere.

## Notes

Proposed by job 086-safer-file-loading (`factory/jobs/086-safer-file-loading/`).
