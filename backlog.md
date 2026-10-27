# Times backlog

`/factory next --project Times` runs the first unchecked item, checks it off and records the job id.
Write one feature per line, in plain language.

- [x] Main screen: replace the starter output with the interactive loop. The current day's view (date heading, empty state for now) sits above a pinned input box at the bottom, built as described in CLAUDE.md. Add the command registry with `/help` (lists registered commands) and `/quit` (exit code 0); unknown commands show a red hint. Tests drive it via TestConsole input and end with `/quit`. → 001-main-screen-loop
- [x] Day navigation: add commands to move the current day to the previous day, the next day and back to today. Moving to a day with no file shows it as empty and creates no file. → 002-day-navigation
- [ ] Arrival and leaving: add commands to book an arrival and a leaving on the current day. Each takes an optional time as hours and minutes separated by `:` (e.g. `8:15` or `17:30`); without one it uses the current time. Invalid times show a red hint.
- [ ] Help overflow: when the `/help` list or a message is taller than the top area, don't clip it silently. Show what fits plus a grey "… more" cue, so it stays usable on short terminals.
- [ ] Tab feedback: when Tab matches more than one command, list the matching commands (or show a grey hint) instead of doing nothing.
- [ ] Input hint: show a grey placeholder in the empty input box, e.g. "Type /help for commands", that disappears when typing starts.
- [ ] Long input: when the typed line is wider than the input box, show a cue (e.g. a leading `…`) that the start is scrolled out of view.
- [ ] Terminal check: verify in a real terminal (macOS Terminal, Windows Terminal) that the full-height layout doesn't scroll by one row and the cursor sits in the input box; fix it if it does.

