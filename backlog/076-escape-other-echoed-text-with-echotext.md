# 076 Escape other echoed text with EchoText

As a maintainer, I want titles, error messages and file names shown in hints to go through EchoText.Markup too, so that every echo of outside text is escaped in one place.

Category: security

## What

Route the remaining Markup.Escape uses on titles, ex.Message and file names (DayView, Pro and others) through EchoText.Markup, keeping what the user sees the same unless the spec says otherwise.

## Why

Job 318 moved only the typed-text echoes in hints to the shared helper; the security review noted that the other Markup.Escape calls still escape on their own and are easy to get wrong in new code.

## Notes

Proposed by job 318-shared-echo-sanitiser (`factory/jobs/318-shared-echo-sanitiser/`).
