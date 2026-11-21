# 064 Cache the month's home-office count

As a user with years of day files, I want the day view to stay quick, so that showing a day doesn't re-read the whole month each time.

Category: tidy-up

## What

HomeOfficeCount.Calculate lists the data folder and loads every day file of the shown month on every render of the day view. Cache the count per month and invalidate it when a day file of that month is written or the folder changes, or show why it isn't needed after measuring with a large folder.

## Why

Each render costs up to about 31 capped file reads plus a folder listing; that is fine today but grows with the folder and repeats on every redraw.

## Notes

Proposed by job 308-home-office-mark (review-security 1).

Proposed by job 308-home-office-mark (`factory/jobs/308-home-office-mark/`).
