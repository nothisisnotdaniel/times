# 079 No spurious refusals in the event queue

As a maintainer, I want a post to the background event queue refused only when the queue is really full, so that a live-clock tick is never dropped by a race.

## What

In BackgroundEventQueue.Post, a refused post briefly increments the count before undoing it, so a concurrent post on a queue that has just freed a slot can be refused spuriously. Replace the increment-then-undo with a compare-and-swap loop that only reserves a slot when one is free, and add a concurrency test.

## Why

Raised by job 325 security review 1 and code review 2. The 64-event bound is never exceeded, so it is safe, but a refused tick is lost until the next one; it is conservative behaviour that a CAS loop removes.

## Notes

Proposed by job 325-bound-event-queue (`factory/jobs/325-bound-event-queue/`).
