# Times user manual

This manual is for people who use Times. It explains every command, every setting, every
feature, the keys, the data folder and the files Times keeps there. For installing Times, see
[Install as a .NET tool](README.md#install-as-a-net-tool) in the README.

## Getting started

Times is a terminal app for tracking the time you spend on projects. You book your arrival, your
breaks and your leaving, and you add checkpoints that say what you worked on. Later you can send
those hours to Azure DevOps work items.

1. Start Times with `tm`. (The command is `tm`, not `times`: `times` is a built-in command of
   bash and zsh.) `tm --version` prints the installed version and exits.
2. Times stays open until you type `quit` (or `q`). It shows today's day above an input box at
   the bottom of the screen; you type commands there and press `Enter`.
3. Type `help` (or `h`) to list every command with its alias, the running version and the data
   folder in use.

A typical day:

```text
arrive 8:15
checkpoint #1 Planning
break 12:00
breakend 12:30
checkpoint #2 Review
leave
```

`#1` and `#2` are project numbers on your quicklist: add projects first with `addproject` (or
`devops import`), or leave the `#` part out.

Commands are plain words. Every command also has a short alias of one or two letters that does
exactly the same, with the same arguments: `a 8:15` books the same arrival as `arrive 8:15`. One
leading `/` is accepted and ignored, so `/arrive 8:15` works too (two are an unknown command).

Times are written as `H:mm` (`8:15`, `13:05`). Wherever a booking takes a time, you can also
write a relative time:

- `now`: the current time;
- `-<n>m`: `n` minutes ago, for example `-10m`;
- `+<n>m`: in `n` minutes, for example `+5m`.

`n` has 1 to 4 digits, and the words are exact and lower case. A relative time that would land
before 0:00 or after 23:59 is refused; it never wraps to another day. The day file always keeps
the plain time.

If something you typed is wrong, Times shows a short red hint on the line just above the input
box and changes nothing.

## The screen

From top to bottom, the screen shows:

- **Projects:** your project quicklist, in a panel with a grey border: each project's number,
  its work item (`#1234`) and its title, at most 5 lines (a longer list ends with `… N more`). It
  is left out while the output of `help` or `projects` is shown.
- **The day view:** a heading with the weekday and date (`Tuesday, 2026-10-27`), then one row
  per entry of that day in time order: its number (which `remove`, `change`, `booked` and
  `unbooked` use), a two-letter code (`AR` arrival, `LE` leaving, `BS` break start, `BE` break
  end, `CP` checkpoint), its time, for a checkpoint its name and work item, and the work time
  `left` until your daily target. A checkpoint marked booked is grey and ends with `✓`. While you
  are working today, a grey `now` row shows the current time and the time left; it updates every
  minute by itself.
- Below the entries: the `Breaks` line, any correction rows, and the target line, for example
  `Target 8:00 · day -0:30 · balance +1:15`. Warnings are yellow and start with `!`.
- **Command output:** the result of the last command, such as a report or the help list, below
  the day view.
- **The hint rows:** one to three lines just above the input box for red hints, update notes and
  alerts.
- **The input box:** a bordered line where you type. An empty line shows the grey placeholder
  `Type help for commands`. A line wider than the box shows its end behind a grey `…`.

When everything does not fit, the area above the input box scrolls: grey lines such as
`↑ 4 more lines above · PgUp / Ctrl+↑` and `↓ 2 more lines below · PgDn / Ctrl+↓` show what is
hidden. After every command the screen is drawn again from the top.

While you type a command, a popup just above the input box lists the commands that match, with
their alias and description. It also finds commands when you mistype them a little, and when you
type an alias. A lone `/` lists every command.

## Commands

An argument in `<…>` is required; one in `[…]` is optional. `remove`, `change`, `booked`,
`unbooked`, `absence` and `correction` work on the day you are looking at, and take an entry's
number from the day view. Dates are written as `yyyy-MM-dd` (`2026-11-01`); a date range
includes both days.

### `arrive` (`a`)

Arguments: an optional time: `H:mm`, `now`, `-<n>m` or `+<n>m`; without one, the current time.

Books your arrival on the shown day.

```text
arrive
arrive 8:15
a -10m
```

### `leave` (`l`)

Arguments: an optional time: `H:mm`, `now`, `-<n>m` or `+<n>m`; without one, the current time.

Books your leaving on the shown day.

```text
leave
leave 17:30
l +5m
```

### `break` (`b`)

Arguments: an optional time: `H:mm`, `now`, `-<n>m` or `+<n>m`; without one, the current time.

Books the start of a break.

```text
break
break 12:00
b -5m
```

### `breakend` (`be`)

Arguments: an optional time: `H:mm`, `now`, `-<n>m` or `+<n>m`; without one, the current time.

Books the end of a break.

```text
breakend
breakend 12:30
be now
```

### `checkpoint` (`cp`)

Arguments: `[time] [#project] <name>`: an optional time (`H:mm`, `now`, `-<n>m` or `+<n>m`), an
optional project number from the quicklist with a leading `#`, and the checkpoint's name.

Books a named checkpoint: from this moment on you work on that. With `#project`, the checkpoint
gets that project's work item, so its time counts for the work item in `summary`, `report`,
`export` and `sync`. The time until the next entry is the checkpoint's time. A name is at most
200 characters and must not hold control or invisible characters.

```text
checkpoint Daily stand-up
checkpoint 9:30 #2 Code review
cp -15m #1 Planning
```

### `remove` (`r`)

Arguments: `<number>`, the entry's number in the day view.

Removes an entry from the shown day.

```text
remove 3
r 1
```

### `change` (`c`)

Arguments: `<number> <time>`: the entry's number and its new time (`H:mm`, `now`, `-<n>m` or
`+<n>m`).

