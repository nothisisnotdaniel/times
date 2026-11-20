# 069 Keep the home-office mark when import replaces a day

As the user, I want an import that replaces a day to keep that day's home-office mark, so that I don't lose it without noticing.

## What

Since job 310 an import that replaces a day keeps the day's balance corrections, but it still drops the existing home-office mark (Times/src/Times/App.cs, around the replace path) without saying so. Keep the mark when the imported day has none, or say in the import output that it was dropped.

## Why

The home-office mark feeds the monthly timesheet; losing it silently gives a wrong count.

## Notes

Proposed by job 310-keep-corrections-import (review-ux 1).

Proposed by job 310-keep-corrections-import (`factory/jobs/310-keep-corrections-import/`).
