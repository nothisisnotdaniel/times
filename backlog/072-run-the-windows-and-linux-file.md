# 072 Run the Windows and Linux file checks in CI

As the developer, I want the Windows-only and case-sensitive day-file tests to run somewhere, so that the handle check and the any-case .json listing are proven and not only read.

## What

Job 314 added tests for the Windows file-handle check (AC-2, CappedFile.cs) that return early off Windows, and an any-case .json branch (AC-4) that only matters on a case-sensitive file system. CI runs on ubuntu-latest only. Add a windows-latest leg to CI (or a separate job) that runs dotnet test, so the AC-2 tests run, and make sure the AC-4 branch is exercised on Linux.

## Why

Code that has never run on its target OS can hide a bug that only shows up for users on Windows.

## Notes

Proposed by job 314-safer-day-file-loading (review-code 2, review-security 2).

Proposed by job 314-safer-day-file-loading (`factory/jobs/314-safer-day-file-loading/`).
