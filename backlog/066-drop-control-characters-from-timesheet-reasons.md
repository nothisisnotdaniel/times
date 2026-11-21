# 066 Drop control characters from timesheet reasons

As a Times user, I want control characters and lone surrogates kept out of the printed timesheet, so that an odd reason in an old day file never shows as a broken glyph.

Category: security

## What

In TimesheetHtml.Escape, replace C0 control characters (except none needed in a one-line reason) and lone surrogates with U+FFFD or drop them, with a test for each.

## Why

Reasons are refused at input, but a day file edited by hand can still hold them; the sheet is for printing, so it should stay clean.

## Notes

Proposed by job 309-monthly-timesheet (review-security 2).

Proposed by job 309-monthly-timesheet (`factory/jobs/309-monthly-timesheet/`).
