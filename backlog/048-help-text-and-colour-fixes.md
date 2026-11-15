# 048 Help text and colour fixes

As a Times user, I want the help texts and colours to read naturally and look intentional, so that the screen is clear.

## What

- Clearer time help text (was #25): Reword the arrive/leave/break/breakend help ("now, or at an optional H:mm, -10m or +5m") so it reads naturally; update the help tests to match.
- Report caption on one line (was #26): Keep the ' · rounded to <n> min' suffix on one line in narrow report tables (shorten or move it so it never wraps onto a second grey line).
- Target hint colour (was #32): Pick a colour for the target hint instead of the steelblue1 accent (review-ui 1)
- Mention the live clock in alert settings (was #33): Mention the live clock in the alert setting descriptions (review-ux 1)
- Holiday name in accent colour (was #34): Style only the holiday name in the accent colour, not the whole 'Public holiday · <name>' value (115 review-ui 1)

## Why

- #25: The current wording ("now, or at an optional H:mm, -10m or +5m") is accurate but awkward. Job 103 left this as a follow-up; the help tests need updating with it.
- #26: In narrow tables the suffix wraps and looks like a layout bug. Job 104 left this as a follow-up.
- #32: It uses the general steelblue1 accent today. Job 113's UI reviewer noted it.
- #33: Without that, it's unclear why an alert doesn't ring while the clock isn't running. Job 113's UX reviewer noted it.
- #34: Colouring the whole value makes the label look like data. Job 115's UI reviewer noted it.

## Notes

- Merges #25, #26, #32, #33, #34 on 2026-11-16, to save the fixed cost of one job per item.
- Cheap mode is enough: text and colour changes.
- #25: Proposed by job 103-relative-times (`factory/jobs/103-relative-times/`).
- #26: Proposed by job 104-report-rounding (`factory/jobs/104-report-rounding/`).
- #32: Proposed by job 113-end-of-day-alert (`factory/jobs/113-end-of-day-alert/`).
- #33: Proposed by job 113-end-of-day-alert (`factory/jobs/113-end-of-day-alert/`).
- #34: Proposed by job 115-public-holidays (`factory/jobs/115-public-holidays/`).
