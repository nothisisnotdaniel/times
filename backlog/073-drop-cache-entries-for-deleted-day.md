# 073 Drop cache entries for deleted day files

As the user, I want Times to forget day files that are gone, so that its memory stays tied to what is on disk.

## What

DayRecordCache (Times/src/Times/DayRecordCache.cs) keeps an entry for every day file it has loaded, also after the file is deleted or renamed, because a missing day is no longer loaded through the cache. Prune entries whose day was not in the last Days() listing when the balance is computed.

## Why

Growth is bounded by the day files seen in one session, so this is tidiness rather than a risk, but memory should follow what is on disk.

## Notes

Proposed by job 314-safer-day-file-loading (review-security 1).

Proposed by job 314-safer-day-file-loading (`factory/jobs/314-safer-day-file-loading/`).
