# 063 Tray: remind again after a missed check-in balloon

As a Times user, I want the check-in reminder to come back if I missed it, so that focus assist or a balloon that timed out unseen doesn't leave my arrival unbooked.

Category: feature

## What

When the check-in reminder was shown but neither clicked nor followed by a booking (for example hidden by focus assist or Do Not Disturb), show it once more after a set time. This changes the spec's once-per-date rule, so the new rule (how often, how many times) needs to be decided in the spec.

## Why

Raised by the UX review of job 307: with focus assist on, the only reminder of the day can be swallowed, and the user forgets to book the arrival.

## Notes

Proposed by job 307-check-in-reminder (review-ux 2).

Proposed by job 307-check-in-reminder (`factory/jobs/307-check-in-reminder/`).
