# 002 Terminal layout check

As the user, I want the full-height layout checked in a real terminal, so that the screen doesn't jump a row and the cursor sits where I type.

## What

Verify in a real terminal (macOS Terminal, Windows Terminal) that the full-height layout doesn't scroll by one row and the cursor sits in the input box; fix it if it does.

## Why

An off-by-one row scroll or a misplaced cursor only shows up in a real terminal, not in TestConsole. It is the first thing a user sees, so it's worth one manual check on macOS and Windows.
