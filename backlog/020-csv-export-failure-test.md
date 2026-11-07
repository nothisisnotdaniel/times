# 020 CSV export failure test

As a maintainer, I want a test that a partial CSV is deleted when writing fails halfway, so that this cleanup can't break unnoticed.

## What

Add an injectable stream factory so a test can check that a partial CSV file is deleted after a mid-write failure

## Why

The cleanup exists but can't be tested without a way to inject a failing stream. Job 100's code and security reviews noted it.

## Notes

From 100 code/security review. Proposed by job 100-csv-export (`factory/jobs/100-csv-export/`).
