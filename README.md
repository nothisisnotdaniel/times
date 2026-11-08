# Times

Times is a terminal app for tracking the time you spend on projects. You check in, check out,
book breaks and add checkpoints that are later transferred into Azure DevOps work items. It runs
as an interactive session: the current day sits above an input box where you type commands
(type `help` to list them, `quit` to leave). A leading `/` is still accepted, so typing one in
front of a command does the same as the command on its own.

The first part, [Using Times](#using-times), is for everyone who installs and uses the `tm`
command. The second part, [Building Times](#building-times), is for people who work on Times
itself.

# Using Times

The [user manual](MANUAL.md) explains every command, setting and feature of Times in one place.

## Install as a .NET tool

You can install Times once as a .NET global tool and then start it from any terminal.
It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

The command is **`tm`**, not `times`: `times` is a built-in command of bash and zsh, so the shell
would run its own `times` and never reach the app.

If `tm` is not found after installing, make sure the .NET tools folder (`~/.dotnet/tools` on
macOS/Linux, `%USERPROFILE%\.dotnet\tools` on Windows) is on your `PATH`.

### Install from a GitHub release

Each release on GitHub has the package `Times.<version>.nupkg` attached. You install it from a
folder on your machine; nothing else needs to be downloaded or built.

1. Download `Times.<version>.nupkg` from the release into an empty folder, for example
   `~/Downloads/times`.

2. In that same folder, create a file named `nuget.config` with this content:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <clear />
       <add key="times-download" value="." />
     </packageSources>
   </configuration>
   ```

   `<clear />` removes every other package source, and `value="."` means the folder that holds
   this `nuget.config`, so it does not matter which folder your terminal is in.

3. Install the tool with that file, pinning the version you downloaded (`<folder>` is the folder
   from step 1):

   ```sh
   dotnet tool install --global --configfile <folder>/nuget.config --version <version> Times
   ```

   For example, with `Times.1.2.3.nupkg` in `~/Downloads/times`:

   ```sh
   dotnet tool install --global --configfile ~/Downloads/times/nuget.config --version 1.2.3 Times
   ```

   On Windows, use a path like `%USERPROFILE%\Downloads\times\nuget.config`.

4. Check the install. This prints the installed version, for example `Times 1.2.3`:

   ```sh
   tm --version
   ```

5. Start Times from any terminal:

   ```sh
   tm
   ```

This is safe because NuGet only looks in your download folder: with `<clear />` and that one
source, a package with the same name `Times` on nuget.org or any other feed is never used. If the
folder holds no matching file, the install fails instead.

NuGet may reuse a `Times` package of the same version that it cached earlier in its global
packages folder. If an install looks wrong, clear that cache with
`dotnet nuget locals global-packages --clear` and install again.

### Update

Download the newer `Times.<new version>.nupkg` into the same folder (next to `nuget.config`),
then run:

```sh
dotnet tool update --global --configfile <folder>/nuget.config --version <new version> Times
```

Times can also do this for you: see [Updates](#updates).

### Uninstall

```sh
dotnet tool uninstall -g Times
```

Uninstalling removes only the tool. Your data stays in the data folder that `help` shows.

## One-shot commands

Give Times a command and it runs that one command against today, prints plain lines and exits, without opening the screen:

```
times arrive 8:15
times leave
times status
```

`status` prints today's day view. Several words are joined, so `times checkpoint 8:30 #1 Design review` works. Commands that need the session (`quit`, `settings`, `prev`, `next`, `today`, `update`, `import`, `devops connect`) are refused with a red line. Exit codes: 0 done, 1 command error, 2 bad data folder, 3 another Times is open on the data folder, 4 the lock could not be taken. With no arguments, or an argument starting with `-`, Times starts the interactive session.

## Commands

Every command has a short alias that does exactly the same, with the same arguments: `a 8:15`
books the same arrival as `arrive 8:15`. `help` lists every command with its alias, and the
command popup shows the alias next to each name (typing an alias also finds its command there).
Tab completes full command names only.

Times are written as `H:mm` (`8:15`, `13:05`). An argument in `[…]` is optional; one in `<…>` is
required. A booking without a time uses the current time. `remove`, `change`, `booked` and
`unbooked` work on the day you are looking at, and take the entry's number from the day view.

| Command | Alias | Arguments | What it does |
|---------|-------|-----------|--------------|
| `arrive` | `a` | `[H:mm]` | Book your arrival: now, or at the given time |
| `leave` | `l` | `[H:mm]` | Book your leaving: now, or at the given time |
| `break` | `b` | `[H:mm]` | Book the start of a break: now, or at the given time |
| `breakend` | `be` | `[H:mm]` | Book the end of a break: now, or at the given time |
| `checkpoint` | `cp` | `[H:mm] [#project] <name>` | Book a named checkpoint; `#project` is the project's number on the quicklist |
| `remove` | `r` | `<number>` | Remove an entry from this day by its number |
| `change` | `c` | `<number> <H:mm>` | Change the time of an entry on this day |
| `booked` | `bk` | `<number>` | Mark a checkpoint on this day as booked in Azure DevOps |
| `unbooked` | `ub` | `<number>` | Mark a checkpoint on this day as not yet booked |
| `summary` | `s` | none | Show the time taken per work item from this day's checkpoints |
| `absence` | `ab` | `vacation`, `sick`, `holiday` or `clear` | Mark this day as vacation, sick or public holiday (it counts as meeting the daily target), or clear it |
| `correction` | `co` | `+H:mm` or `-H:mm` and a reason, or `remove <number>` | Book a manual correction to the flexitime balance on this day (for example a starting balance or paid-out overtime), or remove one by its number; it shows above the target line and counts in the balance, not in the day |
| `report` | `rp` | `<from> <to>` (`yyyy-MM-dd`) | Show the checkpoint hours per work item over a date range (both days included), with a total, to copy into Azure DevOps |
| `export` | `ex` | `<from> <to> <folder>` (`yyyy-MM-dd`, an existing absolute folder, `~` allowed) | Save the same report rows as `times-report-<from>-to-<to>.csv` (UTF-8, comma-separated, decimal hours) in the folder; an existing file is never overwritten |
| `find` | `f` | `<text>` | Find every day whose checkpoint names or quicklist project titles contain the text (ignoring case), with the date and the time taken |
| `statistics` | `st` | `<from> <to>` (`yyyy-MM-dd`) | Show per weekday (Monday to Sunday) how many days you worked and your average arrival, leaving and work time over a date range (both days included), plus an `All days` row; reads only |
| `backup` | `bu` | `<folder>` (an existing absolute folder outside the data folder, `~` allowed) | Zip every day file and `projects.json`, unchanged, into `times-backup-yyyy-MM-dd-HHmm.zip` in the folder; an existing file is never overwritten and the data folder is never changed |
| `import` | `im` | `<folder>` (an existing absolute folder outside the data folder, `~` allowed) | Import the old tool's day files (`yyyy-MM-dd.json` with `Recordings`) from the folder: new days are written, days already imported are left alone, and a day that differs asks before it is replaced (Esc keeps the rest); broken files, `UsedTime` mismatches and other names are listed; the folder is never changed |
| `vacation` | `va` | none | Show this calendar year's vacation allowance (setting `vacation-days`, default 30) with the vacation days taken (up to today), planned (after today) and left; each day marked `absence vacation` counts as one day; reads only |
| `sync` | `sy` | `<from> <to> [send]` (`yyyy-MM-dd`) | Preview the unbooked checkpoint hours per work item over a date range; with `send`, add them to each work item's Completed Work in Azure DevOps and mark those checkpoints booked (see [Azure DevOps](#azure-devops)) |
| `devops` | `dv` | none, `connect <organization>` or `disconnect` | Show the Azure DevOps connection, connect (asks for the token in a masked prompt), or remove the stored token |
| `prev` | `p` | none | Go to the previous day |
| `next` | `n` | none | Go to the next day |
| `today` | `t` | none | Go back to today |
| `addproject` | `ap` | `<work item> <title>` | Add a project to the quicklist |
| `projects` | `pr` | none | List the projects on the quicklist |
| `renameproject` | `rn` | `<number> <title>` | Give a project on the quicklist a new title; its work item number and position stay |
| `removeproject` | `xp` | `<number>` | Remove a project from the quicklist; later projects move up one number, and booked checkpoints keep their work item |
| `moveproject` | `mp` | `<number> <new number>` | Move a project to another number on the quicklist; the projects in between shift by one |
| `settings` | `se` | none | Open the settings panel (see [Settings](#settings)) |
| `update` | `up` | none | Check for a newer Times now and install it when you quit (see [Updates](#updates)) |
| `help` | `h` | none | List all commands, the running version and the data folder |
| `quit` | `q` | none | Exit Times |

### Settings

`settings` (or `se`) opens a panel in place of the quicklist and the day view with every setting
Times knows, its value and what it does. Move with `↑` / `↓`, change the selected setting with
`Enter` or `Space`, and close the panel with `Esc`. An on/off setting flips, a setting with
options moves to the next one, and a number setting asks for the new value in the input box
(`Esc` there cancels). Every change is saved at once to `settings.json` in the data folder that
`help` shows; nothing is written until you change something. If `settings.json` cannot be read, the
panel says why, shows the defaults and saves nothing until the file is fixed or deleted.

| Setting | Kind | Default | What it does |
|---------|------|---------|--------------|
| `daily-target` | number, 0 to 1440 | 480 | Your daily work target in minutes (480 = 8:00). The `left` column, the `day` value and the flexitime `balance` use it, for every day. |
| `vacation-days` | number, 0 to 366 | 30 | Your vacation days per calendar year. `vacation` shows them with the days taken, planned and left. |
| `update-mode` | `notify only`, `install on quit`, `install and restart` | `notify only` | What Times does when it finds a newer version at start (see [Updates](#updates)). |
| `update-channel` | `stable`, `beta`, `develop` | `stable` | Which releases count as updates: `stable` takes proper releases only, `beta` also `-beta` versions, `develop` also `-develop` builds. |

### Azure DevOps

`sync` sends the hours of the checkpoints you have not yet booked straight into the **Completed
Work** field of their work items, so you no longer copy numbers from `report` and run `booked` by
hand.

1. In Azure DevOps, open *User settings → Personal access tokens* and create a token with the scope
   **Work Items: Read & write** (nothing else is needed).
2. In Times, type `devops connect <organization>`, where `<organization>` is the name in
   `https://dev.azure.com/<organization>`. Times asks for the token in the input box; the prompt is
   masked (`•`), `Esc` cancels. Times keeps the token only in the **macOS Keychain** (a generic
   password named `Times Azure DevOps`) or the **Windows Credential Manager** (target
   `Times/AzureDevOps`); it is never written to a Times file and never shown.
3. `devops` shows the organization and `token stored`; `devops disconnect` removes the token.
4. `sync 2026-11-01 2026-11-30` shows what would be sent: one row per work item with its hours and
   the number of checkpoints. Nothing is sent and nothing is written.
5. `sync 2026-11-01 2026-11-30 send` adds those hours to each work item's Completed Work and marks
   the checkpoints booked. A work item that fails (for example a wrong token or a work item someone
   changed at the same moment) keeps its checkpoints unbooked; the others are still sent. Running
   `send` again sends nothing twice: only unbooked checkpoints count.

Only checkpoints with a work item, an end (a later checkpoint, break or leaving) and at least one
minute are sent; a checkpoint that is still running is left for later.

On macOS the Keychain item's access list trusts **only the launcher that started Times**: the
`tm` command that `dotnet tool install` puts in `~/.dotnet/tools` (or, for a build, the `Times`
executable next to `Times.dll`), with symlinks followed. macOS checks that launcher, not the
`Times.dll` it loads, so a program running as you that replaced the files in Times' tool folder
could still read the token. The shared `dotnet` host is never trusted (also not under another
name), so other .NET programs cannot read the token without macOS asking you. When Times runs
through such a host (for example `dotnet Times.dll`), `devops connect` says so before it asks for
the token, and stores nothing; start it with `tm`. Connecting again first prepares the new item's
access list and only then replaces the old token, so a failure there keeps the old one.
After a Times update the launcher is a new file, so macOS may ask once for permission to read the
item; choose *Always Allow*, or run `devops connect` again. (The newer data-protection keychain
would need a signed app with a keychain entitlement, which a .NET tool does not have.)

A `devops` line with anything after it (such as `devops connect contoso`) is never kept in the
input history, so ↑ cannot bring back a token typed on that line by mistake.

**Linux is not supported** yet: there is no credential
store Times can use there, so `devops connect` and `sync … send` say so (the preview still works).

To check the stores by hand: connect, look for the item in *Keychain Access* (macOS) or with
`cmdkey /list:Times/AzureDevOps` (Windows), run `devops`, then `devops disconnect` and check it is gone.

### Updates

At start, Times looks in the GitHub releases of this repository for a version above the running one
on your `update-channel`. It never blocks you while it looks; the result shows on the line above the
input box. Offline, rate-limited or any other failed check only says `Update check skipped: …` in grey.

- `notify only`: Times says `Times X is available. Type update to install it when you quit.`
- `install on quit`: Times downloads the new package, checks its signature and installs it after
  you `quit`.
- `install and restart`: the same, and at `quit` it says when to start `tm` again (Times does not
  start itself).

`update` (`up`) does the check and download right away and installs after `quit`, in every mode.
A package is only installed if its ECDSA signature, made by the release workflow, verifies with the
public key built into Times. The install runs after Times has exited, as `dotnet tool update`, and
writes its output to `update.log` in the data folder; the next start says `Updated to X.` or that
it failed. This works only for Times installed as a dotnet tool (`-g` or `--tool-path`).

## Keys

| Keys | What they do |
|------|--------------|
| `PgUp` / `PgDn` | Scroll the area above the input box up / down by a page |
| `Shift+PgUp` / `Shift+PgDn` | The same |
| `Ctrl+↑` / `Ctrl+↓` | The same |
| `↑` / `↓` | Previous / next command from the history (they do not scroll) |

macOS Terminal keeps plain PgUp/PgDn for its own scrollback, so there use Shift+PgUp/PgDn or
Ctrl+↑/↓ instead.

## Mouse

The mouse wheel scrolls the area above the input box, 3 lines per notch, wherever the pointer is
(the keys under [Keys](#keys) scroll by a page).

To make that work, Times turns on the terminal's mouse reporting while it runs. Because of that,
dragging with the mouse no longer selects text: while Times runs, select text with **Shift-drag**
(**Option-drag** in macOS Terminal). The terminal's normal mouse behaviour comes back when Times
exits, also after Ctrl+C.

Times also turns on bracketed paste: pasted text lands in the input line for you to check and edit,
with each line break and Tab turned into a space, and only runs when you press Enter. An unusually
long or garbled `ESC [` sequence from the terminal is dropped whole, never typed into the line.

## The day view

The screen shows one day at a time, starting at today. Under a heading with the weekday and date
(`Tuesday, 2026-10-27`), each entry of that day has its own row, in time order:

- its number (1, 2, 3, …), which `remove`, `change`, `booked` and `unbooked` use;
- a two-letter code: `AR` arrival, `LE` leaving, `BS` break start, `BE` break end, `CP` checkpoint;
- its time, and for a checkpoint its name and work item (`#5678`), if it has one; a checkpoint
  marked with `booked` is shown in grey and ends with a `✓`;
- the work time left at that entry, counted down from your daily target (8 hours unless you change
  `daily-target` in the settings; it goes negative once you work longer).

Below the entries, a `Breaks` line shows the break you have taken and the break required for the
time you worked (none up to 6 hours, 0:30 up to 9 hours, 0:45 above). If you took too little, the
line ends with a yellow `! Breaks too short` and the minutes missing. Below it, a target line such
as `Target 8:00 · day -0:30 · balance +1:15` shows your daily target, how far this day is over (`+`)
or under (`-`) it, and your flexitime balance: the sum of the same difference over every booked day
with an arrival, up to and including the shown day. Today counts with the work booked so far. Day
files that cannot be read are left out, and the line then ends with a yellow `! 1 day file skipped`.
A day with nothing booked shows `No entries yet.`, followed by the target line without the `day`
part once any earlier day counts.

Above the day, the `Projects` panel shows your quicklist: each project's number, its work item and
its title, at most 5 lines (a longer list ends with `… N more`; `projects` lists them all). It is
left out while the output of `help` or `projects` is shown.
`renameproject`, `removeproject` and `moveproject` keep the list tidy; they change only
`projects.json`, never a day file, so a removed project's booked checkpoints keep their work item.

`prev` and `next` move one day back or forward, and `today` goes back to today. Looking at a day
never creates a file: a day's file is written only when you book the first entry on it.

## Your data

Times keeps one JSON file per day (`yyyy-MM-dd.json`) in its data folder:

- `~/.config/Times/` on macOS and Linux;
- `%APPDATA%\Times\` on Windows.

The project quicklist lives in the same folder, in `projects.json`, and your settings in
`settings.json`. `help` shows which folder is in use.

To use another folder, set the `TIMES_DATA_DIR` environment variable to it. The path must be
absolute and is used exactly as given (no `~` expansion); if it is not absolute, or starts or ends
with whitespace, Times does not start and exits with code 2. Unset or empty means the default
folder.

## License

Times is source-available under the [Functional Source License, Version 1.1, ALv2 Future
License](LICENSE.md) (FSL-1.1-ALv2). You may use, change and share it for any purpose except
offering a competing commercial product or service. Each release becomes available under the
Apache License 2.0 two years after it is published.

Contributions are accepted under the same license.

# Building Times

## Prerequisites

The [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Build and test

From the repo root:

```sh
dotnet build
dotnet test
```

`dotnet build` must stay at 0 warnings; it also enforces the code style in `.editorconfig`, so a
style break shows up as a warning. `dotnet test` runs the xUnit tests.

## Run from source

```sh
dotnet run --project src/Times
```

## Install your own build

To try your own changes as the installed `tm`, pack and install them from the repo root.

The repo root holds a `nuget.config` whose `packageSourceMapping` maps the `Times` package to
`./nupkg` only, so a `Times` package on nuget.org is never used. All other packages still come
from nuget.org, and `<clear />` keeps your user- and machine-level feeds out. NuGet only reads
this file when you run the commands from the repo root (where `nuget.config` lives); run the
install and update commands from there. `./nupkg` is resolved relative to `nuget.config`, not to
your shell's working directory.

1. Build the package (from the repo root). This writes `nupkg/Times.<version>.nupkg`:

   ```sh
   dotnet pack src/Times -c Release
   ```

2. Install it from the local folder (from the repo root):

   ```sh
   dotnet tool install -g Times
   ```

   If `nupkg/` holds no `Times` package, the install fails instead of falling back to
   nuget.org, so always build first.

To update after changing the code, raise `<Version>` in `src/Times/Times.csproj` (with the same
version, .NET may reuse the build it already cached instead of your new one), build the package
again, then update, again from the repo root:

```sh
dotnet pack src/Times -c Release
dotnet tool update -g Times
```

To remove the tool, see [Uninstall](#uninstall).

## Project layout

- `Times.slnx`: the solution.
- `src/Times/`: the app. `Program.cs` only wires things up; the logic starts in `App.cs`.
- `tests/Times.Tests/`: the tests, one test class per production class.
- `.github/`: the release workflow (`workflows/release.yml`) and its scripts.

## Releasing

Releases are built by GitHub Actions (`.github/workflows/release.yml`); nobody edits `<Version>`
for a release.

**Develop builds.** Every push to `develop` makes a GitHub prerelease for testers, versioned
`X.Y.(Z+1)-develop.<run>`: one patch above the highest release tag, plus the workflow run number.
After `v0.1.0` (or `v0.1.0-beta`), run 12 gives `0.1.1-develop.12`, tagged `v0.1.1-develop.12`,
with `Times.0.1.1-develop.12.nupkg` attached. These sort above the latest release and below the
next one, so nobody tags them by hand. A push that changes only `backlog.md` makes no prerelease.

**Releases.** Open a pull request from `develop` into `main` titled `release v1.2.3` and merge it:

```sh
gh pr create --base main --head develop --title "release v1.2.3"
```

When the pull request is merged, the workflow runs the tests on the merge commit, packs Times with
version `1.2.3` (taken from the title, without `release v`), installs the package once to check that
`tm --version` prints `Times 1.2.3`, and creates the GitHub release `v1.2.3` (and its tag) on the merge
commit, with `Times.1.2.3.nupkg` attached and generated notes. A title with a pre-release part, such
as `release v1.2.3-beta.1`, makes a pre-release.
After a proper release `v1.2.3`, the workflow deletes older `-develop` prereleases and their tags:
every `vX.Y.Z-develop.<n>` below `1.2.3` (including `v1.2.3-develop.<n>`). It keeps proper releases,
other prereleases such as betas, and higher prereleases such as `v1.2.4-develop.9`. This is
housekeeping: a failed deletion only shows a warning and does not fail the release.
The title must be exactly `release vX.Y.Z` (lowercase `release`, one space, `v` and a SemVer version,
nothing before or after). A title whose tag already exists, or with a `-develop` version, fails and
no release is made. A pull request that is closed without merging, or one from another branch or a
fork, releases nothing.

**Releasing from a tag.** As an alternative, merge `develop` into `main`, tag the merge on `main`
with `v` plus a SemVer version and push both:

```sh
git checkout main
git merge --no-ff develop
git tag v1.2.3
git push origin main
git push origin v1.2.3
```

The workflow then does the same with version `1.2.3` taken from the tag (without the `v`), and
creates the release `v1.2.3` on the tagged commit. A tag with a pre-release part, such as
`v1.2.3-beta.1`, makes a pre-release.
A release tag whose commit is not on `main` fails before anything is built, and so does a
hand-pushed `-develop` tag (only develop builds make those).
These checks live in the workflow file of the tagged commit, so they catch mistakes, not someone
who can push and edit `release.yml`; to enforce them, add a GitHub tag ruleset for `v*` and branch
protection on `main`.
A tag that is not SemVer (`v1.2`, `v1.2.3.4`, `v01.2.3`) fails, and no release is made.
`help` shows the running version, and `<Version>` in `src/Times/Times.csproj` stays the default
for local builds.
