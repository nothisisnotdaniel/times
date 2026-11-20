# 058 Show a reading line before eod reads Context todos

As a Times user ending the day, I want a short line while eod reads Context's todos, so that a slow read doesn't look like a hang.

## What

A "reading Context…" line before the todos run changes the output, so it needs its own job. In src/Times/App.cs (EndOfDay), print a short grey line before the `context todos` run, which can block for up to 10 s; update the tests and MANUAL.md for the changed output.

## Why

Review-ux found that the `todos` run in EndOfDay can block up to 10 s with no feedback line in the session, so the user can't tell whether eod is working or stuck.

## Notes

Proposed by job 305-end-of-day-ritual.

Proposed by job 305-end-of-day-ritual (`factory/jobs/305-end-of-day-ritual/`).
