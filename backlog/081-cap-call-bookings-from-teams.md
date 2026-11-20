# 081 Cap Call bookings from Teams

As a Times user, I want a limit on how many Call checkpoints Teams events can book, so that a misbehaving or fake Teams endpoint cannot flood my day file.

## What

Limit the Call bookings TeamsTracker makes from meetingUpdate toggles, for example per minute and per day; past the limit, ignore further toggles and show a grey hint once.

## Why

Job 327 security review nit 2: any local process that binds 127.0.0.1:8124 while Teams is not running gets the token and can send start/end toggles with no rate limit, and each pair adds two entries to the day file.

## Notes

Proposed by job 327-teams-call-tracking (`factory/jobs/327-teams-call-tracking/`).
