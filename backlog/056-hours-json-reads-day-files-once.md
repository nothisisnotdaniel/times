# 056 Hours JSON reads day files once

As a script author, I want `report --json` to build `days` and `workItems` from the same loaded day files, so that the two parts of one output always agree.

## What

Let ProjectReport.Calculate take day records that are already loaded (keep the current overload for other callers), and have HoursJson.Build load the days once and pass them to both parts. Add a test that both parts come from one read.

## Why

The code review of job 303 noted that HoursJson loads the day files separately from ProjectReport.Calculate, so a file changing between the two reads can appear in one part and not the other.

## Notes

Proposed by job 303-work-item-hours-json.

Proposed by job 303-work-item-hours-json (`factory/jobs/303-work-item-hours-json/`).
