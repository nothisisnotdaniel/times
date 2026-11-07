# 001 Windows terminal check

As the user, I want Times checked by hand in Windows Terminal and the old console window, so that it works for Windows users and not only in tests.

## What

Run Times by hand in Windows Terminal and the old console window (conhost). Check typing, Tab, ↑/↓, Esc, Backspace, PgUp/PgDn, AltGr and accented characters, mouse-wheel scrolling and clicks. After `/quit` and after Ctrl+C, check that drag-select (Quick Edit) works in the shell again. When the console mode is restored, check the `SetConsoleMode` result and handle a failure. Fix whatever breaks.

## Why

Keyboard, mouse and console-mode handling differ between Windows Terminal and conhost, and the tests can't see a real console. A broken Quick Edit after quitting, or a failed `SetConsoleMode` that goes unnoticed, would leave the user's shell in a bad state.
