# Times

Times is a terminal app for tracking the time you spend on projects. You check in, check out,
book breaks and add checkpoints that are later transferred into Azure DevOps work items. It runs
as an interactive session: the current day sits above an input box where you type commands
(type `help` to list them, `quit` to leave). A leading `/` is still accepted, so typing one in
front of a command does the same as the command on its own.

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

It needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). To run it straight from the
source tree:

```sh
dotnet run --project src/Times
```

## Mouse

The mouse wheel scrolls the area above the input box, 3 lines per notch, wherever the pointer is
(PageUp/PageDown, or Ctrl+↑/↓ in macOS Terminal, scroll by a page).

To make that work, Times turns on the terminal's mouse reporting while it runs. Because of that,
dragging with the mouse no longer selects text: while Times runs, select text with **Shift-drag**
(**Option-drag** in macOS Terminal). The terminal's normal mouse behaviour comes back when Times
exits, also after Ctrl+C.

## Install as a .NET tool

You can install Times once as a .NET global tool and then start it from any terminal.

The command is **`tm`**, not `times`: `times` is a built-in command of bash and zsh, so the shell
would run its own `times` and never reach the app.

The package is not published to nuget.org or any other feed. You build it yourself into the
local `nupkg/` folder at the repo root and install it from there.

1. Build the package (from the repo root). This writes `nupkg/Times.<version>.nupkg`:

   ```sh
   dotnet pack src/Times -c Release
   ```

2. Install it from the local folder:

   ```sh
   dotnet tool install -g --add-source ./nupkg Times
   ```

   `--add-source` adds `./nupkg` next to your configured feeds; it does not replace them. Always
   build first, and to make it much more likely you get the package you just built, pin the version, for example
   `dotnet tool install -g --add-source ./nupkg Times --version 0.1.0`.

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

### Update

After changing the code, raise `<Version>` in `src/Times/Times.csproj` (with the same version,
.NET may reuse the build it already cached instead of your new one), build the package again,
then update, pinning the new version so a higher-versioned `Times` package from another feed is not picked up:

```sh
dotnet pack src/Times -c Release
dotnet tool update -g --add-source ./nupkg Times --version <new version>
```

### Uninstall

```sh
dotnet tool uninstall -g Times
```

Uninstalling removes only the tool. Your data stays in the data folder that `help` shows.
