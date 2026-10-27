# Times backlog

`/factory next --project Times` runs the first unchecked item, checks it off and records the job id.
Write one feature per line, in plain language.

- [ ] Main screen: replace the starter output with the interactive loop. The current day's view (date heading, empty state for now) sits above a pinned input box at the bottom, built as described in CLAUDE.md. Add the command registry with `/help` (lists registered commands) and `/quit` (exit code 0); unknown commands show a red hint. Tests drive it via TestConsole input and end with `/quit`.
