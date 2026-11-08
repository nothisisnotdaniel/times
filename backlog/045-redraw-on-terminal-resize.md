# 045 Redraw on terminal resize

As a Times user, I want the screen to redraw cleanly when I resize the terminal window, so that the layout doesn't break while I work.

## What

- Detect a change of the terminal size while the interactive session runs (on macOS and Linux and on Windows) and redraw the full-height layout at the new size, with the input box and cursor in the right place.
- Typed but unsent input, the selected day and any open panel survive the redraw.
- Shrinking, growing, and many quick resizes in a row leave no stray rows, scrolled-up lines or half-drawn borders; a very small window shows a readable fallback rather than a broken layout.
- One-shot commands are unaffected.
- Tests cover the redraw with a TestConsole whose size changes; the real-terminal check stays part of #2.

## Why

Found during the manual check for #2: on macOS, resizing the terminal window breaks the whole layout. Times reads the size only at start and never watches for a change, so any resize leaves the screen drawn for the old size. Resizing is common, so this is one of the first things a user hits.
