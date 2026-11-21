# 059 Test the real Context process path in ContextCli

As the developer of Times, I want a committed test of ContextCli's real process path, so that its timeout and output cap can't break unnoticed.

Category: test

## What

A committed test of the real process would start a process, and the spec's test plan rules that out. It needs its own job: add a test for src/Times/ContextCli.cs (around line 68) that runs a real short-lived child process (for example a small script) to check the 10 s timeout kill and the 1 MiB output cap.

## Why

Review-code found that the real process path in ContextCli.cs (timeout, 1 MiB cap) has no committed test and was only checked by hand, so a regression there would go unnoticed.

## Notes

Proposed by job 305-end-of-day-ritual.

Proposed by job 305-end-of-day-ritual (`factory/jobs/305-end-of-day-ritual/`).
