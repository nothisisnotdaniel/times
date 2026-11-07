# 021 CSV export file mode

As a Times user, I want a deliberate choice on whether exported CSV files are private (0600) or shareable, so that my time data isn't more or less exposed than I expect.

## What

Decide whether exported CSV files are created owner-only (0600) or stay shareable

## Why

Exports contain personal work data, yet they are often meant to be shared. Job 100's security review asked for an explicit decision.

## Notes

From 100 security review. Proposed by job 100-csv-export (`factory/jobs/100-csv-export/`).
