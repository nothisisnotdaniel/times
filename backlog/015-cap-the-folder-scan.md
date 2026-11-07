# 015 Cap the folder scan

As a Times user with years of data, I want the flexitime balance to stay fast, so that a large data folder doesn't slow down every redraw.

## What

Cap or cache the day-file folder scan behind the flexitime balance, so a very large data folder can't slow every redraw.

## Why

The balance scans the whole day-file folder, and that cost grows with every day tracked. Job 091 left capping or caching the scan as a follow-up.

## Notes

Proposed by job 091-daily-target-flexitime (`factory/jobs/091-daily-target-flexitime/`).
