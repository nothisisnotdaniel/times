# Times — conventions

Every factory agent reads this file before working on Times. Keep it short and accurate.

- **What it is:** a C# console app that helps tracking times used on projects. The user checks in, checks out and books breaks and can also add checkpoints that are later transfered into azure devops work items for controlling purposes.
- **How it runs:** the app stays open in the terminal until the user closes it. It is an
  interactive session, not a set of one-shot commands. The current day's view sits above an
  **input box at the bottom of the screen**, in the style of the GitHub Copilot CLI: a bordered
  prompt line where the user types commands.
  - Commands start with `/` (e.g. `/checkin`). The set of commands (the "palette") grows
    feature by feature. Each feature adds its commands to a single command registry (name, short
    description, handler), and `/help` lists whatever is registered. Unknown commands and other
    validation errors show a short `[red]` hint on the line **just above the input box** (not at
    the top of the screen) and never crash the loop.
  - **How the box is built** (no extra packages): a Spectre `Layout` that fills the terminal,
    with the day view on top, the hint line, and a 3-line bordered input panel at the bottom; the
    empty input line shows a grey `Type /help for commands` placeholder after the prompt, and a
    line wider than the box shows its tail behind a grey leading `…`.
    After each command, clear the screen and redraw the whole layout. Input is read by our own
    small line editor, using `console.Cursor.SetPosition` and
    `console.Input.ReadKey(intercept: true)`: typing, Backspace, Enter, Esc (close the popup if
    it is open, otherwise clear the line), Tab (complete a `/command` from the registry; if
    several match, extend to their common prefix and list them in the popup with a grey count
    footer) and ↑/↓ (history). Don't use Spectre's `Live` display: it can't run while we read input.
  - **Command popup:** while the line starts with `/`, a popup just above the input box lists the
    registered commands with their descriptions, filtered by what was typed and ranked by
    Levenshtein distance so typos still match. The popup is the one thing redrawn **while
    typing**: the line editor redraws only the popup's rows and the input line in place (via
    `Cursor.SetPosition`), never the whole layout. Everything else redraws only after a command.
  - `/quit` ends the loop with exit code 0.
  - Tests drive the loop by queuing input on `TestConsole.Input` (`PushTextWithEnter`, `PushKey`)
    and always end with `/quit`, so they can't hang.
- **Data:** stored as JSON, **one file per day**, in the user's app-data folder:
  `Environment.GetFolderPath(SpecialFolder.ApplicationData)/Times/yyyy-MM-dd.json`
  (`%APPDATA%\Times\` on Windows, `~/.config/Times/` on macOS/Linux). Setting the `TIMES_DATA_DIR`
  environment variable overrides that default; it must be an absolute path (used as given, no `~`
  expansion or trimming), otherwise Times refuses to start with exit code 2. Unset or empty means the
  default. `DataFolder.Resolve` picks the folder and `/help` shows it. The data folder is injected,
  so tests use a temp folder and never touch the real app data. Use `System.Text.Json`.
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
- **Git:** the main branch is `main`. Factory work happens on `factory/<job-id>` branches and is merged with `--no-ff`.
  `.gitignore` is the standard `dotnet new gitignore`.
