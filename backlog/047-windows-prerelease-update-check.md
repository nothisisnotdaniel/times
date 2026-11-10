# 047 Windows prerelease update check

As the Times maintainer, I want to confirm on a real Windows machine that a prerelease update installs, so that GitHub issue #1 can be closed with confidence.

## What

- On Windows, with the develop or beta channel set, let Times stage and apply the newest -develop prerelease (built from job 127-prerelease-update-fix).
- Check that the update helper no longer logs `update failed` and that `times --version` shows the new version.
- If it works, close GitHub issue nothisisnotdaniel/times#1 with a short note; if not, add a backlog story with the helper log.

## Why

Job 127 removed `--prerelease` from the update command line, but no agent can run a real `dotnet tool update` on Windows. Until someone checks it there, the fix for issue #1 is only proven by tests.

## Notes

Proposed by job 127-prerelease-update-fix (build.md and decision.md).
