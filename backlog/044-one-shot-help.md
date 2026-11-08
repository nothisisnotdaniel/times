# 044 One-shot help

As a Times user, I want `times help` from the shell to list only the commands I can run as one-shots, so that I'm not shown commands that would just be refused.

## What

- `times help` (one-shot) lists only commands that work as one-shots, including `status`, and leaves out the session-only ones (quit, settings, prev, next, today, update, import, devops connect).
- One-shot help leaves out the day-view part (what the day view's entries mean); that only matters inside the interactive session.
- `help <command>` from the shell still shows the details for a one-shot command.
- Help inside the interactive session stays unchanged.

## Why

Job 126 added one-shot commands, but `times help` prints the full session help: commands that are refused from the shell, plus day-view details nobody sees in one-shot mode. A help that matches what actually runs is shorter and doesn't mislead.
