# 080 Teams token file read without following links

As a Times user, I want teams.json read without ever following a link, so that a file swapped in between the check and the open can never be read as my pairing token.

## What

TeamsPairing.Read checks teams.json with lstat (FileKind.OfEntry), then CappedFile.Read opens it and follows a link at open time. Add an O_NOFOLLOW variant of CappedFile (or an option) and use it in TeamsPairing.Read, so the check and the open look at the same entry.

## Why

Job 327 security review nit 1: swapping teams.json for a link between the two calls could make a linked file get read. CappedFile still checks for a regular file and caps the size, so the impact is small, but the gap stays open until the shared file code supports no-follow opens.

## Notes

Proposed by job 327-teams-call-tracking (`factory/jobs/327-teams-call-tracking/`).
