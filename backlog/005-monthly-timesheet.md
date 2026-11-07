# 005 Monthly timesheet

As a Times user, I want a printable HTML timesheet for a month, so that I can hand in or file my hours without copying them over.

## What

A command with an alias writes a printable timesheet for a month (one row per day with arrival, leaving, breaks, working time and absences, plus the month's total and balance) as a self-contained HTML file into a folder the user names, never overwriting without asking. All text HTML-escaped; no scripts or external resources in the file.

## Why

Employers and records often want a monthly sheet with arrivals, leavings, breaks, absences and the balance. A self-contained, escaped HTML file prints well and carries no scripts or outside resources, so it's safe to open and share.
