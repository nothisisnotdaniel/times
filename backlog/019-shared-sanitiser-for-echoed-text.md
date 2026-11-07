# 019 Shared sanitiser for echoed text

As a maintainer, I want all hints that echo typed text to use one escaping helper, so that a fix to escaping reaches every hint at once.

## What

The plain Markup.Escape echo of typed text in hints (remove, change, booked, time hints, absence) should go through one helper

## Why

Remove, change, booked, time and absence hints each call `Markup.Escape` on their own, which is easy to forget in a new hint. Job 098's security review suggested one shared helper.

## Notes

From 098 security review. Proposed by job 098-absence-days (`factory/jobs/098-absence-days/`).
