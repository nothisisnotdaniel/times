# 009 Fall back per setting

As a Times user, I want one unknown value in settings.json to fall back for that setting only, so that a single typo doesn't reset all my settings.

## What

An unknown state (or other unknown option value) in settings.json should fall back for that one setting (nationwide holidays) instead of making the whole file count as broken (115 review-ux 1)

## Why

Today an unknown state for public holidays makes the whole file count as broken, which throws away every other setting. Job 115's UX reviewer noted it.

## Notes

Proposed by job 115-public-holidays (`factory/jobs/115-public-holidays/`).
