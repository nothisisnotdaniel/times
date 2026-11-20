# 075 Test the skipped hint order on empty days

As a Times developer, I want a test that checks where the skipped hint sits among the other day-view lines, so that a later change can not move it out of place unnoticed.

## What

Add a DayView test for an empty day with no counted day and skipped files that also has week warnings, a workplace line and correction rows, and check that the skipped hint keeps the target line slot and the other lines keep their order.

## Why

The AC-6 test of job 317 checks only the hint text, not its order with the week-warnings line, workplace line and correction rows (review-code nit 1). The code keeps the order today, but nothing guards it.

## Notes

Proposed by job 317-skipped-hint-empty-days (`factory/jobs/317-skipped-hint-empty-days/`).
