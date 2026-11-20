# 053 Pack test builds on its own

As a developer, I want PackedTimes_HoldsNoIssuerFile to work without a prior build, so that running it alone never fails for the wrong reason.

## What

Make the test pack without `--no-build` (or build first itself) so it does not depend on `dotnet test` having built the output already.

## Why

The code review of job 302 noted the test relies on a previous build; run alone it can fail or check stale output.

## Notes

Proposed by job 302-signed-licence-keys.

Proposed by job 302-signed-licence-keys (`factory/jobs/302-signed-licence-keys/`).
