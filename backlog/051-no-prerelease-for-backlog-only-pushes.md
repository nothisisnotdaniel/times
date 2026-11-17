# 051 No prerelease for backlog-only pushes

As the Times maintainer, I want pushes that only change the backlog or its user stories to build no prerelease, so that the releases list holds real builds only and no runner time is spent on text edits.

## What

In .github/workflows/release.yml, the develop push trigger already skips a push that changes only the root backlog.md (paths-ignore). Extend it so a push whose changes are all in backlog.md and/or backlog/** (the numbered user stories) makes no prerelease either. Any push that also touches anything else still builds as today; version tags and the release pull request behaviour stay unchanged (check that the paths filter never skips a pushed vX.Y.Z tag). Update the comment at the top of release.yml (and CLAUDE.md, if it describes this) to match.

## Why

Every backlog addition or story edit currently makes a develop prerelease because only backlog.md is ignored, not the story files under backlog/.

## Notes

Requested by the user on 2026-11-17; same change as Context#256. Times has no separate CI workflow, so nothing else needs to keep running. Check after merge with a backlog-only push: no Release run.
