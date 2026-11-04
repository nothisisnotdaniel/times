# Times — conventions

Every factory agent reads this file before working on Times. Keep it short and accurate.

- **What it is:** a C# console app that helps tracking times used on projects. The user checks in, checks out and books breaks and can also add checkpoints that are later transfered into azure devops work items for controlling purposes.
- **How it runs:** the app stays open in the terminal until the user closes it. It is an
  interactive session, not a set of one-shot commands. The current day's view sits above an
  **input box at the bottom of the screen**, in the style of the GitHub Copilot CLI: a bordered
  prompt line where the user types commands.
  - Commands are bare words (e.g. `arrive 8:15`); one leading `/` is still accepted and stripped
    before the lookup (only one, so a double `/` is an unknown command). Names are registered
    without `/`, and `Register` rejects a name that is empty, starts with `/` or holds whitespace.
    The set of commands (the "palette") grows feature by feature. Each feature adds its commands
    to a single command registry (name, alias, short description, handler), and `help` lists
    whatever is registered. Every command has a required, unique alias of 1–2 ASCII letters (`a` for
    `arrive`, `cp` for `checkpoint`); `Register` rejects an alias that clashes (ignoring case) with
    any name or alias. `TryGet` and the popup's `Match` accept the alias (exact alias first, prefix
    only, no typo matching); Tab completion stays on names. `help` and popup rows show the alias in
    `steelblue1`. Unknown commands and other validation errors show a short `[red]` hint on the line
    **just above the input box** (not at the top of the screen) and never crash the loop.
  - **How the box is built** (no extra packages): a Spectre `Layout` that fills the terminal,
    with the quicklist panel (`QuicklistPanel`, a `Projects` panel with a grey border, a bold
    steelblue1 header and the `#id` in grey, reloaded from `projects.json` on every redraw, at
    most 5 content lines; left out while `help` or `projects` output is shown, via
    `CommandResult.HideQuicklist`) and the day view on top, both fitted by `TopArea.Fit`, then
    the hint line, and a 3-line bordered input panel at the bottom; the
    empty input line shows a grey `Type help for commands` placeholder after the prompt, and a
    line wider than the box shows its tail behind a grey leading `…`.
    After each command, clear the screen and redraw the whole layout. Input is read by our own
    small line editor, using `console.Cursor.SetPosition` and
    `console.Input.ReadKey(intercept: true)`: typing, Backspace, Enter, Esc (close the popup if
    it is open, otherwise clear the line), Tab (complete a command name from the registry,
    keeping a leading `/` if one was typed; if several match, extend to their common prefix and
    list them in the popup with a grey count footer; nothing on an empty line) and ↑/↓ (history). Don't use Spectre's `Live` display: it can't run while we read input.
  - **Scrolling:** the top area is a scrollable window (`TopArea.Fit(content, height, offset)`;
    `TopArea.MaxOffset` and `TopArea.PageSize` hold the rule). When it is taller than its rows (and
    has at least 3), grey cues show `↑ N more lines above · PgUp / Ctrl+↑` on its first row and
    `↓ N more lines below · PgDn / Ctrl+↓` on its last (`1 more line` when one line is hidden).
    PageUp/PageDown, Shift+PageUp/PageDown and Ctrl+↑/↓ (for macOS Terminal) move it by a page
    (height - 2 lines); plain ↑/↓ stay history. Like the
    popup, a scroll redraws in place: only the top-area rows, then the popup (if open) and the input
    line. The offset goes back to 0 on every full redraw (after every Enter).
    The mouse wheel moves it by 3 lines per notch through the same in-place redraw: `App.Run` turns
    terminal mouse reporting on (`MouseReporting`, SGR `ESC[?1000h ESC[?1006h`) before the first draw
    and off in a `finally` on every exit (Program.cs also turns it off on Ctrl+C and process exit).
    The line editor reads the `ESC [ …` sequence after an Esc that has a `[` waiting and decodes it
    with `MouseInput`; a lone Esc stays Esc. On Windows, `WindowsConsole.cs` sets the console input
    mode and turns wheel records into the same SGR keys. A sequence longer than `MouseInput.MaxKeys`
    (32) is read to its final `@`–`~` character and dropped whole, never typed.
    `MouseReporting` also turns bracketed paste on and off (`ESC[?2004h` / `ESC[?2004l`): the editor
    appends the text between `ESC[200~` and `ESC[201~` to the line in one step (line breaks and Tab
    become one space each, other control characters and `ESC [` sequences are dropped), never submitting it.
  - **Command popup:** while the line is non-empty and does not start with whitespace (after Esc:
    closed until the line is empty again; a lone `/` lists every command), a popup just above the input box lists the
    registered commands with their descriptions, filtered by what was typed and ranked by
    Levenshtein distance so typos still match. The popup is the one thing redrawn **while
    typing**: the line editor redraws only the popup's rows and the input line in place (via
    `Cursor.SetPosition`), never the whole layout. Everything else redraws only after a command.
  - `quit` ends the loop with exit code 0.
  - Tests drive the loop by queuing input on `TestConsole.Input` (`PushTextWithEnter`, `PushKey`)
    and always end with `quit`, so they can't hang.