Changes the time of an entry on the shown day.

```text
change 1 8:05
c 4 -10m
```

### `booked` (`bk`)

Arguments: `<number>`, the checkpoint's number in the day view.

Marks a checkpoint on the shown day as booked in Azure DevOps. It turns grey with a `✓`, and
`sync` leaves it out.

```text
booked 2
bk 5
```

### `unbooked` (`ub`)

Arguments: `<number>`, the checkpoint's number in the day view.

Marks a checkpoint on the shown day as not yet booked again.

```text
unbooked 2
ub 5
```

### `summary` (`s`)

Arguments: none.

Shows the time taken per work item on the shown day, worked out from its checkpoints.

```text
summary
s
```

### `absence` (`ab`)

Arguments: `vacation`, `sick`, `holiday` or `clear`.

Marks the shown day as a vacation day, a sick day or a public holiday, or clears the mark. An
absence day counts as meeting the daily target. A day that already has entries cannot be marked;
remove the entries first. Bookings are refused on an absence day.

```text
absence vacation
absence sick
ab holiday
ab clear
```

### `correction` (`co`)

Arguments: `+H:mm <reason>` or `-H:mm <reason>`, or `remove <number>`.

Books a manual correction to your flexitime balance on the shown day, for example a starting
balance or paid-out overtime, or removes one by its number. The amount has 1 to 3 hour digits
and must not be zero; the reason follows the same rules as a checkpoint name.

```text
correction +12:30 Balance from the old tool
correction -4:00 Overtime paid out
co remove 1
```

### `report` (`rp`)

Arguments: `<from> <to>`, two dates as `yyyy-MM-dd`.

Shows the checkpoint hours per work item over a date range, with `H:mm`, decimal hours and a
`Total` row, ready to copy into Azure DevOps. Reads only.

```text
report 2026-11-01 2026-11-30
rp 2026-11-02 2026-11-06
```

### `export` (`ex`)

Arguments: `<from> <to> <folder>`: two dates as `yyyy-MM-dd` and an existing absolute folder
(`~` allowed, one pair of quotes around it is removed).

Saves the same rows as `report` as a CSV file named `times-report-<from>-to-<to>.csv` in the
folder. An existing file is never overwritten.

