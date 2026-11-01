# Times

Times is a terminal app for tracking the time you spend on projects. You check in, check out,
book breaks and add checkpoints that are later transferred into Azure DevOps work items. It runs
as an interactive session: the current day sits above an input box where you type commands
(type `help` to list them, `quit` to leave). A leading `/` is still accepted, so typing one in
front of a command does the same as the command on its own.

It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). To run it straight from the
source tree:

```sh
dotnet run --project src/Times
```

## Install as a .NET tool

You can install Times once as a .NET global tool and then start it from any terminal.

The command is **`tm`**, not `times`: `times` is a built-in command of bash and zsh, so the shell
would run its own `times` and never reach the app.

The package is not published to nuget.org or any other feed. You build it yourself into the
local `nupkg/` folder at the repo root and install it from there.

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

   NuGet may reuse a `Times` package of the same version that it cached earlier in its global
   packages folder. If an install looks wrong, clear that cache with
   `dotnet nuget locals global-packages --clear` and install again.

3. Check the install. This prints the installed version, for example `Times 0.1.0`:

   ```sh
   tm --version
   ```

4. Start Times from any terminal:

   ```sh
   tm
   ```

If `tm` is not found after installing, make sure the .NET tools folder (`~/.dotnet/tools` on
macOS/Linux, `%USERPROFILE%\.dotnet\tools` on Windows) is on your `PATH`.

### Install from a GitHub release

Each release on GitHub has the package `Times.<version>.nupkg` attached. Download it into an
empty folder and install it from there, pinning the version you downloaded:

```sh
dotnet tool install --global --add-source <folder> --version <version> Times
```

For example, with `Times.1.2.3.nupkg` in `~/Downloads/times`:

```sh
dotnet tool install --global --add-source ~/Downloads/times --version 1.2.3 Times
```

Run this from outside the repo, and always pass `--version` along with `--add-source`. The
folder is added next to your usual feeds (nuget.org included) instead of replacing them, so if a
package named `Times` ever appeared on nuget.org, a version you did not download could be picked.
Check the result with `tm --version`. To move to a newer release, uninstall first (see [Uninstall](#uninstall)),
then install the new file the same way.

### Update

After changing the code, raise `<Version>` in `src/Times/Times.csproj` (with the same version,
.NET may reuse the build it already cached instead of your new one), build the package again,
then update, again from the repo root:

```sh
dotnet pack src/Times -c Release
dotnet tool update -g Times
```

### Uninstall

```sh
dotnet tool uninstall -g Times
```

Uninstalling removes only the tool. Your data stays in the data folder that `help` shows.

## Shortcuts

Every command has a short alias that does exactly the same, with the same arguments: `a 8:15`
books the same arrival as `arrive 8:15`. `help` lists them in its `Alias` column, and the
command popup shows them next to each name (typing an alias also finds its command there).

| Command | Alias | | Command | Alias |
|---------|-------|-|---------|-------|
| `arrive` | `a` | | `prev` | `p` |
| `leave` | `l` | | `next` | `n` |
| `break` | `b` | | `today` | `t` |
| `breakend` | `be` | | `addproject` | `ap` |
| `checkpoint` | `cp` | | `projects` | `pr` |
| `remove` | `r` | | `help` | `h` |
| `change` | `c` | | `quit` | `q` |

Tab still completes full command names only.

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

## Releasing

Releases are built by GitHub Actions (`.github/workflows/release.yml`); nobody edits `<Version>`
for a release.

**Develop builds.** Every push to `develop` makes a GitHub prerelease for testers, versioned
`X.Y.(Z+1)-develop.<run>`: one patch above the highest release tag, plus the workflow run number.
After `v0.1.0` (or `v0.1.0-beta`), run 12 gives `0.1.1-develop.12`, tagged `v0.1.1-develop.12`,
with `Times.0.1.1-develop.12.nupkg` attached. These sort above the latest release and below the
next one, so nobody tags them by hand.

**Releases.** Open a pull request from `develop` into `main` titled `release v1.2.3` and merge it:

```sh
gh pr create --base main --head develop --title "release v1.2.3"
```

When the pull request is merged, the workflow runs the tests on the merge commit, packs Times with
version `1.2.3` (taken from the title, without `release v`), installs the package once to check that
`tm --version` prints `Times 1.2.3`, and creates the GitHub release `v1.2.3` (and its tag) on the merge
commit, with `Times.1.2.3.nupkg` attached and generated notes. A title with a pre-release part, such
as `release v1.2.3-beta.1`, makes a pre-release.
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