- **Data:** stored as JSON, **one file per day**, in the user's app-data folder:
  `Environment.GetFolderPath(SpecialFolder.ApplicationData)/Times/yyyy-MM-dd.json`
  (`%APPDATA%\Times\` on Windows, `~/.config/Times/` on macOS/Linux). Setting the `TIMES_DATA_DIR`
  environment variable overrides that default; it must be an absolute path (used as given, no `~`
  expansion or trimming), otherwise Times refuses to start with exit code 2. A value that starts or
  ends with whitespace (`char.IsWhiteSpace`) is also refused with exit code 2 ("starts or ends
  with whitespace"). Unset or empty means the default. `DataFolder.Resolve` picks the folder and `help` shows it.
  Every shown data-folder path (startup errors, `help`) goes through `DataFolder.ForDisplay`, which
  drops control characters, line/paragraph separators and Cf code points (both halves of a
  supplementary pair); the real path is never changed. The data folder is injected,
  so tests use a temp folder and never touch the real app data. Use `System.Text.Json`.
  The project quicklist lives next to the day files in `projects.json` (`ProjectStore`), as
  `{"projects":[{"workItem":1234,"title":"…"}]}`; a project's number is its 1-based position.
  `projects.json` must be a regular file (its final link target, checked by `FileKind` before it is
  opened, so a FIFO or device is refused without blocking) of at most 64 KiB, read through a capped stream.
  Project titles and checkpoint names (`ProjectList.IsValidTitle`) must be non-blank, at most 200
  characters, and hold no control characters, no Unicode Format (Cf) code points (bidi overrides,
  zero-width characters, soft hyphens, tag characters) and no unpaired surrogates; this is checked
  when typed (red hint) and when `projects.json` or a day file is loaded (file is unreadable).
- **Settings:** features register their user preferences in the one `SettingsRegistry` (kinds
  `OnOff`, `Number` with min/max, `Choice` with options; registration order is display order;
  `CreateDefault()` has none yet), the way commands use `CommandRegistry`, and never add their own
  command for them. `settings` (`se`) opens the modal `SettingsPanel` in place of the top area. Values
  live in `settings.json` next to the day files (`SettingsStore`, `{"settings":{"<name>":true|1|"opt"}}`),
  read like `projects.json` through `CappedFile` (regular file, 64 KiB cap); a broken file is shown as
  unreadable and never overwritten, unknown names are kept on save, and every change is saved at once.
- **Days:** the app always shows one "current" day, starting at today. The user can move to
  the previous or next day or jump to a date. Navigating to a day with no file shows it as an
  empty day **without creating a file**. The day's file is created only when the first
  entry is booked on that day, so browsing never leaves empty files behind.
- **Stack:** .NET 10 (`net10.0`), C# with nullable reference types and implicit usings on.
  [Spectre.Console](https://spectreconsole.net) handles all terminal output.
- **Build:** `dotnet build`
- **Test command:** `dotnet test` (xUnit + Spectre.Console.Testing)
- **Run:** `dotnet run --project src/Times -- <args>`
- **Layout:**
  - `Times.slnx`: the solution.
  - `src/Times/`: the app. `Program.cs` only wires things up, and the logic starts in `App.cs`.
  - `tests/Times.Tests/`: one test class per production class (`AppTests.cs` tests `App.cs`).
  - `.github/workflows/release.yml`: on a push to `develop`, a merged `develop` -> `main` pull request
    titled `release vX.Y.Z`, or a pushed `vX.Y.Z` tag, tests, packs with
    `-p:Version` (the `ValidateVersion` target in `Times.csproj` refuses non-SemVer), smoke-installs the
    tool and creates the GitHub release with the `.nupkg`. A `develop` push makes a prerelease versioned
    `X.Y.(Z+1)-develop.<run number>` (next patch after the highest release tag, computed by
    `.github/scripts/develop-version.sh`); a `develop` push that changes only `backlog.md` runs no
    workflow (`paths-ignore`) and makes no prerelease; a release tag takes its version from the tag
    and must be on `main` (its commit an ancestor of `origin/main`), and a pushed `-develop` tag fails. A release PR
    takes its version from the title (`.github/scripts/release-pr-version.sh`: exactly `release vX.Y.Z`,
    no `-develop`, tag not yet there), and the release and its tag are made on the merge commit; the
    job's `if:` skips closed-unmerged PRs and PRs from other branches or forks. After a proper
    (non-prerelease) release `vX.Y.Z`, the `Prune old prereleases` step (`continue-on-error`) deletes
    older `-develop` prereleases with their tags (core <= X.Y.Z, filtered by
    `.github/scripts/old-prereleases.sh`); proper releases and higher prereleases stay.
    `<Version>0.1.0</Version>` is only the local default; `help` shows the running version above
    `Data folder:`.
- **Style:**
  - Write all output through an injected `IAnsiConsole`, never `Console.*` or the static
    `AnsiConsole.*` methods (except in `Program.cs`). Tests use `TestConsole` and assert on `.Output`.
  - Read the clock through an injected `TimeProvider`, never `DateTime.Now` or `DateTimeOffset.Now`.
    Tests use a fixed `TimeProvider` subclass.
  - Wrap any user or external text in `Markup.Escape` before it goes into markup.
  - Use Spectre widgets (`Table`, `Panel`, `Rule`, `FigletText`) instead of hand-drawn ASCII.
    Keep the colours consistent with what is already there (`SteelBlue1` accents, `grey` headers).
  - File-scoped namespaces, `internal sealed` unless a type must be public.
  - No primary constructors anywhere (classes, structs or positional records). Write a
    conventional constructor that assigns to fields or properties.
  - Private fields are camelCase **without** a leading underscore (`console`, not `_console`).
    Use `this.` where a field and a constructor parameter share a name.
  - No `var`: always write the explicit type. Braces on every `if`/`else`/`for`/`foreach`/`while`
    body, even single lines. `this.` only where a field and a parameter share a name.
  - `.editorconfig` holds these rules. The build enforces it (`EnforceCodeStyleInBuild`), so a
    style break is a build warning, and builds must stay at 0 warnings.
  - `Run` returns a process exit code: 0 for success, non-zero for errors. Show errors as
    `[red]` markup, never as a stack trace.
  - No new NuGet packages without a reason in the spec. The approver escalates any new dependency.
- **Git:** `develop` is the working branch: factory work branches off it as `factory/<job-id>` and is merged back with `--no-ff`. `main` holds releases only and is pushed to only for a new release.
  `.gitignore` is the standard `dotnet new gitignore`.
