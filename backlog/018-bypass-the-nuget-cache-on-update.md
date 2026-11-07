# 018 Bypass the NuGet cache on update

As a Times user, I want installing a staged update to bypass the local NuGet cache, so that I really get the new package and not a stale cached copy.

## What

--no-cache to bypass ~/.nuget/packages when installing the staged update; may break across SDKs, needs its own job

## Why

A cached package with the same version can shadow the staged update. Using `--no-cache` may behave differently across SDKs, so job 093 left it for its own job.

## Notes

Proposed by job 093-auto-update (`factory/jobs/093-auto-update/`).
