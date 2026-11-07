# 031 Remove unused Prime parameters

As a maintainer, I want the unused `targetOn` and `limitOn` parameters removed from `Prime`, so that the code says what it actually does.

## What

Remove the unused targetOn/limitOn parameters from Prime (review-code 1)

## Why

Unused parameters suggest behaviour that isn't there and confuse the next reader. Job 113's code reviewer noted it.

## Notes

Proposed by job 113-end-of-day-alert (`factory/jobs/113-end-of-day-alert/`).
