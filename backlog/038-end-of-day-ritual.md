# 038 End-of-day ritual

As a developer, I want one command that closes my workday, so that I leave with my time booked, my open work listed and tomorrow planned.

## What

An `eod` command (in the session and as `times eod`) books the leaving time (now or a given time), shows today's hours per work item, lists open todos from Context when Context is installed (through its `--json` CLI), asks for one line of plan for tomorrow and saves it as a Context note, and prints a stand-up draft (yesterday / today) that can be copied. Every Context step is skipped quietly when Context isn't installed. Full path.

## Why

The end of the day is when bookings get forgotten and context gets lost. A short daily ritual is used every day, which is what makes people keep (and pay for) a tool.

## Notes

Uses Context#17 (JSON output) for the todos.
