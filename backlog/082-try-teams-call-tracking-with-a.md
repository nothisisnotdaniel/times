# 082 Try Teams call tracking with a real Teams

As the Times owner, I want to try Teams call tracking with a real Teams once, so that the assumptions it was built on are confirmed.

## What

With the new Teams app installed, pair Times with the teams command and check: 1:1 calls set isInMeeting and book a Call; the pairing request toggles nothing (no mute, no video); whether Teams sends a meeting title. Fix any differing constant in TeamsProtocol.

## Why

Job 327 was built on the documented local API (protocol 2.0.0) because Teams was not installed on the build machine; the approver escalated it because the client was never tried against a real Teams.

## Notes

From job 327-teams-call-tracking (factory/jobs/327-teams-call-tracking/).
