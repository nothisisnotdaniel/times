# 060 Tray: no Left today row on an absent day

As a Times user, I want the tray menu to leave out the Left today row on an absence day, so that it does not suggest I still owe a full day.

Category: ui

## What

On a day booked as absent, the tray menu shows no 'Left today' row (or shows the absence instead). Change AC-1 of the tray job's menu model and its tests to match.

## Why

Today an absent day shows 'Left today: 8:00', which reads as if work were still due.

## Notes

Follow-up from job 306 (code 2 / ux 1).

Proposed by job 306-menu-bar-tray-app (`factory/jobs/306-menu-bar-tray-app/`).
