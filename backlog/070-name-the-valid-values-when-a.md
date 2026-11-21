# 070 Name the valid values when a setting falls back

As the user, I want the warning for an unknown setting value to say which values are valid, so that I can fix settings.json without looking it up.

Category: ux

## What

Since job 311 an unknown value in settings.json falls back for that setting only and a yellow line names the setting, but it gives no hint what a valid value is. Add the valid values (or the expected form) to that line, for example `theme: "blue" is not valid (light, dark); using light`.

## Why

Without the valid values the user has to guess or read the docs to fix the setting.

## Notes

Proposed by job 311-fallback-per-setting (review-ux 1).

Proposed by job 311-fallback-per-setting (`factory/jobs/311-fallback-per-setting/`).
