# 004 One-shot commands

As a Times user, I want to run a single command like `times arrive 8:15` from the shell, so that I can book time without opening the interactive session.

## What

`times <command> [args]` (for example `times arrive 8:15`, `times leave`, `times status`) runs one registered command against today without the interactive session, prints the result as plain lines (no layout, no popup, no mouse reporting) and exits with 0 on success or non-zero with a red line on an error. Without arguments Times starts the interactive session as before. Same data folder rules and validation as the session.

## Why

Many bookings are one quick action, and opening the full-screen app for them is slow. One-shot commands also make Times scriptable, which the menu bar and tray app and other tools build on.
