# 029 Bound the background event queue

As a maintainer, I want the background event queue bounded or coalesced once there's a real event source, so that a noisy source can't grow memory without limit.

## What

Review-security 1 — bounding or coalescing the unbounded queue changes behaviour (dropping or merging events) and has no producer to design it against yet; belongs in the job that adds the first real event source (Teams, live clock).

## Why

Job 110's security review flagged the unbounded queue, but without a real producer there's nothing to design drops or merges against. It belongs with the first real source (Teams or the live clock).

## Notes

Proposed by job 110-background-events-loop (`factory/jobs/110-background-events-loop/`).
