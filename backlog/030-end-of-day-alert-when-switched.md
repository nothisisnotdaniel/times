# 030 End-of-day alert when switched on later

As a Times user, I want a clear rule for an end-of-day alert that was off at startup and is switched on later, so that it rings when I expect it to.

## What

Should an alert that is switched off at startup still be marked as fired by Prime, or ring later when it is switched on (review-ux 2)?

## Why

Today Prime may mark such an alert as already fired, so switching it on later never rings. Job 113's UX reviewer raised it as an open question.

## Notes

Proposed by job 113-end-of-day-alert (`factory/jobs/113-end-of-day-alert/`).
