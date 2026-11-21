# 050 CSV export file mode and failure test

As a Times user, I want CSV exports created with a deliberate file mode and the clean-up after a failed write tested, so that my exports are safe and never left half-written.

Category: security

## What

- CSV export failure test (was #20): Add an injectable stream factory so a test can check that a partial CSV file is deleted after a mid-write failure
- CSV export file mode (was #21): Decide whether exported CSV files are created owner-only (0600) or stay shareable

## Why

- #20: The cleanup exists but can't be tested without a way to inject a failing stream. Job 100's code and security reviews noted it.
- #21: Exports contain personal work data, yet they are often meant to be shared. Job 100's security review asked for an explicit decision.

## Notes

- Merges #20, #21 on 2026-11-16, to save the fixed cost of one job per item.
- Cheap mode is enough. Decide the file mode in the spec.
- #20: From 100 code/security review. Proposed by job 100-csv-export (`factory/jobs/100-csv-export/`).
- #21: From 100 security review. Proposed by job 100-csv-export (`factory/jobs/100-csv-export/`).
