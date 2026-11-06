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
    the hint rows (the hint wrapped at the terminal width onto 1 to 3 rows, `App.MaxHintRows`; more than 1
    only while the top area keeps 4 rows; a cut hint ends its last row in a grey `…`; the rows come out
    of the top area, never moving the input box), and a 3-line bordered input panel at the bottom; the
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
    A paste keeps at most 4096 characters (`LineEditor.MaxPasteLength`, counted after that clean-up, never
    half a surrogate pair); the rest is read to `ESC[201~` and dropped, and a red hint on the hint rows says so (in place; if the hint's row count changes, every row but the input line is rewritten in place).
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
  `renameproject` (`rn`), `removeproject` (`xp`) and `moveproject` (`mp`) change a project by its number
  (`ProjectList.Rename/Remove/Move`) and write only `projects.json`, never a day file.
  Day files and `projects.json` must be regular files (the final link target) of at most 64 KiB, read
  through `CappedFile`: `FileKind` checks the path first, then on Unix the file is opened with
  `O_NONBLOCK | O_CLOEXEC` and the opened descriptor is checked with `fstat` before a capped read, so a
  FIFO or device (even one swapped in after the check) is refused without blocking. A day file or
  `projects.json` whose property names or strings cannot be decoded (lone surrogates) is unreadable.
  Project titles and checkpoint names (`ProjectList.IsValidTitle`) must be non-blank, at most 200
  characters, and hold no control characters, no unpaired surrogates and no invisible code points
  (`ProjectList.HasInvisibleOrBrokenCharacters`, checked per code point): Unicode Format (Cf) (bidi
  overrides, zero-width characters, soft hyphens, tag characters), `Default_Ignorable_Code_Point`
  (Hangul fillers, combining grapheme joiner, Khmer inherent vowels, Mongolian and other variation
  selectors, unassigned ignorables; one table, `DefaultIgnorableRanges`) and U+2800 braille blank. Only
  U+FE0E/U+FE0F are allowed, and only right after a visible code point (so `❤️` stays valid); this is checked
  when typed (red hint; for hidden or broken characters it also says to retype the text without
  them) and when `projects.json` or a day file is loaded (file is unreadable).
  A day file may instead hold `"absence":"vacation"|"sick"|"publicHoliday"` (written before `"entries"`,
  left out when none; never together with entries or the legacy properties, otherwise unreadable), set by
  `absence` (`ab`) on the shown day; booking commands refuse an absence day.
- **Relative times:** every typed time (`arrive`, `leave`, `break`, `breakend`, `checkpoint`, `change`) also takes
  `now`, `-<n>m` or `+<n>m` (1-4 ASCII digits, exact and case-sensitive), worked out by `TimeInput.Parse` from `App.Now()`
  (seconds dropped); a result outside 0:00-23:59 is `CrossesMidnight` (own red hint), never wrapped; files keep plain times.
- **Settings:** features register their user preferences in the one `SettingsRegistry` (kinds
  `OnOff`, `Number` with min/max, `Choice` with options; registration order is display order;
  `CreateDefault()` registers `daily-target`, a `Number` 0..1440 with default 480, the daily work target
  in minutes), the way commands use `CommandRegistry`, and never add their own
  command for them. `settings` (`se`) opens the modal `SettingsPanel` in place of the top area. Values
  live in `settings.json` next to the day files (`SettingsStore`, `{"settings":{"<name>":true|1|"opt"}}`),
  read like `projects.json` through `CappedFile` (regular file, 64 KiB cap); a broken file is shown as
  unreadable and never overwritten, unknown names are kept on save, and every change is saved at once.
- **Target and balance:** `DayRecord.TimeLeft(target)` counts down from the target `App.RenderDay`
  reads from `daily-target` in `settings.json` on every redraw (missing, broken or out of range: 480;
  never written). Below the `Breaks` line, `DayView` shows `Target 8:00 · day -0:30 · balance +1:15`
  (signed values via `DayView.FormatSigned`; an empty day shows it without `day`, only when a day
  counts). `FlexitimeBalance.Compute` sums `WorkTime() - target` over every day file (`DayStore.Days()`:
  names exactly `yyyy-MM-dd.json`, folder listing only) on or before the shown day that holds an
  arrival; unreadable files are skipped and counted (yellow `! N day file(s) skipped`, no names).
  An absence day counts with a delta of zero, and `DayView` shows `Absence: Vacation` plus the
  target line with `day +0:00` for it.
- **Report:** `report <from> <to>` (`rp`, dates `yyyy-MM-dd`, inclusive) sums `CheckpointSummary` per day over the
  day files `DayStore.Days()` lists in the range (`ProjectReport.Calculate`, read-only), one row per work item
  (ascending, title from `projects.json` or the names) then per name (ordinal), with `H:mm`, decimal hours
  (invariant, 2 decimals) and a `Total` row (`App.ReportView`); unreadable day files are skipped and counted.
  Titles come from `ReportRow.Title(projects)`. `export <from> <to> <folder>` (`ex`) writes the same rows as
  `times-report-<from>-to-<to>.csv` (`ReportCsv`: UTF-8 BOM, CRLF, `Work item,Title,Hours`, RFC 4180 quoting, a `'`
  before titles starting `=+-@`, no total) into an existing absolute folder (one pair of quotes removed, `~` expanded),
  created with `FileMode.CreateNew` (never overwrites or follows a link; a failed write deletes the file).
  The `report-rounding` setting (`off`/`5 min`/`10 min`/`15 min`, read at each call, anything else is `off`) rounds
  each row's time per day to the nearest step (half rounds up) in `report` and `export` only, adding
  ` · rounded to <n> min` to the caption and the export success line.
- **Updates:** `UpdateService` (wired in `Program.cs`, `null` in tests unless injected) runs one background
  check at start (`IReleaseSource`, GitHub's release list only, allow-listed asset URLs, capped and timed out) and
  the `update` (`up`) command. Settings `update-mode` (`notify only` / `install on quit` / `install and restart`)
  and `update-channel` (`stable` / `beta` / `develop`). A package is staged only after `UpdateDownloader` checks
  the digest, the ECDSA P-256 `.sig` against the keys embedded from `src/Times/update-key.pem` (public key only,
  committed by the user; missing means nothing is staged) and the nuspec, and writes a `nuget.config` with
  `<clear />` next to it (passed as `--configfile`, so no other feed can serve the version). At quit `UpdateApplier` writes
  `update-pending.json` and starts one detached helper (`nohup sh -c` fixed script, or `cmd /c` with refused
  characters) through `IProcessLauncher`; the next start reports and deletes the pending file. Tests use
  `FakeReleaseSource`, `TestSigner` and `FakeLauncher`: no network, no real `dotnet tool update`, never the real key.
- **Azure DevOps:** `devops` (`dv`) shows, sets (`connect <org>`, masked `LineEditor` prompt, never in history) or
  removes (`disconnect`) the link; the PAT lives only in the OS store (`ITokenStore`: macOS Keychain generic password
  service `Times Azure DevOps` whose access list (`SecAccessCreate`, built before the old item is deleted) trusts only the
  apphost at `Environment.ProcessPath` (symlinks resolved), never a .NET host (named `dotnet` or next to `host/fxr`);
  `ITokenStore.WriteRefusal` says so before the masked prompt; Windows Credential Manager target `Times/AzureDevOps`; `UnsupportedTokenStore` elsewhere
  and in tests unless injected), never in a file, output or exception. A `devops`/`dv` line with an argument never enters
  `LineEditor.History` (the editor's `keepOutOfHistory` predicate). `sync <from> <to>` (`sy`) previews
  `DevOpsSync.Plan` (ended, unbooked checkpoints with a work item, per work item) with no store read or network;
  `sync <from> <to> send` reads the store once, then per work item GETs Completed Work and PATCHes
  `round(old + hours, 2)` with a rev test (`AzureDevOpsClient`: no redirects, 15 s, 1 MiB cap, fixed error texts),
  then marks those checkpoints booked; failures stay per item. Tests use `FakeTokenStore` and `FakeDevOpsClient`.
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
    Both actions are pinned to full commit SHAs with a trailing `# vX.Y.Z` comment (change the SHA and the comment together),
    so a moved tag cannot run new code with the `contents: write` token. `develop-version.sh` runs under
    `LC_ALL=C` and ignores a tag whose MAJOR, MINOR or PATCH has more than 9 digits, so the bumped patch
    fits in 32-bit version parts. The workflow tests read `release.yml` through their own small YAML reader
    (`tests/Times.Tests/WorkflowYaml.cs`), so reformatting the YAML does not break them.
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
