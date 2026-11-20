# 061 Tray: Windows icon check and handle cleanup

As a Times user on Windows, I want the tray icon to be clear on a dark taskbar and to free its native resources, so that it is readable and does not leak.

## What

Check the tray icon's contrast on a dark and a light Windows taskbar and adjust it if needed; free the native icon handle the Windows icon loader creates (DestroyIcon after the Icon is cloned). Run the Windows tray shell once by hand.

## Why

The Windows shell has only been compiled, never run, and its icon loader never frees its handle.

## Notes

Follow-up from job 306 (ui 2, security r3 nit 2). Needs a Windows machine.

Proposed by job 306-menu-bar-tray-app (`factory/jobs/306-menu-bar-tray-app/`).
