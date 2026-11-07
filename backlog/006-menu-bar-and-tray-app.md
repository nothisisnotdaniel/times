# 006 Menu bar and tray app

As a Times user, I want a menu bar or tray icon that shows whether I'm checked in and lets me arrive, break and leave, so that I can track time without a terminal window open.

## What

A small separate app (macOS menu bar, Windows tray) that shows whether you're checked in, the open checkpoint and the time left, and offers arrive, break, break end and leave. It calls the one-shot commands (`times status --json` and friends) and keeps no data of its own. Spec picks the toolkit; any new package needs a reason in the spec. Builds on one-shot commands. Revisit if Times moves into the Context desktop app.

## Why

The terminal app is great for detail but easy to forget. A small companion that only calls the one-shot commands keeps a single source of data and puts the most common actions one click away.
