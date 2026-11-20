# 078 Keep slash-only first word token lines out of history

As a Times user, I want a line like `// devops connect x <token>` (a space after the slashes) kept out of the in-memory history, so that a token typed that way never shows up when I press ↑.

## What

Extend the history filter from job 321 so a line whose first word is only slashes, followed by a devops or pro command with a secret, is also kept out of history; flip the predicate test that pins it as entering history.

## Why

Job 321 security review: such a line still enters history with its token, because the filter only looks at the first word. Without this, a common typo still leaks the token to ↑ recall.

## Notes

Proposed by job 321-token-lines-out-history (`factory/jobs/321-token-lines-out-history/`).
