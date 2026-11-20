# 055 Mistyped pro command kept out of history

As a Pro buyer, I want a mistyped `pro activate <key>` line to stay out of the command history, so that my licence key is not kept where I didn't expect it.

## What

Extend IsProLineWithKey (and the DevOps token check) to treat a line whose second word is `activate` followed by a key-like token as secret even when the command name is mistyped, and leave it out of the in-memory history.

## Why

The security review of job 302 noted that `prp activate <key>` lands in history; history is in memory only, so it is minor.

## Notes

Proposed by job 302-signed-licence-keys.

Proposed by job 302-signed-licence-keys (`factory/jobs/302-signed-licence-keys/`).
