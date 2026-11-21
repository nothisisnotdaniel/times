# 067 Share the balance rule between Timesheet and FlexitimeBalance

As the developer, I want the timesheet and the flexitime balance to use one balance rule, so that the two can never drift apart.

Category: tidy-up

## What

Move the per-day balance and empty-day rule used by Timesheet.Calculate and FlexitimeBalance.Compute into one shared helper; behaviour and output stay the same and the existing tests keep passing.

## Why

The rule is written twice today; tests catch drift, but one place is simpler to change.

## Notes

Proposed by job 309-monthly-timesheet (review-code 1).

Proposed by job 309-monthly-timesheet (`factory/jobs/309-monthly-timesheet/`).
