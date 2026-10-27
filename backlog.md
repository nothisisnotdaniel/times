# Times backlog

`/factory next --project Times` runs the first unchecked item, checks it off and records the job id.
Write one feature per line, in plain language.

- [x] Main screen: replace the starter output with the interactive loop. The current day's view (date heading, empty state for now) sits above a pinned input box at the bottom, built as described in CLAUDE.md. Add the command registry with `/help` (lists registered commands) and `/quit` (exit code 0); unknown commands show a red hint. Tests drive it via TestConsole input and end with `/quit`. → 001-main-screen-loop
- [x] Day navigation: add commands to move the current day to the previous day, the next day and back to today. Moving to a day with no file shows it as empty and creates no file. → 002-day-navigation
- [ ] Hint position: show validation and error hints (the red messages) on the line just above the input box instead of at the top of the screen. The day view stays at the top.
- [ ] Command popup: typing `/` in an empty input box opens a popup just above the input box that lists all registered commands with their short descriptions. Each further key filters the list to the commands that match what was typed, ranked so that typos still find the right command (Levenshtein distance, e.g. `/tdoay` still shows `/today`). The popup closes on Enter, Esc, or when the line no longer starts with `/`. This settles open question 9; CLAUDE.md describes how the popup is drawn.
- [ ] Arrival and leaving: add commands to book an arrival and a leaving on the current day. Each takes an optional time as hours and minutes separated by `:` (e.g. `8:15` or `17:30`); without one it uses the current time. Invalid times show a red hint. Before writing any file, the app checks its app-data folder: if it is empty or not an absolute path, it shows a red error and exits with a non-zero code instead of writing relative to the working directory.
- [ ] Help overflow: when the `/help` list or a message is taller than the top area, don't clip it silently. Show what fits plus a grey "… more" cue, so it stays usable on short terminals.
- [ ] Tab feedback: when Tab matches more than one command, list the matching commands (or show a grey hint) instead of doing nothing.
- [ ] Input hint: show a grey placeholder in the empty input box, e.g. "Type /help for commands", that disappears when typing starts.
- [ ] Long input: when the typed line is wider than the input box, show a cue (e.g. a leading `…`) that the start is scrolled out of view.
- [ ] Terminal check: verify in a real terminal (macOS Terminal, Windows Terminal) that the full-height layout doesn't scroll by one row and the cursor sits in the input box; fix it if it does.
