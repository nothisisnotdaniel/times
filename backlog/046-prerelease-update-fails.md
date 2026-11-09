# 046 Prerelease update fails

As a Times user on the beta or develop channel, I want a staged prerelease update to install, so that the update channel I picked actually updates.

## What

- `UpdateApplier.UpdateArguments` no longer adds `--prerelease`: the exact `--version` already names the prerelease, and `dotnet tool update` refuses the two options together ("The --prerelease and --version options are not supported in the same command").
- Update `UpdateApplierTests` (the AC-9 argument list and the Windows helper command line) and `AppTests` (the check that the last argument is `--prerelease`) so they assert the new arguments, and add a test that a prerelease update never passes `--prerelease`.
- The release workflow's `$PRERELEASE` for `gh release create` stays as it is; it is a different command.
- Check by hand on Windows that `dotnet tool update Times -g --configfile <staged>/nuget.config --add-source <staged> --version <x.y.z-develop.n>` installs the staged prerelease.

## Why

GitHub issue nothisisnotdaniel/times#1: on Windows, updating to 0.1.1-develop.76 fails with that error and the helper logs `update failed`. Every prerelease update builds this command line, so nobody on the beta or develop channel can update at all.