```text
export 2026-11-01 2026-11-30 ~/Documents
ex 2026-11-01 2026-11-30 "~/My Reports"
```

### `find` (`f`)

Arguments: `<text>`.

Finds every day whose checkpoint names or quicklist project titles contain the text, ignoring
case, with the date, the work item, the title and the time taken.

```text
find review
f planning
```

### `statistics` (`st`)

Arguments: `<from> <to>`, two dates as `yyyy-MM-dd`.

Shows per weekday how many days you worked, and your average arrival, leaving and work time over
the range, plus an `All days` row. Reads only.

```text
statistics 2026-01-01 2026-06-30
st 2026-10-01 2026-10-31
```

### `backup` (`bu`)

Arguments: `<folder>`, an existing absolute folder outside the data folder (`~` allowed).

Zips every day file and `projects.json`, unchanged, into `times-backup-yyyy-MM-dd-HHmm.zip` in
the folder.

```text
backup ~/Backups
bu /Volumes/USB/times
```

### `import` (`im`)

Arguments: `<folder>`, an existing absolute folder outside the data folder (`~` allowed).

Imports the day files of the old time-tracking tool from the folder.

```text
import ~/OldTimes
im ~/Downloads/old-data
```

### `vacation` (`va`)

Arguments: none.

Shows this calendar year's vacation allowance with the days taken (up to today), planned (after
today) and left. Reads only.

```text
vacation
va
```

### `devops` (`dv`)

Arguments: none, `connect <organization>`, `import` or `disconnect`.

Shows the Azure DevOps connection, connects (asks for your token in a masked prompt), adds your
open work items to the quicklist, or removes the stored token.

```text
devops
devops connect contoso
dv import
dv disconnect
```

### `sync` (`sy`)

Arguments: `<from> <to> [send]`: two dates as `yyyy-MM-dd`, and `send` to really send.

Without `send`, previews the unbooked checkpoint hours per work item over the range. With `send`,
adds them to each work item's Completed Work in Azure DevOps and marks those checkpoints booked.

```text
sync 2026-11-01 2026-11-30
sy 2026-11-01 2026-11-30 send
```

### `prev` (`p`)

Arguments: none.

Goes to the previous day.

```text
prev
p
```

### `next` (`n`)

Arguments: none.

Goes to the next day.

```text
next
n
```

### `today` (`t`)

Arguments: none.

Goes back to today.

```text
today
t
```

### `addproject` (`ap`)

Arguments: `<work item> <title>`: the work item number and the project's title.

Adds a project to the end of the quicklist. A work item can be on the list only once.

```text
addproject 1234 Website relaunch
ap 5678 Support
```

### `projects` (`pr`)

Arguments: none.

Lists every project on the quicklist with its number, work item and title.

```text
projects
pr
```

### `renameproject` (`rn`)

Arguments: `<number> <title>`: the project's number on the quicklist and its new title.

Gives a project a new title; its work item and number stay.

```text
renameproject 2 Website relaunch phase 2
rn 1 Support and maintenance
```

### `removeproject` (`xp`)

Arguments: `<number>`, the project's number on the quicklist.

Removes a project from the quicklist. Later projects move up one number. Checkpoints already
booked keep their work item.

```text
removeproject 3
xp 1
```

### `moveproject` (`mp`)

Arguments: `<number> <new number>`.

Moves a project to another number on the quicklist; the projects in between shift by one.

```text
moveproject 4 1
mp 1 3
```

### `settings` (`se`)

Arguments: none.

