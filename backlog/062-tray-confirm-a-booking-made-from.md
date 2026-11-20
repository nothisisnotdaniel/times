# 062 Tray: confirm a booking made from the reminder

As a Times user on Windows, I want a short confirmation after clicking the check-in reminder, so that I know the arrival was booked without opening the tray menu.

## What

After a click on the check-in reminder balloon books the arrival, show a brief confirmation (for example a second balloon or a tray tooltip naming the booked time). Today the click books silently and only the refreshed tray menu shows the arrival.

## Why

Raised by the UX review of job 307: a silent booking leaves the user unsure whether the click worked, so they may open the menu or book twice.

## Notes

Proposed by job 307-check-in-reminder (review-ux 1).

Proposed by job 307-check-in-reminder (`factory/jobs/307-check-in-reminder/`).