Opens the settings panel (see [Settings](#settings)).

```text
settings
se
```

### `update` (`up`)

Arguments: none.

Checks for a newer Times right away, downloads and verifies it, and installs it when you quit
(see [Updates](#updates)).

```text
update
up
```

### `help` (`h`)

Arguments: none.

Lists every command with its alias and description, the running version and the data folder.

```text
help
h
```

### `quit` (`q`)

Arguments: none.

Exits Times. Everything is already saved.

```text
quit
q
```

## Settings

`settings` (or `se`) opens a panel in place of the quicklist and the day view with every setting,
its value and what it does. Move with `↑` / `↓`, change the selected setting with `Enter` or
`Space`, and close the panel with `Esc`. An on/off setting flips, a setting with options moves to
the next option, and a number setting asks for the new value in the input box (`Esc` there
cancels). Every change is saved at once to `settings.json`; nothing is written until you change
something. If `settings.json` cannot be read, the panel says so, shows the defaults and saves
nothing until the file is fixed or deleted. A value that is missing, broken or out of range counts
as the default.

### `daily-target`

Values: a whole number of minutes from `0` to `1440`.

Default: `480` (8:00).

Your daily work target in minutes. The `left` column, the `day` value of the target line, the
flexitime `balance` and the `alert-target` alert use it, for every day.

### `alert-target`

Values: `on` or `off`.

Default: `on`.

While you are working today, Times rings the terminal bell once and shows
`Daily target H:mm reached.` when your work reaches the daily target (if the target is above 0).

### `alert-limit`

Values: `on` or `off`.

Default: `on`.

While you are working today, Times rings the terminal bell once and shows
`! 15 minutes left until 10 hours of work.` when your work reaches 9:45.

### `check-breaks`

Values: `on` or `off`.

Default: `on`.

Warns with `! Breaks too short` on the `Breaks` line when your breaks are shorter than the law
requires: none up to 6 hours of work, 0:30 up to 9 hours, 0:45 above.

### `check-max-work`

Values: `on` or `off`.

Default: `on`.

Warns when a day has more than 10 hours of work.

### `check-rest`

Values: `on` or `off`.

Default: `on`.

Warns when the rest between the previous day's leaving and this day's arrival is under 11 hours.

### `holiday-country`

Values: `none` or `Germany`.

Default: `none`.

The country whose public holidays Times works out. With `Germany`, an empty day on a public
holiday shows as `Absence: Public holiday · <name>` and counts as meeting the target, and a booked
day on a holiday gets a grey `Public holiday: <name>` line. Nothing is written for a holiday.

### `holiday-state`

Values: `none`, `BB`, `BE`, `BW`, `BY`, `HB`, `HE`, `HH`, `MV`, `NI`, `NW`, `RP`, `SH`, `SL`, `SN`, `ST` or `TH`.

Default: `none`.

The German state whose own public holidays are added to the nationwide ones. `none` means the
nationwide holidays only. It has no effect while `holiday-country` is `none`.

### `vacation-days`

Values: a whole number of days from `0` to `366`.

Default: `30`.

Your vacation days per calendar year. `vacation` shows them with the days taken, planned and left.

### `report-rounding`

Values: `off`, `5 min`, `10 min` or `15 min`.

Default: `off`.

Rounds each row's time per day to the nearest step (half rounds up) in `report` and `export`
only. The caption and the export message then say `rounded to <n> min`. The day view, the balance
and `sync` always use the exact times.

### `update-mode`

Values: `notify only`, `install on quit` or `install and restart`.

Default: `notify only`.

What Times does when it finds a newer version at start: only say so, download and install it
after you quit, or the same and also say at quit when to start `tm` again (see
[Updates](#updates)).

### `update-channel`

Values: `stable`, `beta` or `develop`.

Default: `stable`.

Which releases count as updates: `stable` takes proper releases only, `beta` also `-beta`
versions, `develop` also `-develop` test builds.

## Features

### Day view and navigation

Times always shows one day, starting at today. `prev` and `next` move one day back or forward,
and `today` goes back to today. Looking at a day never creates a file: a day's file is written
only when you book the first entry on it. Bookings, `remove`, `change`, `booked`, `unbooked`,
`absence` and `correction` work on the day you are looking at. If the day's file was changed
outside Times since it was shown, these commands save nothing and ask you to check the reloaded
day and run the command again.

### Bookings and relative times

`arrive`, `leave`, `break`, `breakend` and `checkpoint` book an entry, and `change` moves one to a
new time. Each takes a time as `H:mm`, `now`, `-<n>m` or `+<n>m`; without a time, the booking
uses the current time. The day's entries are always shown in time order. `remove` deletes an
entry by its number.

### Checkpoints and the project quicklist

A checkpoint names what you work on from that moment until the next entry. Keep the projects you
book often on the quicklist with `addproject <work item> <title>`; then `checkpoint #2 <name>`
gives the checkpoint project 2's work item. `projects` lists them all; `renameproject`,
`removeproject` and `moveproject` keep the list tidy. They change only the quicklist, never a day
file, so booked checkpoints keep their work item. `summary` shows the time per work item for the
shown day. Titles and names are at most 200 characters, and invisible or control characters are
refused (emoji are fine).

### Absences and public holidays

`absence vacation`, `absence sick` and `absence holiday` mark the shown day; `absence clear`
removes the mark. The day view then shows `Absence: Vacation` (or `Sick`, `Public holiday`), and
the day counts with `day +0:00`. With the `holiday-country` and `holiday-state` settings, Times
knows Germany's public holidays itself and shows them by name, without writing anything.

### Target and flexitime balance

The target line below the entries, such as `Target 8:00 · day -0:30 · balance +1:15`, shows your
daily target (`daily-target`), how far the shown day is over (`+`) or under (`-`) it, and your
flexitime balance: the sum of that difference over every day with an arrival, up to and
including the shown day, plus every correction. Today counts with the work booked so far, and
while you are working it updates every minute. Absence days count as zero. Day files that cannot
be read are left out, and the line ends with a yellow `! N day file(s) skipped`.

### Balance corrections

`correction +H:mm <reason>` or `correction -H:mm <reason>` adds a correction to the balance on
the shown day, for example the balance you bring from another tool or overtime that was paid out.
Each correction shows as a grey `Correction <n>` row above the target line. It counts in the
balance but not in the day's `day` value. `correction remove <n>` removes one.

### Working-time warnings

With the settings `check-breaks`, `check-max-work` and `check-rest` (all on by default), the
`Breaks` line ends with yellow warnings when breaks are too short, the day has more than 10 hours
of work, or the rest since the previous day's leaving is under 11 hours. Below the day, a yellow
line such as `! Working-time warnings this week: Mon, Wed` names the days of the shown week
(Monday to Sunday) that have a warning.

### End-of-day alerts

While Times runs and you are working today, it rings the terminal bell and shows a hint once per
day when your work reaches the daily target (`alert-target`) and when 15 minutes are left until
10 hours of work (`alert-limit`). If you start Times after a threshold has passed, that alert
stays silent.

### Vacation

Mark vacation days with `absence vacation`, also in the future. `vacation` shows this calendar
year's allowance (`vacation-days`), the days taken up to today, the days planned after today and
the days left, with a yellow warning when you are over your allowance.

### Reports and export

`report <from> <to>` shows the checkpoint hours per work item (then per checkpoint name for
checkpoints without a work item) over a date range, as `H:mm` and as decimal hours, with a
`Total` row. `export <from> <to> <folder>` saves the same rows (without the total) as a CSV file
`times-report-<from>-to-<to>.csv` in an existing folder: UTF-8, comma-separated, with the columns
`Work item`, `Title` and `Hours`, ready for a spreadsheet. Export never overwrites an existing
file. The `report-rounding` setting rounds the times. Day files that cannot be read are skipped
and counted.

### Search

`find <text>` lists every day whose checkpoint names or quicklist titles contain the text,
ignoring case, one row per date and work item with the time taken.

### Statistics

`statistics <from> <to>` shows, per weekday from Monday to Sunday, how many days you worked and
your average arrival, leaving and work time, plus an `All days` row. A day counts if it has an
arrival; absence days do not. Only days with a leaving count for the leaving and the work time; a
grey `–` means none had one.

### Backup

`backup <folder>` zips every day file and `projects.json`, byte for byte, into
`times-backup-yyyy-MM-dd-HHmm.zip` in an existing folder outside the data folder. A file larger
than 64 KiB or one that cannot be read is left out and counted. An existing file of that name is
never overwritten, and the data folder is never changed. To restore, quit Times and unzip the
files into the data folder.

### Import from the old tool

`import <folder>` reads the day files of the old time-tracking tool (`yyyy-MM-dd.json` with
`Recordings`) from a folder outside the data folder. New days are written; days already imported
unchanged are left alone. When a day already holds something different, Times asks whether to
replace it: type `yes` (or `y`) to replace it, anything else to keep it, and `Esc` to keep all the
remaining ones. Broken files, files with other names and differences in the old tool's
`UsedTime` values are listed as yellow lines. The source folder is never changed.

### Azure DevOps

`sync` sends the hours of the checkpoints you have not yet booked into the **Completed Work**
field of their work items, so you no longer copy numbers from `report` and run `booked` by hand.

1. In Azure DevOps, open *User settings → Personal access tokens* and create a token with the
   scope **Work Items: Read & write**.
2. In Times, type `devops connect <organization>`, where `<organization>` is the name in
   `https://dev.azure.com/<organization>`. Times asks for the token in the input box; the prompt is
   masked (`•`), and `Esc` cancels. Never type the token on the `devops` line itself.
3. Times keeps the token only in the **macOS Keychain** (a generic password named
   `Times Azure DevOps`) or the **Windows Credential Manager** (target `Times/AzureDevOps`). It is
   never written to a file, never shown, and you never put it into a file yourself. A `devops`
   line with anything after it is never kept in the input history.
4. `devops` shows the organization and whether a token is stored; `devops disconnect` removes it.
5. `devops import` adds your open work items assigned to you to the quicklist (those already on
   it are left alone).
6. `sync <from> <to>` previews what would be sent, one row per work item; nothing is sent or
   written. `sync <from> <to> send` adds the hours and marks the checkpoints booked. A work item
   that fails keeps its checkpoints unbooked; the others are still sent. Running `send` again
   sends nothing twice.

Only checkpoints with a work item, an end (a later entry) and at least one minute are sent.

On macOS, the Keychain item trusts only the `tm` launcher that started Times. When Times runs
through the shared `dotnet` host instead, `devops connect` says so and stores nothing; start it
with `tm`. After an update, macOS may ask once for permission to read the item; choose
*Always Allow*, or run `devops connect` again. On Linux there is no supported credential store
yet, so `devops connect` and `sync … send` say so (the preview still works).

### Updates

At start, Times looks in its GitHub releases for a newer version on your `update-channel`. It
never blocks you; the result shows above the input box. A failed check (offline, for example)
only shows a grey `Update check skipped: …`. What happens next depends on `update-mode`. `update`
checks and downloads right away and installs after `quit`, in every mode. A package is installed
only if its signature verifies with the key built into Times. The install runs after Times has
exited and writes its output to `update.log` in the data folder; the next start says
`Updated to X.` or that it failed. This works only for Times installed as a .NET tool.

## Keyboard shortcuts

| Keys | What they do |
|------|--------------|
| `Enter` | Run the command on the input line |
| `Esc` | Close the command popup if it is open, otherwise clear the line; in a prompt or the settings panel, cancel or close |
| `Tab` | Complete a command name; when several match, extend to their common part and list them in the popup |
| `Backspace` | Delete the character before the cursor |
| `↑` / `↓` | Previous / next command from the history (in the settings panel: move the selection) |
| `PgUp` / `PgDn` | Scroll the area above the input box up / down by a page |
| `Shift+PgUp` / `Shift+PgDn` | The same |
| `Ctrl+↑` / `Ctrl+↓` | The same |
| `Space` | In the settings panel: change the selected setting (like `Enter`) |
| `Ctrl+C` | Leave Times at once; everything booked is already saved |

macOS Terminal keeps plain `PgUp` / `PgDn` for its own scrollback, so there use `Shift+PgUp` /
`Shift+PgDn` or `Ctrl+↑` / `Ctrl+↓`. Tab completes full command names only, never aliases.

## Mouse and paste

The mouse wheel scrolls the area above the input box, 3 lines per notch.

To make that work, Times turns on the terminal's mouse reporting while it runs. Because of that,
dragging no longer selects text: while Times runs, select text with **Shift-drag**
(**Option-drag** in macOS Terminal). The normal mouse behaviour comes back when Times exits, also
after `Ctrl+C`.

Pasted text lands in the input line for you to check and edit; it only runs when you press
`Enter`. Line breaks and tabs become spaces, and other control characters are dropped. A paste
keeps at most 4096 characters; the rest is dropped and a red hint says so.

## Data folder

Times keeps all its files in one data folder:

- macOS and Linux: `~/.config/Times/`
- Windows: `%APPDATA%\Times\`

`help` shows the folder in use. To use another folder, set the `TIMES_DATA_DIR` environment
variable to it, for example `TIMES_DATA_DIR=/Users/me/work-times tm`. The path must be absolute
and is used exactly as given (no `~` expansion). Unset or empty means the default folder. Times
creates the folder when it starts, if it is missing.

Only one Times can run per data folder. When Times cannot start, it shows one red line and exits
with one of these codes:

| Exit code | Meaning |
|-----------|---------|
| `2` | The data folder is not usable: `TIMES_DATA_DIR` is not an absolute path, or it starts or ends with whitespace |
| `3` | Another Times is already running with this data folder; close it first |
| `4` | The data folder cannot be created or written, or `times.lock` in it is not a normal file |

## Files

| File | What it holds | Can I edit or delete it? |
|------|---------------|--------------------------|
| `yyyy-MM-dd.json` | One day: its entries, absence and corrections, for example `2026-11-02.json` | Better use the commands. You can delete a day's file to clear that day; quit Times first. If you edit one by hand, keep it valid JSON; a broken file is shown as unreadable and skipped in the balance |
| `projects.json` | The project quicklist | Better use the project commands; deleting it empties the quicklist |
| `settings.json` | Your settings | Better use `settings`; deleting it brings back every default |
| `times.lock` | Marks that Times is running with this folder | Do not edit or delete it; Times removes it when it exits and takes over a leftover one |
| `update.log` | The output of the last update installs | Read it when an update failed; you can delete it |
| `update-pending.json` | Marks an update that runs after quit, so the next start can report it | Do not edit or delete it; Times deletes it at the next start |

Day files, `projects.json` and `settings.json` must be normal files of at most 64 KiB. A file
that is larger, is not a normal file or holds invalid text is shown as unreadable and never
overwritten.

## Troubleshooting

- **`tm` is not found:** make sure the .NET tools folder (`~/.dotnet/tools` on macOS/Linux,
  `%USERPROFILE%\.dotnet\tools` on Windows) is on your `PATH`.
- **Typing `times` does nothing useful:** the command is `tm`; `times` is a shell built-in.
- **"Times is already running with this data folder" (exit code 3):** another Times uses the same
  folder, perhaps in another terminal window. Close it, then start again.
- **"Times cannot start" (exit code 2 or 4):** check `TIMES_DATA_DIR` (absolute, no spaces at the
  start or end) and that you may write to the data folder.
- **"the file … changed on disk since it was shown":** the day file was changed outside this
  Times. The day is reloaded; check it and run the command again.
- **A day file, `projects.json` or `settings.json` is unreadable:** it is broken, too large or
  not a normal file. Fix it or restore it from a backup; Times never overwrites it.
- **`! N day file(s) skipped`:** some day files could not be read and are left out of the
  balance, reports and statistics.
- **I cannot select text with the mouse:** use Shift-drag (Option-drag in macOS Terminal) while
  Times runs.
- **`PgUp` does not scroll in macOS Terminal:** use `Shift+PgUp` or `Ctrl+↑`.
- **An update failed:** read `update.log` in the data folder.
- **`devops connect` says no credential store is available:** Azure DevOps works on macOS and
  Windows only for now.
